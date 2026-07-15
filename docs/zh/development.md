# 开发工作流

HomeHarbor 的工程目标是让 appliance 构建可重复、发布链路可审计、用户数据路径安全。开发时优先遵循现有模块边界，不把一次性 shell 逻辑扩散成长期业务规则。

## pnpm Workspace

前端和文档站位于同一个根级 pnpm workspace：

```text
pnpm-workspace.yaml
frontend/package.json
docs/package.json
```

日常使用仓库根目录的安装和脚本：

```bash
pnpm install
pnpm frontend:dev
pnpm frontend:build
pnpm docs:dev
pnpm docs:build
```

统一 lockfile 是根目录 `pnpm-lock.yaml`。不再使用 package 局部 lockfile，这样 UI 和文档站的依赖解析保持一致。

## 目录职责

| 路径 | 职责 |
| --- | --- |
| `src/HomeHarbor.Api` | ASP.NET Core API、EF Core 数据模型、认证、控制平面、静态前端托管 |
| `src/HomeHarbor.Core` | 领域 record、枚举、存储路径策略和共享模型 |
| `src/HomeHarbor.WebDav` | WebDAV HTTP method、状态码和 XML 编解码辅助 |
| `tools/system-build` | 递归固定版本的 `Akaishi-Tech/system-build` 构建引擎与 CLI |
| `tools/system-build/external/system-utils` | 固定版本的 `Akaishi-Tech/system-utils` A/B、OTA、verified boot 与运行时工具源码 |
| `src/HomeHarbor.Agent` | appliance 内 systemd 调用的运行时命令 |
| `src/HomeHarbor.Installer` | live installer TUI、manifest 校验和 boot-state 命令 |
| `src/HomeHarbor.Recovery` | recovery console 与 fastboot TCP 服务 |
| `frontend` | React 控制台 |
| `docs` | VitePress 文档站和 Cloudflare Wrangler 配置 |
| `system/x86_64` | system 与 kernel 镜像构建 descriptor |
| `boot/assets`、`system`、`os`、`packaging/arch` | HomeHarbor 品牌资源、manifest、Arch package 与 systemd 集成 |
| `tests/HomeHarbor.Tests` | 本地 MSTest 单元测试 |
| `tests/HomeHarbor.FullE2E.Tests` | VM 级完整系统测试 |

## C# 优先策略

appliance、build、release、OTA、installer、recovery、validation、JSON/manifest、加密、路径安全和 channel 防护逻辑应优先写在 C#。可复用的构建行为放入 `Akaishi-Tech/system-build`；可复用的 A/B 与 OTA 行为放入 `Akaishi-Tech/system-utils`；HomeHarbor 专属的运行时行为留在本仓库。

Shell 只适合保留为薄入口：

- POSIX wrapper。
- Arch packaging glue。
- mkinitcpio hook/install 脚本。
- chroot/bootstrap 调用。
- 无法合理用托管代码替代的系统工具编排。

不要在 shell 里新增 JSON 解析、manifest canonicalization、OTA slot 决策、Secure Boot 策略、release guard、channel 部署不变量或路径遍历检查。

## 后端约定

- 使用 nullable reference types 和 implicit usings。
- 四空格缩进，file-scoped namespace。
- 类型和方法使用 PascalCase，局部变量使用 camelCase。
- 异步服务和命令名要描述行为，不使用模糊缩写。
- API 默认要求用户 JWT；只有 setup、login、health 和静态前端回退是匿名入口。
- 自动化端点必须显式标注 `AuthorizationPolicies.Automation`。

## 前端约定

前端使用 TypeScript、React function components、Vite、React Router、TanStack Query 和本地 UI 组件。源文件位于 `frontend/src`。

- 两空格缩进。
- 双引号和分号。
- 数据请求通过 `frontend/src/lib/api.ts` 与 `frontend/src/hooks/queries.ts` 汇总。
- 路由集中在 `frontend/src/routes/router.tsx`。
- 登录态由 `authStore` 提供，401 会清理 session 并跳转登录页。

## 配置与机密

开发配置可放在本地环境变量、`.work/` 或开发数据库中。不要提交：

- 私有 release key。
- Secure Boot signing key。
- passphrase。
- channel 凭据。
- 机器本地状态。
- appliance 镜像和 ISO 生成物。

## 变更前检查

改 API 或共享 C# 逻辑后，至少运行：

```bash
dotnet test tests/HomeHarbor.Tests/HomeHarbor.Tests.csproj
```

改前端后，运行：

```bash
pnpm frontend:typecheck
pnpm frontend:build
```

改文档站后，运行：

```bash
pnpm docs:build
```
