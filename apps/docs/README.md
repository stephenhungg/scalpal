# Scalpal Docs

Owner: Silas. [Mintlify](https://mintlify.com) docs for Scalpal. Config is `docs.json`; pages are the `.mdx` files.

```sh
cd apps/docs
npx mint dev            # http://localhost:3000 (use --port if SpacetimeDB is on 3000)
npx mint broken-links
```

Hosting: a static export on Vercel (project `scalpal-docs`, Silas's account) at https://docs.scalpal.tech. It doesn't auto-deploy; after changing pages, redeploy:

```sh
cd apps/docs
npx mint export --output /tmp/scalpal-docs.zip
rm -rf /tmp/scalpal-docs && unzip -q /tmp/scalpal-docs.zip -d /tmp/scalpal-docs
cd /tmp/scalpal-docs && rm -f serve.js "Start Docs.bat" "Start Docs.command"
echo '{"cleanUrls": true, "trailingSlash": false}' > vercel.json
vercel link --yes --project scalpal-docs && vercel deploy --prod --yes
```

Keep pages in sync with the component READMEs, which stay the source of truth. Only put measured results under "Measured" in `status.mdx`.
