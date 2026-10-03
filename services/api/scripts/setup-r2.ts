// Check R2/S3 access and set the bucket CORS rules browsers need for direct
// signed uploads/downloads. Re-run whenever the companion's origin changes.
//
//   npx tsx --env-file=.env scripts/setup-r2.ts https://scalpal.example.com http://localhost:5173

import {
  GetBucketCorsCommand,
  HeadBucketCommand,
  PutBucketCorsCommand,
  S3Client,
} from '@aws-sdk/client-s3';
import { loadConfig } from '../src/config';
import { S3Storage } from '../src/storage';

const origins = process.argv.slice(2);
if (origins.length === 0) {
  console.error('usage: setup-r2.ts <origin> [origin...]');
  process.exit(1);
}

const cfg = loadConfig().storage.s3;
const client = new S3Client({
  region: cfg.region,
  endpoint: cfg.endpoint,
  credentials: { accessKeyId: cfg.accessKeyId!, secretAccessKey: cfg.secretAccessKey! },
  requestChecksumCalculation: 'WHEN_REQUIRED',
});

await client.send(new HeadBucketCommand({ Bucket: cfg.bucket! }));
console.log(`bucket ${cfg.bucket}: reachable`);

try {
  await client.send(
    new PutBucketCorsCommand({
      Bucket: cfg.bucket!,
      CORSConfiguration: {
        CORSRules: [
          {
            AllowedOrigins: origins,
            AllowedMethods: ['GET', 'PUT', 'HEAD'],
            AllowedHeaders: ['content-type'],
            ExposeHeaders: ['etag', 'content-length'],
            MaxAgeSeconds: 600,
          },
        ],
      },
    })
  );
  const cors = await client.send(new GetBucketCorsCommand({ Bucket: cfg.bucket! }));
  console.log('CORS origins:', cors.CORSRules?.[0]?.AllowedOrigins?.join(', '));
} catch (err: any) {
  console.warn(`could not set CORS (${err?.name ?? err}); these keys lack bucket-admin rights.`);
  console.warn('Set it in the Cloudflare dashboard: R2 > bucket > Settings > CORS policy:');
  console.warn(
    JSON.stringify(
      [{ AllowedOrigins: origins, AllowedMethods: ['GET', 'PUT', 'HEAD'], AllowedHeaders: ['content-type'], ExposeHeaders: ['etag', 'content-length'], MaxAgeSeconds: 600 }],
      null,
      2
    )
  );
}

// Round trip through presigned URLs, exactly as clients use them.
const storage = new S3Storage(cfg);
const key = `healthcheck/${Date.now()}.txt`;
const body = 'scalpal r2 check';
const put = await storage.presignPut(key, 'text/plain', 60_000);
const putRes = await fetch(put.url, { method: 'PUT', headers: put.headers, body });
if (!putRes.ok) throw new Error(`presigned PUT failed: ${putRes.status} ${await putRes.text()}`);
const size = await storage.head(key);
const get = await storage.presignGet(key, 60_000);
const text = await (await fetch(get.url)).text();
const sha = await storage.sha256(key);
await storage.delete(key);
const gone = (await storage.head(key)) == null;
console.log(`presigned PUT/GET: ${text === body ? 'ok' : 'MISMATCH'}; head size ${size}; sha256 ${sha.slice(0, 12)}…; delete ${gone ? 'ok' : 'FAILED'}`);
