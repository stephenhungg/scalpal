import { serve } from '@hono/node-server';
import { loadConfig } from './config';
import { createApp } from './http';
import { log } from './log';
import { Realtime } from './realtime';
import { Reconciler } from './reconciler';
import { createStorage } from './storage';

const config = loadConfig();
const storage = createStorage(config);
const rt = new Realtime(config.spacetime);
const reconciler = new Reconciler(rt, storage, config);
const app = createApp(config, rt, storage);

rt.start();
reconciler.start();

const server = serve({ fetch: app.fetch, port: config.port }, info => {
  log.info('gateway listening', {
    port: info.port,
    storage: storage.driver,
    publicBaseUrl: config.publicBaseUrl,
    workers: config.workers.tokens.size,
  });
});

function shutdown() {
  log.info('shutting down');
  reconciler.stop();
  rt.stop();
  server.close(() => process.exit(0));
  setTimeout(() => process.exit(0), 3000).unref();
}
process.on('SIGINT', shutdown);
process.on('SIGTERM', shutdown);
