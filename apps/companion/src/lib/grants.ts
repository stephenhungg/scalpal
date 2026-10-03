// Client side of the grant pattern: insert a request row through a reducer,
// then wait for the gateway to fill it in (visible only to us).

import type { DbConnection } from '../module_bindings';
import { uid } from './ids';

export async function waitFor<T>(
  read: () => T | null | undefined | false,
  timeoutMs = 20_000,
  intervalMs = 50
): Promise<T> {
  const start = Date.now();
  for (;;) {
    const v = read();
    if (v) return v;
    if (Date.now() - start > timeoutMs) throw new Error('timed out waiting for the gateway');
    await new Promise(r => setTimeout(r, intervalMs));
  }
}

export type IceConfig = { iceServers: RTCIceServer[]; relay: boolean };

const FALLBACK_ICE: IceConfig = {
  iceServers: [{ urls: ['stun:stun.cloudflare.com:3478', 'stun:stun.l.google.com:19302'] }],
  relay: false,
};

/** TURN/STUN servers from the gateway; falls back to public STUN if it is down. */
export async function requestIce(conn: DbConnection, sessionId: string): Promise<IceConfig> {
  const grantId = uid('grant');
  try {
    await conn.reducers.requestServiceGrant({ grantId, sessionId, kind: 'ice' });
    const g = await waitFor(
      () => {
        for (const row of conn.db.myServiceGrants.iter()) {
          if (row.grantId === grantId && row.status !== 'requested') return row;
        }
        return null;
      },
      8000
    );
    if (g.status === 'issued' && g.payload) return JSON.parse(g.payload) as IceConfig;
  } catch (err) {
    console.warn('ICE grant unavailable, using public STUN only', err);
  }
  return FALLBACK_ICE;
}

async function awaitTransferGrant(conn: DbConnection, grantId: string) {
  const g = await waitFor(() => {
    for (const row of conn.db.myTransferGrants.iter()) {
      if (row.grantId === grantId && row.status !== 'requested') return row;
    }
    return null;
  });
  if (g.status !== 'issued' || !g.url) throw new Error(g.reason ?? `grant ${g.status}`);
  return g;
}

export async function downloadUrl(conn: DbConnection, artifactId: string): Promise<string> {
  const grantId = uid('grant');
  await conn.reducers.requestDownload({ grantId, artifactId });
  return (await awaitTransferGrant(conn, grantId)).url!;
}

async function sha256Hex(file: Blob): Promise<string | undefined> {
  if (file.size > 256 * 1024 * 1024 || !crypto.subtle) return undefined;
  const digest = await crypto.subtle.digest('SHA-256', await file.arrayBuffer());
  return Array.from(new Uint8Array(digest), b => b.toString(16).padStart(2, '0')).join('');
}

/** Upload through a signed URL with progress; returns the artifact id. */
export async function uploadFile(
  conn: DbConnection,
  args: { sessionId: string; attemptId: string; kind: string; file: File },
  onProgress?: (fraction: number) => void
): Promise<string> {
  const artifactId = uid('art');
  const grantId = uid('grant');
  const contentType = args.file.type || 'application/octet-stream';
  const sha256 = await sha256Hex(args.file);
  await conn.reducers.requestUpload({
    grantId,
    artifactId,
    sessionId: args.sessionId,
    attemptId: args.attemptId,
    kind: args.kind,
    filename: args.file.name,
    contentType,
    declaredBytes: BigInt(args.file.size),
    sha256,
  });
  const grant = await awaitTransferGrant(conn, grantId);
  await new Promise<void>((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open(grant.method ?? 'PUT', grant.url!);
    xhr.setRequestHeader('content-type', contentType);
    xhr.upload.onprogress = e => e.lengthComputable && onProgress?.(e.loaded / e.total);
    xhr.onload = () => (xhr.status >= 200 && xhr.status < 300 ? resolve() : reject(new Error(`upload failed: ${xhr.status}`)));
    xhr.onerror = () => reject(new Error('upload failed: network error'));
    xhr.send(args.file);
  });
  await conn.reducers.markUploaded({ artifactId });
  return artifactId;
}

/**
 * Join with an invite code and resolve the session it belongs to. Codes are
 * not visible to non-operators, so watch for the new membership row; if the
 * code was already used (no new row), fall back to the latest membership.
 */
export async function joinWithCode(conn: DbConnection, code: string, displayName: string): Promise<string> {
  const before = new Set([...conn.db.myMemberships.iter()].map(m => String(m.membershipId)));
  await conn.reducers.joinSession({ code: code.trim().toUpperCase(), displayName });
  try {
    const m = await waitFor(
      () => [...conn.db.myMemberships.iter()].find(m => !before.has(String(m.membershipId))),
      3000
    );
    return m.sessionId;
  } catch {
    const latest = [...conn.db.myMemberships.iter()].sort(
      (a, b) => Number(b.joinedAt.microsSinceUnixEpoch - a.joinedAt.microsSinceUnixEpoch)
    )[0];
    if (!latest) throw new Error('Joined, but the session did not appear. Reload and try again.');
    return latest.sessionId;
  }
}
