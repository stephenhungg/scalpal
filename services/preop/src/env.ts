import { existsSync } from "node:fs";

// Load services/preop/.env for local runs. Hosted deploys set real environment variables instead.
if (existsSync(".env")) process.loadEnvFile(".env");
