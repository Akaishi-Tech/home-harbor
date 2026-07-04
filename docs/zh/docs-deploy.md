# 文档站与 Wrangler 部署

`docs/` 是根级 pnpm workspace 里的 VitePress 文档站，并带有 Cloudflare Wrangler 配置。它既可以本地预览，也可以直接发布到 Cloudflare Pages。

## 文件结构

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

英文页面位于 `docs/` 根部，也是默认语言。中文页面位于 `docs/zh/`，访问路径是 `/zh/`。

## 本地开发

安装依赖：

```bash
pnpm install
```

启动开发服务器：

```bash
pnpm docs:dev
```

构建：

```bash
pnpm docs:build
```

预览release 构建：

```bash
pnpm docs:preview
```

## Wrangler 配置

`docs/wrangler.toml`：

```toml
"$schema" = "./node_modules/wrangler/config-schema.json"

name = "home-harbor"
pages_build_output_dir = ".vitepress/dist"
compatibility_date = "2026-07-01"
```

`name` 是 Cloudflare Pages 项目名。`pages_build_output_dir` 指向 VitePress 的静态构建目录。不要在这里添加 `[assets]` 配置；Wrangler 会把它视为 Workers 静态资源配置，并在 Pages 部署校验阶段拒绝。这个文件作为 Pages 项目的配置来源；如果后续在 Cloudflare Dashboard 修改 Pages settings，应把变更同步回 `wrangler.toml`。

## 直接上传部署

先构建：

```bash
pnpm docs:build
```

再部署：

```bash
pnpm docs:deploy
```

`deploy` 脚本会在 `homeharbor-docs` package 内执行 `wrangler pages deploy .vitepress/dist --project-name home-harbor`。`homeharbor-docs` 是 pnpm package 名，`home-harbor` 是 Cloudflare Pages 项目名。

Direct Upload CI 需要配置这些环境变量：

- `CLOUDFLARE_API_TOKEN`：account API token，需要对拥有 `home-harbor` 的账号具备 Account > Cloudflare Pages > Edit 权限。
- `CLOUDFLARE_ACCOUNT_ID`：Cloudflare account ID，尤其适用于 token 可以访问多个账号，或 CI 不应依赖账号自动推断的情况。

如果 VitePress 构建完成后 Wrangler 报 `Authentication error [code: 10000]`，说明静态构建已经成功。请轮换或调整 `CLOUDFLARE_API_TOKEN` 的权限范围，然后重新部署。

如果首次部署或需要登录：

```bash
pnpm --filter homeharbor-docs exec wrangler login
pnpm --filter homeharbor-docs exec wrangler pages project create home-harbor
```

## Cloudflare Pages Git 集成

如果使用 Git 集成，项目根目录建议设置为：

```text
docs
```

构建命令：

```text
pnpm install && pnpm build
```

输出目录：

```text
.vitepress/dist
```

部署命令：

```text
留空
```

不要在 Pages Git 集成里把 deploy command 设置为 `pnpm deploy`。Pages 会自行发布输出目录；`pnpm docs:deploy` 只用于本机或外部 CI 的 direct upload。

Node 版本使用 20 或更高。

## 缓存与安全头

`docs/public/_headers` 会被 VitePress 复制到输出目录，为 hashed assets 设置长期缓存，并给所有页面加基础安全头。

不要启用会改写 HTML 注释的自动压缩选项。VitePress/Vue 的 hydration 可能依赖输出结构，HTML auto-minify 可能造成不一致。

## 更新文档流程

1. 修改 Markdown 或 VitePress 配置。
2. 运行 `pnpm docs:build`。
3. 本地需要查看时运行 `pnpm docs:preview`。
4. 提交源码，不提交 `docs/.vitepress/dist`。
5. 通过 Git 集成或 `pnpm docs:deploy` 发布。
