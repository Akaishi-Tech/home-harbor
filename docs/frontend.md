# Frontend App

The frontend lives in `frontend/` and uses Vite, React, React Router, TanStack Query, and TypeScript. Release assets are generated into `src/HomeHarbor.Api/wwwroot` and served by the API process.

## Commands

From the workspace root:

```bash
pnpm install
pnpm frontend:dev
pnpm frontend:typecheck
pnpm frontend:build
pnpm frontend:preview
```

`pnpm frontend:dev` listens on `0.0.0.0`. In development, `/api` and `/dav` are proxied to the local API.

## Routes

Routes are defined in `frontend/src/routes/router.tsx`.

| Route | Purpose |
| --- | --- |
| `/` | Redirect based on setup and login state |
| `/setup` | First-run initialization and storage OOBE |
| `/login` | Login |
| `/dashboard` | Control console shell |
| `/dashboard/devices` | Devices |
| `/dashboard/family` | Family members |
| `/dashboard/apps` | App catalog and install state |
| `/dashboard/shares` | SMB shares and credentials |
| `/dashboard/backups` | Backup targets and jobs |
| `/dashboard/remote` | WireGuard peers |
| `/dashboard/vault` | Vault items |
| `/dashboard/system` | System, OTA, policy, and health status |

Unknown paths redirect to `/`.

## API Client

`frontend/src/lib/api.ts` provides the shared `api<T>()` and `post<T>()` helpers:

- Sets `accept: application/json`.
- Adds `Authorization: Bearer ...` when a token is available.
- Serializes request bodies as JSON and sets `content-type`.
- Throws `ApiError` for non-2xx responses while preserving HTTP status.
- Calls the global unauthorized handler on 401, clearing the session and redirecting to `/login`.

`VITE_API_BASE_URL` can point requests at another origin. When unset, requests use same-origin paths.

## Data Fetching

React Query hooks live in `frontend/src/hooks/queries.ts`. Pages should use these hooks instead of hand-written fetch calls.

Common queries:

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

Common mutations:

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

## Auth State

After login, `authStore.setFromResponse` stores the token, member, family, and expiration time. Router initialization injects the token provider into the API client. Any request that receives 401 clears local session state and navigates to `/login`.

## Build Output

The release build writes to `src/HomeHarbor.Api/wwwroot`. This directory is ignored. Packaging and release flows rebuild the frontend instead of relying on committed static files.
