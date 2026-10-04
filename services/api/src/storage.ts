// Artifact storage behind short-lived signed URLs.
//
// - `local`: files on the gateway's disk, served by the gateway's own
//   /files route with HMAC-signed URLs. For development and single-machine
//   demos.
// - `s3`: any S3-compatible bucket (Cloudflare R2 in production) using
//   presigned PUT/GET URLs, so clip bytes never pass through the gateway.

import { createHash, createHmac, randomBytes, timingSafeEqual } from 'node:crypto';
import { createReadStream, createWriteStream } from 'node:fs';
import { mkdir, rename, rm, stat } from 'node:fs/promises';
import path from 'node:path';
import { Readable, Transform } from 'node:stream';
import { pipeline } from 'node:stream/promises';
import {
  DeleteObjectCommand,
  GetObjectCommand,
  HeadObjectCommand,
  PutObjectCommand,
  S3Client,
} from '@aws-sdk/client-s3';
import { getSignedUrl } from '@aws-sdk/s3-request-presigner';
import type { Config } from './config';
import { log } from './log';

export type SignedRequest = {
  url: string;
  method: 'PUT' | 'GET';
  /** Headers the client must send with the request. */
  headers: Record<string, string>;
};

export interface Storage {
  readonly driver: 'local' | 's3';
  presignPut(key: string, contentType: string, ttlMs: number): Promise<SignedRequest>;
  presignGet(key: string, ttlMs: number, downloadName?: string): Promise<SignedRequest>;
  /** Size in bytes, or null when the object does not exist. */
  head(key: string): Promise<number | null>;
  sha256(key: string): Promise<string>;
  delete(key: string): Promise<void>;
  /** Bounded read for small manifests; never buffers arbitrary video. */
  readSmall(key: string, maxBytes: number): Promise<Buffer>;
}

async function readBounded(stream: Readable, maxBytes: number): Promise<Buffer> {
  const chunks: Buffer[] = [];
  let size = 0;
  try {
    for await (const chunk of stream) {
      const bytes = Buffer.from(chunk);
      size += bytes.length;
      if (size > maxBytes) throw new Error("manifest exceeds size limit");
      chunks.push(bytes);
    }
    return Buffer.concat(chunks);
  } finally { stream.destroy(); }
}

export function createStorage(config: Config): Storage {
  if (config.storage.driver === 's3') return new S3Storage(config.storage.s3);
  return new LocalStorage(config.storage.local, config.publicBaseUrl);
}

function assertSafeKey(key: string) {
  if (!key || key.startsWith('/') || key.includes('..') || key.includes('\\') || key.includes('\0')) {
    throw new Error(`unsafe storage key: ${key}`);
  }
}

// ---------------------------------------------------------------------------
// Local disk
// ---------------------------------------------------------------------------

export class LocalStorage implements Storage {
  readonly driver = 'local' as const;
  readonly root: string;
  readonly maxBytes: number;
  #secret: Buffer;
  #baseUrl: string;

  constructor(cfg: Config['storage']['local'], publicBaseUrl: string) {
    this.root = path.resolve(cfg.dir);
    this.maxBytes = cfg.maxBytes;
    this.#baseUrl = publicBaseUrl;
    if (cfg.signingSecret) {
      this.#secret = Buffer.from(cfg.signingSecret);
    } else {
      this.#secret = randomBytes(32);
      log.warn('URL_SIGNING_SECRET not set; local file URLs will not survive a restart');
    }
  }

  pathFor(key: string): string {
    assertSafeKey(key);
    const full = path.resolve(this.root, key);
    if (!full.startsWith(this.root + path.sep)) throw new Error('key escapes storage root');
    return full;
  }

  #sign(method: string, key: string, exp: number, contentType: string): string {
    return createHmac('sha256', this.#secret)
      .update(`${method}\n${key}\n${exp}\n${contentType}`)
      .digest('base64url');
  }

  /** Validate a signed URL presented to the /files route. */
  verify(method: string, key: string, exp: string | undefined, sig: string | undefined, contentType = ''): boolean {
    if (!exp || !sig) return false;
    const expNum = Number(exp);
    if (!Number.isFinite(expNum) || expNum < Date.now()) return false;
    const expected = Buffer.from(this.#sign(method, key, expNum, contentType));
    const given = Buffer.from(sig);
    return expected.length === given.length && timingSafeEqual(expected, given);
  }

  #url(method: 'PUT' | 'GET', key: string, ttlMs: number, contentType: string, extra = '') {
    const exp = Date.now() + ttlMs;
    const sig = this.#sign(method, key, exp, contentType);
    const encodedKey = key.split('/').map(encodeURIComponent).join('/');
    return `${this.#baseUrl}/files/${encodedKey}?m=${method}&exp=${exp}&sig=${sig}${extra}`;
  }

  async presignPut(key: string, contentType: string, ttlMs: number): Promise<SignedRequest> {
    assertSafeKey(key);
    return {
      url: this.#url('PUT', key, ttlMs, contentType),
      method: 'PUT',
      headers: { 'content-type': contentType },
    };
  }

  async presignGet(key: string, ttlMs: number, downloadName?: string): Promise<SignedRequest> {
    assertSafeKey(key);
    const extra = downloadName ? `&dl=${encodeURIComponent(downloadName)}` : '';
    return { url: this.#url('GET', key, ttlMs, '', extra), method: 'GET', headers: {} };
  }

  async head(key: string): Promise<number | null> {
    try {
      const s = await stat(this.pathFor(key));
      return s.isFile() ? s.size : null;
    } catch {
      return null;
    }
  }

  async sha256(key: string): Promise<string> {
    const hash = createHash('sha256');
    await pipeline(createReadStream(this.pathFor(key)), hash);
    return hash.digest('hex');
  }

  async delete(key: string): Promise<void> {
    await rm(this.pathFor(key), { force: true });
  }

  async readSmall(key: string, maxBytes: number): Promise<Buffer> {
    return readBounded(createReadStream(this.pathFor(key)), maxBytes);
  }

  /** Stream a request body to disk atomically, enforcing the size cap. */
  async write(key: string, body: Readable): Promise<number> {
    const full = this.pathFor(key);
    await mkdir(path.dirname(full), { recursive: true });
    const tmp = `${full}.${randomBytes(6).toString('hex')}.part`;
    let bytes = 0;
    const max = this.maxBytes;
    const counter = new Transform({
      transform(chunk, _enc, cb) {
        bytes += chunk.length;
        if (bytes > max) cb(new Error('upload exceeds size limit'));
        else cb(null, chunk);
      },
    });
    try {
      await pipeline(body, counter, createWriteStream(tmp));
      await rename(tmp, full);
      return bytes;
    } catch (err) {
      await rm(tmp, { force: true });
      throw err;
    }
  }
}

// ---------------------------------------------------------------------------
// S3 / R2
// ---------------------------------------------------------------------------

export class S3Storage implements Storage {
  readonly driver = 's3' as const;
  #client: S3Client;
  #bucket: string;

  constructor(cfg: Config['storage']['s3']) {
    if (!cfg.bucket || !cfg.accessKeyId || !cfg.secretAccessKey) {
      throw new Error('S3 storage requires S3_BUCKET, S3_ACCESS_KEY_ID and S3_SECRET_ACCESS_KEY');
    }
    this.#bucket = cfg.bucket;
    this.#client = new S3Client({
      region: cfg.region,
      endpoint: cfg.endpoint,
      forcePathStyle: cfg.forcePathStyle,
      credentials: { accessKeyId: cfg.accessKeyId, secretAccessKey: cfg.secretAccessKey },
      // R2 rejects the SDK's default CRC32 checksum headers on presigned PUTs.
      requestChecksumCalculation: 'WHEN_REQUIRED',
      responseChecksumValidation: 'WHEN_REQUIRED',
    });
  }

  async presignPut(key: string, contentType: string, ttlMs: number): Promise<SignedRequest> {
    assertSafeKey(key);
    const url = await getSignedUrl(
      this.#client,
      new PutObjectCommand({ Bucket: this.#bucket, Key: key, ContentType: contentType }),
      { expiresIn: Math.ceil(ttlMs / 1000), signableHeaders: new Set(['content-type']) }
    );
    return { url, method: 'PUT', headers: { 'content-type': contentType } };
  }

  async presignGet(key: string, ttlMs: number, downloadName?: string): Promise<SignedRequest> {
    assertSafeKey(key);
    const url = await getSignedUrl(
      this.#client,
      new GetObjectCommand({
        Bucket: this.#bucket,
        Key: key,
        ResponseContentDisposition: downloadName
          ? `inline; filename="${downloadName.replace(/"/g, '')}"`
          : undefined,
      }),
      { expiresIn: Math.ceil(ttlMs / 1000) }
    );
    return { url, method: 'GET', headers: {} };
  }

  async head(key: string): Promise<number | null> {
    try {
      const out = await this.#client.send(new HeadObjectCommand({ Bucket: this.#bucket, Key: key }));
      return out.ContentLength ?? 0;
    } catch (err: any) {
      if (err?.$metadata?.httpStatusCode === 404 || err?.name === 'NotFound') return null;
      throw err;
    }
  }

  async sha256(key: string): Promise<string> {
    const out = await this.#client.send(new GetObjectCommand({ Bucket: this.#bucket, Key: key }));
    const hash = createHash('sha256');
    await pipeline(out.Body as Readable, hash);
    return hash.digest('hex');
  }

  async readSmall(key: string, maxBytes: number): Promise<Buffer> {
    assertSafeKey(key);
    const out = await this.#client.send(new GetObjectCommand({ Bucket: this.#bucket, Key: key }));
    return readBounded(out.Body as Readable, maxBytes);
  }

  async delete(key: string): Promise<void> {
    await this.#client.send(new DeleteObjectCommand({ Bucket: this.#bucket, Key: key }));
  }
}
