import { createApp } from "./app.js";
import { createFinchNodeClient } from "./finchnode.js";

// Vercel and other Hono hosts pick up the default export.
export default createApp({ client: createFinchNodeClient({ baseUrl: process.env.FINCHNODE_BASE_URL }) });
