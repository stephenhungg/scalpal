import { useEffect, useMemo, useRef, useState } from 'react';
import { useLive, type SessionData } from '../data/live';
import { ago } from '../lib/format';
import { requestIce, type IceConfig } from '../lib/grants';
import { Publisher, SignalInbox, Viewer, type PeerStats } from '../lib/rtc';
import { Panel, Pill, StatusPill, type Tone } from './ui';

type ViewerState = 'idle' | 'connecting' | 'connected' | 'reconnecting' | 'failed';

const VIEWER_TONE: Record<ViewerState, Tone> = {
  idle: 'muted',
  connecting: 'info',
  connected: 'ok',
  reconnecting: 'warn',
  failed: 'bad',
};

function Video({ stream }: { stream: MediaStream | null }) {
  const ref = useRef<HTMLVideoElement>(null);
  useEffect(() => {
    if (ref.current) ref.current.srcObject = stream;
  }, [stream]);
  return <video ref={ref} autoPlay playsInline muted />;
}

function StatsLine({ s }: { s: PeerStats }) {
  return (
    <>
      {s.width && s.height ? <Pill tone="muted" dot={false}>{s.width}×{s.height}</Pill> : null}
      {s.fps != null ? <Pill tone="muted" dot={false}>{Math.round(s.fps)} fps</Pill> : null}
      {s.bitrateKbps != null ? <Pill tone="muted" dot={false}>{(s.bitrateKbps / 1000).toFixed(1)} Mbps</Pill> : null}
      {s.rttMs != null ? <Pill tone="muted" dot={false}>RTT {s.rttMs} ms</Pill> : null}
      {s.relayed != null ? <Pill tone="muted" dot={false}>{s.relayed ? 'via TURN relay' : 'direct'}</Pill> : null}
    </>
  );
}

export default function LiveView({ data }: { data: SessionData }) {
  const { conn } = useLive();
  const { media, session } = data;
  const sessionId = session!.sessionId;
  const [ice, setIce] = useState<IceConfig | null>(null);
  const [stream, setStream] = useState<MediaStream | null>(null);

  // Viewer side
  const [vState, setVState] = useState<ViewerState>('idle');
  const [vDetail, setVDetail] = useState<string | undefined>();
  const [vStats, setVStats] = useState<PeerStats | null>(null);
  const viewerRef = useRef<Viewer | null>(null);

  // Publisher side
  const publisherRef = useRef<Publisher | null>(null);
  const [pStatus, setPStatus] = useState<string>('off');
  const [pDetail, setPDetail] = useState<string | undefined>();
  const [peers, setPeers] = useState<PeerStats[]>([]);
  const [localStream, setLocalStream] = useState<MediaStream | null>(null);

  const publisherHex = media?.publisher?.toHexString();
  const iAmPublisher = Boolean(publisherHex && publisherHex === data.me);
  const isPublishing = localStream != null;
  const sourceLive = media?.status === 'live' && media.publisher != null;
  const ended = session?.status !== 'active';

  useEffect(() => {
    if (!conn || ended) return;
    let cancelled = false;
    requestIce(conn, sessionId).then(cfg => !cancelled && setIce(cfg));
    return () => {
      cancelled = true;
    };
  }, [conn, sessionId, ended]);

  // Viewer lifecycle: watch whenever someone else is live.
  useEffect(() => {
    if (!conn || !ice || isPublishing) return;
    const viewer = new Viewer(new SignalInbox(conn, sessionId), ice, {
      onStream: setStream,
      onState: (s, d) => {
        setVState(s);
        setVDetail(d);
      },
      onStats: setVStats,
    });
    viewerRef.current = viewer;
    return () => {
      viewer.dispose();
      viewerRef.current = null;
    };
  }, [conn, ice, sessionId, isPublishing]);

  useEffect(() => {
    const v = viewerRef.current;
    if (!v) return;
    if (sourceLive && !iAmPublisher && media?.publisher) v.connect(media.publisher);
    else v.disconnect();
    // Reconnect only when the publisher or its liveness changes.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sourceLive, publisherHex, iAmPublisher, ice, isPublishing]);

  // Tear the publisher down when leaving the page.
  useEffect(() => () => publisherRef.current?.dispose(), []);

  const startSharing = async () => {
    if (!conn || !ice) return;
    publisherRef.current?.dispose();
    viewerRef.current?.dispose();
    viewerRef.current = null;
    const pub = new Publisher(conn, sessionId, new SignalInbox(conn, sessionId), ice, {
      onStatus: (s, d) => {
        setPStatus(s);
        setPDetail(d);
      },
      onPeers: setPeers,
      onStream: setLocalStream,
    });
    publisherRef.current = pub;
    await pub.start();
  };

  const stopSharing = async () => {
    await publisherRef.current?.stop();
    publisherRef.current?.dispose();
    publisherRef.current = null;
  };

  const shown = isPublishing ? localStream : stream;

  const sourcePill = useMemo(() => {
    if (!media) return null;
    return <StatusPill status={media.status} label={`Source ${media.status}`} />;
  }, [media]);

  let empty: { title: string; body: string } | null = null;
  if (!shown) {
    if (ended) empty = { title: 'Session ended', body: 'The live view is no longer available.' };
    else if (!media || media.status === 'off')
      empty = {
        title: 'No live view yet',
        body: data.isOperator
          ? 'On the Mac showing the Quest mirror, press Share Quest view and choose the mirror window.'
          : 'The operator has not started sharing the headset view.',
      };
    else if (media.status === 'starting') empty = { title: 'Operator is choosing a window…', body: 'The view appears once sharing starts.' };
    else if (media.status === 'denied') empty = { title: 'Sharing was not allowed', body: media.detail ?? 'The operator denied or cancelled screen sharing.' };
    else if (media.status === 'error') empty = { title: 'Sharing failed', body: media.detail ?? 'The publisher reported an error.' };
    else if (media.status === 'stopped') empty = { title: 'Source stopped', body: `${media.detail ?? 'Sharing stopped.'} (${ago(media.updatedAt)})` };
    else if (media.status === 'live')
      empty = {
        title: vState === 'failed' ? 'Cannot reach the video' : 'Connecting to the live view…',
        body: vDetail ?? (iAmPublisher ? 'You are the publisher in another tab or window.' : 'Negotiating a peer connection.'),
      };
  }

  return (
    <Panel
      title="Live headset view"
      flush
      actions={
        <div className="row">
          {sourcePill}
          {data.isOperator && !ended && (
            isPublishing ? (
              <button className="btn sm danger" onClick={stopSharing}>
                Stop sharing
              </button>
            ) : (
              <button
                className="btn sm primary"
                onClick={startSharing}
                disabled={!ice || (sourceLive && !iAmPublisher)}
                title={sourceLive && !iAmPublisher ? 'Another operator is publishing' : 'Share the Quest mirror window'}
              >
                Share Quest view
              </button>
            )
          )}
        </div>
      }
    >
      <div className="stage">
        {shown ? <Video stream={shown} /> : null}
        {empty && (
          <div className="stage-empty">
            <h3>{empty.title}</h3>
            <div>{empty.body}</div>
          </div>
        )}
        <div className="stage-overlay">
          {isPublishing ? (
            <>
              <Pill tone="ok" live>
                Publishing · {peers.filter(p => p.state === 'connected').length} viewer
                {peers.filter(p => p.state === 'connected').length === 1 ? '' : 's'}
              </Pill>
              {peers[0] && <StatsLine s={peers[0]} />}
            </>
          ) : (
            <>
              {media?.status === 'live' && (
                <Pill tone={VIEWER_TONE[vState]} live={vState === 'connected'}>
                  Video {vState}
                </Pill>
              )}
              {vStats && <StatsLine s={vStats} />}
            </>
          )}
          {ice && !ice.relay && <Pill tone="warn" dot={false}>no TURN relay</Pill>}
        </div>
      </div>
      {(isPublishing || pStatus === 'denied' || pStatus === 'error') && pDetail && (
        <div className="panel-body small muted">{pDetail}</div>
      )}
    </Panel>
  );
}
