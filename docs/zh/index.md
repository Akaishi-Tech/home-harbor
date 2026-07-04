# HomeHarbor 文档

HomeHarbor 是一个家庭 appliance 控制平面。它把 .NET 控制 API、Vite React 前端、WebDAV 家庭存储、SMB 与容器编排、OTA 打包、live installer、recovery 工具和 VM 级系统验证放在同一个可重复构建的仓库里。

## 文档入口

- [快速上手](./getting-started.md)：本地 API、前端和最小开发环境。
- [开发工作流](./development.md)：目录约定、代码风格、前后端协作和配置。
- [整体架构](./architecture.md)：控制平面、运行时 agent、镜像/安装/恢复工具和发布链路。
- [后端 API](./api.md)：认证、路由分层、自动化端点和 WebDAV。
- [存储与 WebDAV](./storage-webdav.md)：家庭空间、路径安全、OOBE 存储规划和文件访问。
- [应用格式](./app-format.md)：HHAF v1 应用 manifest、应用商店 index 和签名系统应用行为。
- [OTA 与发布](./ota-release.md)：A/B slot、AVB/EROFS、manifest 签名和channel readiness。
- [Wrangler 部署](./docs-deploy.md)：文档站如何用 VitePress 构建并通过 Cloudflare Pages 发布。

## 当前仓库边界

源码在 `src/`，测试在 `tests/`，前端在 `frontend/`，appliance 构建资产在 `build/`、`scripts/`、`os/` 和 `packaging/arch/`。生成物进入 `artifacts/`、`.work/`、`src/HomeHarbor.Api/wwwroot/` 或 VitePress 输出目录，不应作为源码提交。

本地只能运行单元测试和前端静态检查。完整安装、集成或端到端行为必须在虚拟机里验证。
