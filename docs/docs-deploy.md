# Docs Site and Wrangler Deploy

`docs/` is a VitePress documentation site inside the root pnpm workspace. It also includes Cloudflare Wrangler configuration so the generated static site can be deployed to Cloudflare Pages.

## Structure

```text
docs/
  .vitepress/config.ts
  public/
    _headers
    logo.svg
  zh/
    index.md
    ...
  package.json
  wrangler.toml
  *.md
  reference/
```

English pages live at the docs root and are the default locale. Simplified Chinese pages live under `docs/zh/` and are served from `/zh/`.

## Local Development

Install all workspace dependencies from the repository root:

```bash
pnpm install
```

Start the docs dev server:

```bash
pnpm docs:dev
```

Build:

```bash
pnpm docs:build
```

Preview the release build:

```bash
pnpm docs:preview
```

## Wrangler Configuration

`docs/wrangler.toml`:

```toml
"$schema" = "./node_modules/wrangler/config-schema.json"

name = "homeharbor-docs"
pages_build_output_dir = ".vitepress/dist"
compatibility_date = "2026-07-01"
```

`pages_build_output_dir` points to the VitePress static output directory. Treat this file as the source of truth for the Pages project; if settings are changed in the Cloudflare dashboard, sync them back into `wrangler.toml`.

## Direct Upload Deploy

Build first:

```bash
pnpm docs:build
```

Deploy:

```bash
pnpm docs:deploy
```

The deploy script runs `wrangler pages deploy` in the `homeharbor-docs` package. Wrangler reads `docs/wrangler.toml` and uploads `docs/.vitepress/dist`.

For first-time setup or login:

```bash
pnpm --filter homeharbor-docs exec wrangler login
pnpm --filter homeharbor-docs exec wrangler pages project create homeharbor-docs
```

## Cloudflare Pages Git Integration

If using Git integration, set the project root directory to:

```text
docs
```

Build command:

```text
pnpm install && pnpm build
```

Output directory:

```text
.vitepress/dist
```

Use Node 20 or newer.

## Headers

`docs/public/_headers` is copied verbatim into the output directory. It gives hashed assets long-lived caching and adds basic security headers to all pages.

Do not enable HTML auto-minification that rewrites comments. VitePress/Vue hydration can depend on output structure, and HTML auto-minify can cause mismatches.

## Updating Docs

1. Edit Markdown or VitePress configuration.
2. Run `pnpm docs:build`.
3. Use `pnpm docs:preview` when visual inspection is useful.
4. Commit source files, not `docs/.vitepress/dist`.
5. Publish via Git integration or `pnpm docs:deploy`.
