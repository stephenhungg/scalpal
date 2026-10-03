// Live headset view over WebRTC.
//
// The publisher (operator page on the Mac running the Quest mirror) captures
// one window with getDisplayMedia and serves one RTCPeerConnection per viewer.
// Viewers send a 'join' signal; the publisher answers with an offer. All
// signaling goes through SpacetimeDB rows addressed to one identity; video
// frames travel peer-to-peer (or through TURN), never through the database.

import type { Identity } from 'spacetimedb';
import type { DbConnection } from '../module_bindings';
import type { IceConfig } from './grants';
import { uid } from './ids';

type SignalRow = {
  signalId: bigint;
  sessionId: string;
  from: Identity;
  to: Identity;
  peerId: string;
  kind: string;
  payload: string;
  createdAt: { microsSinceUnixEpoch: bigint };
};

const STALE_SIGNAL_MS = 20_000;
const MAX_BITRATE = 4_000_000;

/** Delivers inbound signals for one session to a handler and acks them. */
export class SignalInbox {
  #seen = new Set<string>();
  #pendingAcks: bigint[] = [];
  #ackTimer: number | null = null;
  #handler: ((s: SignalRow) => void) | null = null;
  #off: () => void;

  constructor(
    private conn: DbConnection,
    private sessionId: string
  ) {
    const onInsert = (_ctx: unknown, row: SignalRow) => this.#deliver(row);
    conn.db.myRtcSignals.onInsert(onInsert as any);
    this.#off = () => conn.db.myRtcSignals.removeOnInsert(onInsert as any);
  }

  setHandler(fn: (s: SignalRow) => void) {
    this.#handler = fn;
    for (const row of this.conn.db.myRtcSignals.iter()) this.#deliver(row as SignalRow);
  }

  #deliver(row: SignalRow) {
    if (row.sessionId !== this.sessionId || !this.#handler) return;
    const key = String(row.signalId);
    if (this.#seen.has(key)) return;
    this.#seen.add(key);
    this.#pendingAcks.push(row.signalId);
    this.#scheduleAck();
    const age = Date.now() - Number(row.createdAt.microsSinceUnixEpoch / 1000n);
    if (age > STALE_SIGNAL_MS) return;
    this.#handler(row);
  }

  #scheduleAck() {
    if (this.#ackTimer != null) return;
    this.#ackTimer = window.setTimeout(() => {
      this.#ackTimer = null;
      const ids = this.#pendingAcks.splice(0);
      if (ids.length) this.conn.reducers.ackSignals({ signalIds: ids }).catch(() => {});
    }, 250);
  }

  send(to: Identity, peerId: string, kind: string, payload: unknown) {
    return this.conn.reducers.sendSignal({
      sessionId: this.sessionId,
      to,
      peerId,
      kind,
      payload: JSON.stringify(payload ?? {}),
    });
  }

  dispose() {
    this.#off();
    this.#handler = null;
  }
}

export type PeerStats = {
  peerId: string;
  state: RTCPeerConnectionState;
  bitrateKbps?: number;
  fps?: number;
  width?: number;
  height?: number;
  rttMs?: number;
  relayed?: boolean;
};

async function readStats(pc: RTCPeerConnection, prev: Map<string, { bytes: number; t: number }>, key: string, inbound: boolean) {
  const report = await pc.getStats();
  const out: Omit<PeerStats, 'peerId' | 'state'> = {};
  let selectedPair: any;
  const byId = new Map<string, any>();
  report.forEach(s => byId.set(s.id, s));
  report.forEach((s: any) => {
    if ((inbound && s.type === 'inbound-rtp' && s.kind === 'video') || (!inbound && s.type === 'outbound-rtp' && s.kind === 'video')) {
      const bytesNow = inbound ? s.bytesReceived : s.bytesSent;
      const last = prev.get(key);
      if (last && s.timestamp > last.t) {
        out.bitrateKbps = Math.round(((bytesNow - last.bytes) * 8) / (s.timestamp - last.t));
      }
      prev.set(key, { bytes: bytesNow, t: s.timestamp });
      out.fps = s.framesPerSecond;
      out.width = s.frameWidth;
      out.height = s.frameHeight;
    }
    if (s.type === 'transport' && s.selectedCandidatePairId) selectedPair = byId.get(s.selectedCandidatePairId);
    if (!selectedPair && s.type === 'candidate-pair' && s.nominated && s.state === 'succeeded') selectedPair = s;
  });
  if (selectedPair) {
    if (selectedPair.currentRoundTripTime != null) out.rttMs = Math.round(selectedPair.currentRoundTripTime * 1000);
    const local = byId.get(selectedPair.localCandidateId);
    const remote = byId.get(selectedPair.remoteCandidateId);
    out.relayed = local?.candidateType === 'relay' || remote?.candidateType === 'relay';
  }
  return out;
}

// ---------------------------------------------------------------------------
// Publisher
// ---------------------------------------------------------------------------

type PublisherEvents = {
  onStatus(status: 'starting' | 'live' | 'stopped' | 'denied' | 'error', detail?: string): void;
  onPeers(stats: PeerStats[]): void;
  onStream(stream: MediaStream | null): void;
};

export class Publisher {
  #stream: MediaStream | null = null;
  #peers = new Map<string, { pc: RTCPeerConnection; remote: Identity; pendingIce: RTCIceCandidateInit[] }>();
  #statsPrev = new Map<string, { bytes: number; t: number }>();
  #statsTimer: number | null = null;

  constructor(
    private conn: DbConnection,
    private sessionId: string,
    private inbox: SignalInbox,
    private ice: IceConfig,
    private events: PublisherEvents
  ) {
    inbox.setHandler(s => this.#onSignal(s).catch(err => console.warn('publisher signal error', err)));
  }

  get live() {
    return this.#stream != null;
  }

  /** Must be called from a user gesture (browser requirement). */
  async start(label?: string) {
    if (!navigator.mediaDevices?.getDisplayMedia) {
      await this.#report('error', 'This browser cannot share a window (getDisplayMedia unavailable).');
      return;
    }
    await this.#report('starting', 'Choose the Quest mirror window');
    let stream: MediaStream;
    try {
      stream = await navigator.mediaDevices.getDisplayMedia({
        video: { frameRate: { ideal: 30, max: 30 } },
        audio: false,
      });
    } catch (err: any) {
      const denied = err?.name === 'NotAllowedError' || err?.name === 'SecurityError';
      await this.#report(denied ? 'denied' : 'error', denied ? 'Screen sharing was denied or cancelled.' : String(err?.message ?? err));
      return;
    }
    const track = stream.getVideoTracks()[0];
    if ('contentHint' in track) track.contentHint = 'motion';
    track.addEventListener('ended', () => this.stop('Sharing ended from the browser.'));
    this.#stream = stream;
    this.events.onStream(stream);
    const settings = track.getSettings();
    await this.conn.reducers.setMediaSource({
      sessionId: this.sessionId,
      status: 'live',
      label: label || track.label || 'Quest mirror',
      detail: undefined,
      width: settings.width,
      height: settings.height,
    });
    this.events.onStatus('live');
    this.#statsTimer = window.setInterval(() => this.#collectStats(), 2000);
  }

  async stop(detail = 'Stopped by operator.') {
    this.#stream?.getTracks().forEach(t => t.stop());
    this.#stream = null;
    this.events.onStream(null);
    for (const [peerId, p] of this.#peers) {
      this.inbox.send(p.remote, peerId, 'bye', {}).catch(() => {});
      p.pc.close();
    }
    this.#peers.clear();
    this.events.onPeers([]);
    if (this.#statsTimer != null) clearInterval(this.#statsTimer);
    this.#statsTimer = null;
    await this.#report('stopped', detail);
  }

  dispose() {
    if (this.#stream) this.stop('Publisher page closed.').catch(() => {});
    this.inbox.dispose();
  }

  async #report(status: 'starting' | 'live' | 'stopped' | 'denied' | 'error', detail?: string) {
    this.events.onStatus(status, detail);
    try {
      await this.conn.reducers.setMediaSource({
        sessionId: this.sessionId,
        status,
        label: undefined,
        detail,
        width: undefined,
        height: undefined,
      });
    } catch (err) {
      console.warn('setMediaSource failed', err);
    }
  }

  async #onSignal(s: SignalRow) {
    const payload = s.payload ? JSON.parse(s.payload) : {};
    if (s.kind === 'join') {
      if (!this.#stream) return; // viewer will retry when we go live
      await this.#offer(s.peerId, s.from);
      return;
    }
    const peer = this.#peers.get(s.peerId);
    if (!peer) return;
    if (s.kind === 'answer') {
      await peer.pc.setRemoteDescription(payload);
      for (const c of peer.pendingIce.splice(0)) await peer.pc.addIceCandidate(c).catch(() => {});
    } else if (s.kind === 'ice') {
      if (peer.pc.remoteDescription) await peer.pc.addIceCandidate(payload).catch(() => {});
      else peer.pendingIce.push(payload);
    } else if (s.kind === 'bye') {
      peer.pc.close();
      this.#peers.delete(s.peerId);
    }
  }

  async #offer(peerId: string, remote: Identity) {
    this.#peers.get(peerId)?.pc.close();
    const pc = new RTCPeerConnection({ iceServers: this.ice.iceServers });
    const entry = { pc, remote, pendingIce: [] as RTCIceCandidateInit[] };
    this.#peers.set(peerId, entry);
    for (const track of this.#stream!.getTracks()) {
      const sender = pc.addTrack(track, this.#stream!);
      const params = sender.getParameters();
      params.encodings = params.encodings?.length ? params.encodings : [{}];
      params.encodings[0].maxBitrate = MAX_BITRATE;
      sender.setParameters(params).catch(() => {});
    }
    pc.onicecandidate = e => {
      if (e.candidate) this.inbox.send(remote, peerId, 'ice', e.candidate.toJSON()).catch(() => {});
    };
    pc.onconnectionstatechange = () => {
      if (pc.connectionState === 'failed' || pc.connectionState === 'closed') {
        pc.close();
        if (this.#peers.get(peerId)?.pc === pc) this.#peers.delete(peerId);
      }
      this.#collectStats();
    };
    const offer = await pc.createOffer();
    await pc.setLocalDescription(offer);
    await this.inbox.send(remote, peerId, 'offer', pc.localDescription!.toJSON());
  }

  async #collectStats() {
    const out: PeerStats[] = [];
    for (const [peerId, p] of this.#peers) {
      const s = await readStats(p.pc, this.#statsPrev, peerId, false).catch(() => ({}));
      out.push({ peerId, state: p.pc.connectionState, ...s });
    }
    this.events.onPeers(out);
  }
}

// ---------------------------------------------------------------------------
// Viewer
// ---------------------------------------------------------------------------

type ViewerEvents = {
  onStream(stream: MediaStream | null): void;
  onState(state: 'idle' | 'connecting' | 'connected' | 'reconnecting' | 'failed', detail?: string): void;
  onStats(stats: PeerStats | null): void;
};

export class Viewer {
  #pc: RTCPeerConnection | null = null;
  #peerId = '';
  #publisher: Identity | null = null;
  #pendingIce: RTCIceCandidateInit[] = [];
  #retryTimer: number | null = null;
  #offerTimer: number | null = null;
  #statsTimer: number | null = null;
  #statsPrev = new Map<string, { bytes: number; t: number }>();
  #attempt = 0;
  #disposed = false;

  constructor(
    private inbox: SignalInbox,
    private ice: IceConfig,
    private events: ViewerEvents
  ) {
    inbox.setHandler(s => this.#onSignal(s).catch(err => console.warn('viewer signal error', err)));
  }

  /** Connect (or reconnect) to the current publisher. */
  connect(publisher: Identity) {
    if (this.#disposed) return;
    this.#publisher = publisher;
    this.#teardown();
    this.#peerId = uid('peer');
    this.#attempt++;
    this.events.onState(this.#attempt > 1 ? 'reconnecting' : 'connecting');
    this.inbox.send(publisher, this.#peerId, 'join', {}).catch(err => this.#fail(String(err?.message ?? err)));
    // No offer within 8s: the publisher may have restarted; try again.
    this.#offerTimer = window.setTimeout(() => this.#retry('No answer from the publisher.'), 8000);
  }

  disconnect() {
    this.#publisher = null;
    if (this.#retryTimer != null) clearTimeout(this.#retryTimer);
    this.#retryTimer = null;
    this.#teardown();
    this.#attempt = 0;
    this.events.onState('idle');
  }

  dispose() {
    this.#disposed = true;
    this.disconnect();
    this.inbox.dispose();
  }

  #teardown() {
    if (this.#offerTimer != null) clearTimeout(this.#offerTimer);
    if (this.#statsTimer != null) clearInterval(this.#statsTimer);
    this.#offerTimer = null;
    this.#statsTimer = null;
    if (this.#pc && this.#publisher && this.#peerId) {
      this.inbox.send(this.#publisher, this.#peerId, 'bye', {}).catch(() => {});
    }
    this.#pc?.close();
    this.#pc = null;
    this.#pendingIce = [];
    this.events.onStream(null);
    this.events.onStats(null);
  }

  #fail(detail: string) {
    this.events.onState('failed', detail);
    this.#retry(detail);
  }

  #retry(detail: string) {
    if (!this.#publisher || this.#disposed) return;
    const delay = Math.min(1000 * 2 ** Math.min(this.#attempt, 4), 15_000);
    this.events.onState('reconnecting', `${detail} Retrying in ${Math.round(delay / 1000)}s.`);
    if (this.#retryTimer != null) clearTimeout(this.#retryTimer);
    const publisher = this.#publisher;
    this.#retryTimer = window.setTimeout(() => this.connect(publisher), delay);
  }

  async #onSignal(s: SignalRow) {
    if (s.peerId !== this.#peerId) return;
    const payload = s.payload ? JSON.parse(s.payload) : {};
    if (s.kind === 'offer') {
      if (this.#offerTimer != null) clearTimeout(this.#offerTimer);
      this.#offerTimer = null;
      const pc = new RTCPeerConnection({ iceServers: this.ice.iceServers });
      this.#pc = pc;
      const peerId = this.#peerId;
      const publisher = s.from;
      pc.ontrack = e => this.events.onStream(e.streams[0] ?? new MediaStream([e.track]));
      pc.onicecandidate = e => {
        if (e.candidate) this.inbox.send(publisher, peerId, 'ice', e.candidate.toJSON()).catch(() => {});
      };
      pc.onconnectionstatechange = () => {
        if (pc !== this.#pc) return;
        if (pc.connectionState === 'connected') {
          this.#attempt = 0;
          this.events.onState('connected');
        } else if (pc.connectionState === 'failed') {
          this.#fail(this.ice.relay ? 'Connection failed.' : 'Connection failed (no TURN relay configured).');
        } else if (pc.connectionState === 'disconnected') {
          this.events.onState('reconnecting', 'Connection interrupted.');
        }
      };
      await pc.setRemoteDescription(payload);
      for (const c of this.#pendingIce.splice(0)) await pc.addIceCandidate(c).catch(() => {});
      const answer = await pc.createAnswer();
      await pc.setLocalDescription(answer);
      await this.inbox.send(publisher, peerId, 'answer', pc.localDescription!.toJSON());
      this.#statsTimer = window.setInterval(async () => {
        if (!this.#pc) return;
        const st = await readStats(this.#pc, this.#statsPrev, 'in', true).catch(() => ({}));
        this.events.onStats({ peerId, state: this.#pc.connectionState, ...st });
      }, 2000);
    } else if (s.kind === 'ice') {
      if (this.#pc?.remoteDescription) await this.#pc.addIceCandidate(payload).catch(() => {});
      else this.#pendingIce.push(payload);
    } else if (s.kind === 'bye') {
      this.#teardown();
      this.events.onState('idle', 'Publisher stopped sharing.');
    }
  }
}
