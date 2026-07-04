# 存储与 WebDAV

HomeHarbor 的用户数据以家庭为单位组织。API、WebDAV、SMB、备份和媒体索引都必须通过同一套路径策略进入物理存储，防止路径穿越和跨家庭访问。

## 数据根目录

release 默认数据根是：

```text
/homeharbor-data
```

家庭数据路径形如：

```text
/homeharbor-data/families/{familyId:N}/{area}/{relativePath}
```

`area` 由 `StorageArea` 定义：

| Area | 目录名 | 用途 |
| --- | --- | --- |
| `Files` | `files` | 普通家庭文件 |
| `Photos` | `photos` | 照片和媒体资产 |
| `Backups` | `backups` | 本地备份数据 |

## 路径安全策略

`StoragePathPolicy.NormalizeDavPath` 和 `ResolvePhysicalPath` 是 WebDAV 与存储访问的核心安全边界。

策略要求：

- 空路径归一为 `/`。
- percent-encoding 必须合法。
- 反斜杠统一为 `/`。
- 路径必须以 `/` 开头。
- 禁止 NUL。
- 禁止 `.` 和 `..` segment。
- 物理路径必须等于 area root 或位于 area root 内部。

如果路径逃逸，API 会抛出路径相关异常，并由中间件转成 `400`。

## WebDAV

WebDAV controller 位于 `src/HomeHarbor.Api/Controllers/WebDavController.cs`，路径为：

```text
/dav/{area}/{*path=}
```

认证使用 Basic Auth，不使用前端 bearer token。WebDAV token 可在 setup 响应中初始生成，也可通过 `/api/webdav-tokens` 管理。

典型客户端配置：

- URL：`https://<homeharbor-host>/dav/files/`
- Username：setup 或 token API 返回的用户名。
- Password：WebDAV token。

不同 area 可用于不同同步工具。照片同步应使用 `/dav/photos/`，备份工具应使用 `/dav/backups/`。

## Storage OOBE

Storage OOBE 用于首次启动时选择加密方式、解锁方式、数据目标、文件系统和 RAID 模式。默认仍然是 LUKS2 加 Btrfs 推荐模式。XFS 在单盘模式下仅支持一个目标；如果明确选择 RAID5/RAID6，则会使用 `mdadm` 底层阵列。Btrfs RAID5/RAID6 也会自动使用 `mdadm`，并在应用前提示用户。ZFS 会在每个选中目标下面使用 LUKS2，并创建原生 pool 布局（`single`、`mirror`、`raid10`、`raidz1` 或 `raidz2`）；Web OOBE 会把 RAID5 映射到 RAIDZ1，把 RAID6 映射到 RAIDZ2。只有启动带签名 `zfs-utils` EROFS `/usr` addon 的 `zfs` kernel channel 时，ZFS 才可用。

安装器会把主硬盘剩余空间保留为未格式化的 `data-candidate` 分区；OOBE 可以使用该目标、一个或多个独立硬盘，或多个加密目标组成 Btrfs RAID profile、mdadm-backed RAID5/RAID6，或 ZFS pool layout。

- `GET /api/setup/storage/inventory`
- `POST /api/setup/storage/recommendation`
- `POST /api/setup/storage/plan`
- `POST /api/setup/storage/apply`
- `GET /api/setup/storage/status`

默认保护分区标签包括：

```text
esp, boot_a, boot_b, super, state, recovery_a, recovery_b,
vbmeta_a, vbmeta_b, data, data-candidate
```

`MinimumInstallableBytes` 默认为 32 GiB。Inventory 会返回明确的 storage targets（`main-reserved` 或 `whole-disk`）、eligibility reasons，以及 filesystem capability status。生成 plan 时会列出 destructive targets、filesystem、RAID mode、RAID backend（`filesystem` 或 `mdadm`）、解析后的底层 Btrfs profile 或 ZFS layout 元数据、warnings、unlock mode（`passphrase` 或 `tpm2`）、confirmation phrase，以及是否需要 bootloader 解锁。

## 存储健康

`StorageHealthService` 和 `POST /api/storage/health/check` 用于系统定时检查。前端读取 `GET /api/storage/health` 和 dashboard overview 中的 storage 状态。

自动化 health check 必须使用 automation token，避免普通用户随意触发 appliance 内部探测。

## SMB 与容器数据

SMB share 和 managed containers 都以 API desired state 为源，agent 将配置渲染到系统服务。新增共享或容器时，要让 API 负责 desired state 和审计数据，agent 负责把状态应用到 appliance。
