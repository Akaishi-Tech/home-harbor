# 贡献指南

HomeHarbor 贡献应保持源码树可重复，并避免机器本地假设。

## 变更原则

- 保持模块边界清楚。
- 业务规则优先写 C#，shell 只做薄入口。
- 不提交生成物。
- 不提交 secrets。
- 涉及 appliance、storage、OTA、recovery 或 security 的变更必须在 PR 中明确说明影响。

## 提交前检查

后端或 tooling：

```bash
dotnet test tests/HomeHarbor.Tests/HomeHarbor.Tests.csproj
```

前端：

```bash
pnpm frontend:typecheck
pnpm frontend:build
```

文档：

```bash
pnpm docs:build
```

构建命令 smoke check：

```bash
dotnet run --project src/HomeHarbor.ImageBuilder/HomeHarbor.ImageBuilder.csproj -- plan 0.1.0
```

## PR 内容

PR 应包含：

- 问题是什么。
- 采用的方案。
- 用户可见影响。
- 测试结果。
- appliance、storage、OTA、recovery、security 影响。
- UI 变化的截图或录屏。

## 文档贡献

新增功能时同步更新：

- 用户或开发者会接触的命令。
- 新环境变量。
- 新 API 端点。
- 新生成物。
- 新安全边界。
- 新 release/installer 行为。

文档站由 VitePress 构建。新增页面后，需要在 `docs/.vitepress/config.ts` 的 sidebar 中加入入口。
