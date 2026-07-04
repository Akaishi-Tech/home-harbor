# 前端应用

前端位于 `frontend/`，使用 Vite、React、React Router、TanStack Query 和 TypeScript。release 构建输出进入 `src/HomeHarbor.Api/wwwroot`，由 API 进程托管。

## 运行命令

```bash
pnpm install
pnpm frontend:dev
pnpm frontend:typecheck
pnpm frontend:build
```

`pnpm frontend:dev` 默认监听 `0.0.0.0`。开发时 `/api` 和 `/dav` 代理到本地 API。

## 路由结构

路由定义在 `frontend/src/routes/router.tsx`。

| 路由 | 用途 |
| --- | --- |
| `/` | 根据 setup 和登录状态跳转 |
| `/setup` | 首次初始化和 storage OOBE |
| `/login` | 登录 |
| `/dashboard` | 控制台 shell |
| `/dashboard/devices` | 设备 |
| `/dashboard/family` | 家庭成员 |
| `/dashboard/apps` | 应用 catalog 和 install state |
| `/dashboard/shares` | SMB share 和 credential |
| `/dashboard/backups` | 备份目标和任务 |
| `/dashboard/remote` | WireGuard peers |
| `/dashboard/vault` | vault items |
| `/dashboard/system` | 系统、OTA、策略和状态 |

未知路径重定向到 `/`。

## API client

`frontend/src/lib/api.ts` 提供统一 `api<T>()` 和 `post<T>()`。行为包括：

- 默认设置 `accept: application/json`。
- 有 token 时附加 `Authorization: Bearer ...`。
- body 存在时 JSON 序列化并设置 `content-type`。
- 非 2xx 抛出 `ApiError`，保留 HTTP status。
- 401 触发全局 unauthorized handler，清理 session 并跳转 `/login`。

`VITE_API_BASE_URL` 可以把请求切到其他 origin；未设置时使用同源。

## 数据请求

React Query hooks 位于 `frontend/src/hooks/queries.ts`。大多数页面不直接手写 fetch，而是通过 hooks 调用后端端点。

常见 query：

- `useSetupStatus`
- `usePairing`
- `useStorageInventory`
- `useStorageStatus`
- `useOverview`
- `usePolicy`
- `useOta`
- `useMembers`
- `useDevices`
- `useBackupTargets`
- `usePeers`
- `useCatalog`
- `useSmbShares`
- `useSmbCredentials`
- `useContainers`
- `useVaultItems`
- `useVaultItem`

常见 mutation：

- `useLogin`
- `useLogout`
- `useCompleteSetup`
- `useStorageRecommendation`
- `useStoragePlan`
- `useStorageApply`
- `useCreateDevice`
- `useCreateMember`
- `useCreatePeer`
- `useCreateBackup`
- `useInstallApp`
- `useUninstallApp`
- `useCreateSmbShare`
- `useCreateSmbCredential`
- `useRevokeSmbCredential`
- `useCreateContainer`
- `useContainerAction`
- `useCreateVaultItem`
- `useDeleteVaultItem`
- `useMediaIndex`

## 登录态

登录成功后，`authStore.setFromResponse` 保存 token、member、family 和过期时间。router 初始化时会把 token provider 注入 API client。任何请求收到 401，都会清理本地 session 并跳转登录页。

## 构建产物

release 构建写入 `src/HomeHarbor.Api/wwwroot`。这个目录被 `.gitignore` 忽略，打包和 release 流程需要重新构建，不依赖提交的静态文件。
