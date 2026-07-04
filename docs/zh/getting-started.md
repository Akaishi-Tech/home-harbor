# 快速上手

本页用于把一个干净工作区拉到可开发状态。HomeHarbor 的正常开发循环由四部分组成：ASP.NET Core API、Vite React 前端、VitePress 文档站、PostgreSQL 开发库。

## 前置条件

- .NET SDK 使用仓库根目录 `global.json` 固定的版本。
- Node 与 pnpm 用于前端和文档站。
- PostgreSQL 用于 API 开发库。默认开发连接串是 `Host=localhost;Port=5432;Database=homeharbor_dev;Username=homeharbor;Pooling=true`。
- appliance 镜像、ISO、完整 E2E 需要额外工具链，不属于普通本地开发路径。

## 安装 JavaScript 依赖

```bash
pnpm install
```

前端和文档站现在位于同一个根级 pnpm workspace。需要单独指向 package 时可以使用：

```bash
pnpm --filter homeharbor-frontend install
pnpm --filter homeharbor-docs install
```

## 准备数据库

创建或更新开发数据库 schema，并写出本地 automation token：

```bash
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/HomeHarbor.Api/HomeHarbor.Api.csproj -- database-migrate
```

拉取或创建新的 EF Core migration 后需要重新运行该命令。

## 启动 API

```bash
dotnet run --project src/HomeHarbor.Api/HomeHarbor.Api.csproj
```

开发配置来自 `src/HomeHarbor.Api/appsettings.Development.json`。它会把数据目录放到 `./data`，Valkey cache socket 放到 `./data/run/valkey/homeharbor.sock`，JWT signing key 写到 `./data/jwt-signing.key`，automation token 写到 `./data/automation.jwt`。

API 启动时会：

1. 创建 `HomeHarbor:Storage:DataRoot`。
2. 如果配置的 Unix socket 可用则使用 Valkey distributed cache；Development 中不可用时退回内存 cache。
3. 使用配置的 connection string 连接 PostgreSQL。
4. 创建或读取 JWT signing key。
5. 在开发环境映射 OpenAPI。

## 启动前端

```bash
pnpm frontend:dev
```

Vite 开发服务器会通过 `frontend/vite.config.ts` 把 `/api` 和 `/dav` 代理到 API。前端 API client 默认使用同源路径，只有设置 `VITE_API_BASE_URL` 时才会走显式远端地址。

## 构建前端

```bash
pnpm frontend:build
```

构建输出进入 `src/HomeHarbor.Api/wwwroot`，由 ASP.NET Core 静态文件中间件提供。该目录是生成物，不提交。

## 启动文档站

```bash
pnpm docs:dev
```

文档站使用 VitePress。默认语言是英文 `/`，中文版本位于 `/zh/`。release 构建命令是：

```bash
pnpm docs:build
```

构建输出为 `docs/.vitepress/dist`，Wrangler 会使用同一个目录发布到 Cloudflare Pages。

## 常用本地检查

```bash
dotnet test tests/HomeHarbor.Tests/HomeHarbor.Tests.csproj
pnpm frontend:typecheck
pnpm frontend:build
pnpm docs:build
```

不要在本机直接运行 full E2E。完整系统验证必须使用 VM，并由 `tests/HomeHarbor.FullE2E.Tests` 或 VM 专用脚本驱动。
