# 应用格式

HomeHarbor App Format v1（HHAF v1）是应用商店使用的签名应用格式。它用同一种 manifest 描述容器应用和签名系统应用。

## 应用 Manifest

应用 manifest 是 JSON，包含：

- `schemaVersion: 1`
- `kind: "homeharbor.app"`
- `appKey`、`version`、`channel`
- `displayName`、`title`、`description`、`category`
- `recommendedInSetup`
- `visibleRoles`
- `install`
- `signatureAlgorithm: "Ed25519"`、`signedPayloadSha256`、`signingKeyId`、`signature`

签名 payload 由 C# 按固定字段顺序写出。远程 manifest 必须通过 HomeHarbor release public key 校验，并且 `channel` 必须匹配当前 appliance OTA channel，否则会被忽略。

容器应用使用 `install.type: "container"`，并映射到现有 `ManagedContainerSpecService` 的安全子集：image、ports、environment、volumes 和 command。HHAF v1 不开放 privileged container、device mapping、额外 capability 或原始 Podman 参数。

系统应用使用 `install.type: "system"` 和 `mode: "usr-overlay"`。它通过 `install.manifestUrl` 指向现有签名 system payload manifest，声明 wrapper `commands`，也可以声明 `hotCheck` 命令。

## 商店索引

应用商店 index 也是签名 JSON：

- `schemaVersion: 1`
- `kind: "homeharbor.app-store"`
- `channel`
- `generatedAt`
- `apps: [{ appKey, version, manifestUrl, manifestSha256 }]`

默认读取 `${HOMEHARBOR_APP_STORE_BASE_URL}/index.json`，也可以用 `HOMEHARBOR_APP_STORE_INDEX_URL` 覆盖。远程 index 或应用 manifest 校验失败时，HomeHarbor 会回退到内置 catalog。

## ZFS Utilities

`zfs-utils` 不再是系统应用。它会作为 `zfs` kernel channel 的 `zfs-utils` kernel overlay addon 打包。该 addon 是 EROFS 镜像，由签名 kernel OTA manifest 的 `addons` 数组引用；initramfs 从 `state` 分区的内容寻址 store 校验 SHA-256 后，把它挂载为 `/usr` overlay。

`generic` kernel channel 没有 addons。只有当前内核存在 `zfs` 模块，并且挂载的 addon 提供 `/usr/bin/zfs` 与 `/usr/bin/zpool` 时，ZFS 存储能力才可用。
