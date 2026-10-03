// The gateway's own SpacetimeDB connection. It runs as a service identity,
// subscribes to every view, and reconnects with backoff when dropped.

import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import type { Identity } from 'spacetimedb';
import { DbConnection, tables } from './module_bindings/index';
import type { Config } from './config';
import { log } from './log';

export type Conn = DbConnection;

export class Realtime {
  #config: Config['spacetime'];
  #conn: DbConnection | null = null;
  #identity: Identity | null = null;
  #ready = false;
  #listeners = new Set<(conn: DbConnection) => void>();
  #retryMs = 1000;
  #stopped = false;

  constructor(config: Config['spacetime']) {
    this.#config = config;
  }

  get connection(): DbConnection | null {
    return this.#ready ? this.#conn : null;
  }

  get identityHex(): string | null {
    return this.#identity?.toHexString() ?? null;
  }

  get isReady() {
    return this.#ready;
  }

  /** True when this connection's identity is registered as a service. */
  get isService(): boolean {
    const conn = this.connection;
    if (!conn) return false;
    for (const _ of conn.db.amService.iter()) return true;
    return false;
  }

  /** The connected connection, or throws 503-style error. */
  require(): DbConnection {
    const conn = this.connection;
    if (!conn) throw new Unavailable('realtime database not connected');
    return conn;
  }

  /** Called on every (re)subscription so watchers can reattach callbacks. */
  onReady(fn: (conn: DbConnection) => void) {
    this.#listeners.add(fn);
    if (this.#ready && this.#conn) fn(this.#conn);
  }

  start() {
    this.#connect();
  }

  stop() {
    this.#stopped = true;
    this.#conn?.disconnect();
  }

  #loadToken(): string | undefined {
    if (this.#config.token) return this.#config.token;
    if (existsSync(this.#config.tokenFile)) {
      return readFileSync(this.#config.tokenFile, 'utf8').trim() || undefined;
    }
    return undefined;
  }

  #saveToken(token: string) {
    if (this.#config.token) return;
    try {
      writeFileSync(this.#config.tokenFile, token, { mode: 0o600 });
    } catch (err) {
      log.warn('could not persist gateway token', { err: String(err) });
    }
  }

  #connect() {
    if (this.#stopped) return;
    log.info('connecting to SpacetimeDB', { uri: this.#config.uri, database: this.#config.database });
    const conn = DbConnection.builder()
      .withUri(this.#config.uri)
      .withDatabaseName(this.#config.database)
      .withToken(this.#loadToken())
      .onConnect((c, identity, token) => {
        this.#identity = identity;
        this.#saveToken(token);
        this.#retryMs = 1000;
        c.subscriptionBuilder()
          .onApplied(() => {
            this.#ready = true;
            log.info('subscribed', { identity: identity.toHexString(), service: this.isService });
            if (!this.isService) {
              log.warn(
                'gateway identity is not a service identity; grants and jobs will be refused. ' +
                  `Register it with: spacetime call ${this.#config.database} add_service_identity ` +
                  `'"0x${identity.toHexString()}"' '"gateway"'`
              );
            }
            for (const fn of this.#listeners) fn(c);
          })
          .onError(ctx => log.error('subscription error', { err: String(ctx.event) }))
          .subscribe([
            tables.amService,
            tables.mySessions,
            tables.sessionArtifacts,
            tables.sessionMotionJobs,
            tables.myTransferGrants,
            tables.myServiceGrants,
          ]);
      })
      .onDisconnect((_ctx, err) => {
        this.#ready = false;
        log.warn('disconnected from SpacetimeDB', { err: err ? String(err) : undefined });
        this.#scheduleReconnect();
      })
      .onConnectError((_ctx, err) => {
        this.#ready = false;
        log.error('SpacetimeDB connect error', { err: String(err) });
        this.#scheduleReconnect();
      })
      .build();
    this.#conn = conn;
  }

  #scheduleReconnect() {
    if (this.#stopped) return;
    const delay = this.#retryMs;
    this.#retryMs = Math.min(this.#retryMs * 2, 30_000);
    setTimeout(() => this.#connect(), delay);
  }
}

export class Unavailable extends Error {}

/** Poll the local cache until `predicate` holds (reducer effects are async). */
export async function waitFor<T>(
  read: () => T | undefined | null | false,
  timeoutMs = 5000,
  intervalMs = 20
): Promise<T> {
  const start = Date.now();
  for (;;) {
    const v = read();
    if (v) return v;
    if (Date.now() - start > timeoutMs) throw new Error('timed out waiting for realtime state');
    await new Promise(r => setTimeout(r, intervalMs));
  }
}
