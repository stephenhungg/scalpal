# Scalpal Docs

Owner: Silas. [Mintlify](https://mintlify.com) docs for Scalpal. Config is `docs.json`; pages are the `.mdx` files.

```sh
cd apps/docs
npx mint dev            # http://localhost:3000 (use --port if SpacetimeDB is on 3000)
npx mint broken-links
```

Hosting: connect this repo in the Mintlify dashboard with the monorepo path set to `/apps/docs`. Every push to main redeploys.

Keep pages in sync with the component READMEs, which stay the source of truth. Only put measured results under "Measured" in `status.mdx`.
