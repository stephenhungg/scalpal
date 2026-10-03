// Gateway configuration. Every secret comes from the environment; see
// `.env.example` for the full list.

function env(name: string, fallback?: string): string {
  const value = process.env[name];
  if (value != null && value !== '') return value;
  if (fallback != null) return fallback;
  throw new Error(`missing required environment variable ${name}`);
}

function optional(name: string): string | undefined {
  const value = process.env[name];
  return value != null && value !== '' ? value : undefined;
}

function parseWorkerTokens(raw: string | undefined): Map<string, string> {
  // "workerId:token,otherWorker:token2" -> token -> workerId
  const map = new Map<string, string>();
  if (!raw) return map;
  for (const pair of raw.split(',')) {
    const idx = pair.indexOf(':');
    if (idx <= 0) continue;
    const id = pair.slice(0, idx).trim();
    const token = pair.slice(idx + 1).trim();
    if (id && token.length >= 16) map.set(token, id);
  }
  return map;
}

export type StorageDriver = 'local' | 's3';

export function loadConfig() {
  const port = Number(env('PORT', '8787'));
  const storageDriver = env('STORAGE_DRIVER', 'local') as StorageDriver;
  if (storageDriver !== 'local' && storageDriver !== 's3') {
    throw new Error(`STORAGE_DRIVER must be 'local' or 's3'`);
  }
  return {
    port,
    publicBaseUrl: env('PUBLIC_BASE_URL', `http://localhost:${port}`).replace(/\/$/, ''),
    corsOrigins: env('CORS_ORIGINS', 'http://localhost:5173')
      .split(',')
      .map(s => s.trim())
      .filter(Boolean),

    spacetime: {
      uri: env('SPACETIMEDB_URI', 'ws://127.0.0.1:3000'),
      database: env('SPACETIMEDB_DB', 'scalpal'),
      /** Token for a service identity. If unset, a token is created and saved. */
      token: optional('SPACETIMEDB_TOKEN'),
      tokenFile: env('SPACETIMEDB_TOKEN_FILE', '.gateway-token'),
    },

    storage: {
      driver: storageDriver,
      local: {
        dir: env('LOCAL_STORAGE_DIR', './data/artifacts'),
        signingSecret: optional('URL_SIGNING_SECRET'),
        maxBytes: Number(env('LOCAL_MAX_BYTES', String(2 * 1024 * 1024 * 1024))),
      },
      s3: {
        endpoint: optional('S3_ENDPOINT'),
        region: env('S3_REGION', 'auto'),
        bucket: optional('S3_BUCKET'),
        accessKeyId: optional('S3_ACCESS_KEY_ID'),
        secretAccessKey: optional('S3_SECRET_ACCESS_KEY'),
        forcePathStyle: env('S3_FORCE_PATH_STYLE', 'false') === 'true',
      },
      uploadTtlMs: Number(env('UPLOAD_URL_TTL_MS', String(15 * 60 * 1000))),
      downloadTtlMs: Number(env('DOWNLOAD_URL_TTL_MS', String(15 * 60 * 1000))),
      /** Hash objects up to this size to verify a declared sha256. */
      hashVerifyMaxBytes: Number(env('HASH_VERIFY_MAX_BYTES', String(512 * 1024 * 1024))),
    },

    workers: {
      tokens: parseWorkerTokens(optional('WORKER_TOKENS')),
      defaultLeaseMs: Number(env('WORKER_LEASE_MS', '60000')),
      minLeaseMs: Number(env('WORKER_MIN_LEASE_MS', '10000')),
      inputUrlTtlMs: Number(env('WORKER_INPUT_URL_TTL_MS', String(60 * 60 * 1000))),
    },

    voice: {
      elevenLabsApiKey: optional('ELEVENLABS_API_KEY'),
      elevenLabsAgentId: optional('ELEVENLABS_AGENT_ID'),
      ttlMs: Number(env('VOICE_GRANT_TTL_MS', String(14 * 60 * 1000))),
    },

    ice: {
      cloudflareTurnKeyId: optional('CLOUDFLARE_TURN_KEY_ID'),
      cloudflareTurnApiToken: optional('CLOUDFLARE_TURN_API_TOKEN'),
      turnUrls: optional('TURN_URLS')?.split(',').map(s => s.trim()),
      turnUsername: optional('TURN_USERNAME'),
      turnCredential: optional('TURN_CREDENTIAL'),
      stunUrls: env('STUN_URLS', 'stun:stun.cloudflare.com:3478,stun:stun.l.google.com:19302')
        .split(',')
        .map(s => s.trim()),
      ttlSeconds: Number(env('ICE_TTL_SECONDS', String(4 * 60 * 60))),
    },
  };
}

export type Config = ReturnType<typeof loadConfig>;
