// Watches the realtime state and does the work reducers cannot (network and
// storage I/O):
//
// - transfer grants: mint signed upload/download URLs
// - service grants:  mint voice-session and TURN credentials
// - artifacts:       verify uploads ('verifying' -> available/failed) and
//                    remove objects for 'deleted' artifacts
//
// Everything is driven from persisted rows, so a gateway restart simply
// picks up whatever is still outstanding.

import type { Config } from './config';
import { log } from './log';
import { mintIceServers, mintVoiceSession, NotConfigured } from './providers';
import type { Realtime } from './realtime';
import type { Storage } from './storage';

export class Reconciler {
  #inFlight = new Set<string>();
  #deleted = new Set<string>();
  #timer: NodeJS.Timeout | null = null;
  #scheduled = false;

  constructor(
    private rt: Realtime,
    private storage: Storage,
    private config: Config
  ) {}

  start() {
    this.rt.onReady(conn => {
      const kick = () => this.kick();
      conn.db.myTransferGrants.onInsert(kick);
      conn.db.myServiceGrants.onInsert(kick);
      conn.db.sessionArtifacts.onInsert(kick);
      this.kick();
    });
    // Safety net for anything missed between reconnects.
    this.#timer = setInterval(() => this.kick(), 2000);
  }

  stop() {
    if (this.#timer) clearInterval(this.#timer);
  }

  kick() {
    if (this.#scheduled) return;
    this.#scheduled = true;
    queueMicrotask(() => {
      this.#scheduled = false;
      this.#pass();
    });
  }

  #once(key: string, work: () => Promise<void>) {
    if (this.#inFlight.has(key)) return;
    this.#inFlight.add(key);
    work()
      .catch(err => log.error('reconcile step failed', { key, err: String(err?.message ?? err) }))
      .finally(() => this.#inFlight.delete(key));
  }

  #pass() {
    const conn = this.rt.connection;
    if (!conn || !this.rt.isService) return;

    for (const g of conn.db.myTransferGrants.iter()) {
      if (g.status !== 'requested') continue;
      this.#once(`tg:${g.grantId}`, async () => {
        const art = [...conn.db.sessionArtifacts.iter()].find(a => a.artifactId === g.artifactId);
        if (!art) {
          await conn.reducers.denyGrant({ grantId: g.grantId, reason: 'unknown artifact' });
          return;
        }
        try {
          const ttl =
            g.direction === 'upload' ? this.config.storage.uploadTtlMs : this.config.storage.downloadTtlMs;
          const signed =
            g.direction === 'upload'
              ? await this.storage.presignPut(art.storageKey, art.contentType, ttl)
              : await this.storage.presignGet(art.storageKey, ttl, art.filename);
          await conn.reducers.issueGrant({
            grantId: g.grantId,
            url: signed.url,
            method: signed.method,
            ttlMs: ttl,
          });
          log.info('issued transfer grant', { grantId: g.grantId, direction: g.direction, artifactId: art.artifactId });
        } catch (err) {
          await conn.reducers.denyGrant({ grantId: g.grantId, reason: 'could not sign storage URL' });
          throw err;
        }
      });
    }

    for (const g of conn.db.myServiceGrants.iter()) {
      if (g.status !== 'requested') continue;
      this.#once(`sg:${g.grantId}`, async () => {
        try {
          if (g.kind === 'voice') {
            const payload = await mintVoiceSession(this.config.voice);
            await conn.reducers.issueServiceGrant({
              grantId: g.grantId,
              payload: JSON.stringify(payload),
              ttlMs: this.config.voice.ttlMs,
            });
          } else if (g.kind === 'ice') {
            const payload = await mintIceServers(this.config.ice);
            await conn.reducers.issueServiceGrant({
              grantId: g.grantId,
              payload: JSON.stringify(payload),
              ttlMs: this.config.ice.ttlSeconds * 1000,
            });
          } else {
            await conn.reducers.denyServiceGrant({ grantId: g.grantId, reason: `unsupported kind ${g.kind}` });
            return;
          }
          log.info('issued service grant', { grantId: g.grantId, kind: g.kind });
        } catch (err) {
          const reason = err instanceof NotConfigured ? err.message : `${g.kind} provider request failed`;
          await conn.reducers.denyServiceGrant({ grantId: g.grantId, reason });
          if (!(err instanceof NotConfigured)) throw err;
        }
      });
    }

    for (const art of conn.db.sessionArtifacts.iter()) {
      if (art.status === 'verifying') {
        this.#once(`av:${art.artifactId}`, () => this.#verify(art));
      } else if (art.status === 'deleted' && !this.#deleted.has(art.artifactId)) {
        this.#once(`ad:${art.artifactId}`, async () => {
          await this.storage.delete(art.storageKey);
          this.#deleted.add(art.artifactId);
          log.info('deleted stored object', { artifactId: art.artifactId });
        });
      }
    }
  }

  async #verify(art: {
    artifactId: string;
    storageKey: string;
    declaredBytes: bigint | undefined;
    sha256: string | undefined;
  }) {
    const conn = this.rt.require();
    const size = await this.storage.head(art.storageKey);
    let ok = size != null;
    let reason: string | undefined = ok ? undefined : 'object not found in storage';
    if (ok && art.declaredBytes != null && BigInt(size!) !== art.declaredBytes) {
      ok = false;
      reason = `size mismatch: declared ${art.declaredBytes}, stored ${size}`;
    }
    if (ok && art.sha256) {
      if (size! <= this.config.storage.hashVerifyMaxBytes) {
        const actual = await this.storage.sha256(art.storageKey);
        if (actual !== art.sha256) {
          ok = false;
          reason = 'sha256 mismatch';
        }
      } else {
        reason = 'sha256 not verified (object larger than HASH_VERIFY_MAX_BYTES)';
      }
    }
    await conn.reducers.confirmArtifact({
      artifactId: art.artifactId,
      ok,
      verifiedBytes: size != null ? BigInt(size) : undefined,
      reason,
    });
    log.info('verified artifact', { artifactId: art.artifactId, ok, reason });
  }
}
