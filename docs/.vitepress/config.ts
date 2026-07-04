import { fileURLToPath } from "node:url";
import { defineConfig } from "vitepress";
import type { DefaultTheme } from "vitepress";
import { withMermaid } from "vitepress-plugin-mermaid";

const dayjsEsmEntry = fileURLToPath(new URL("../node_modules/dayjs/esm/index.js", import.meta.url));

const enNav: DefaultTheme.NavItem[] = [
  { text: "Guide", link: "/getting-started" },
  { text: "Architecture", link: "/architecture" },
  { text: "API", link: "/api" },
  { text: "Docs Deploy", link: "/docs-deploy" }
];

const zhNav: DefaultTheme.NavItem[] = [
  { text: "指南", link: "/zh/getting-started" },
  { text: "架构", link: "/zh/architecture" },
  { text: "API", link: "/zh/api" },
  { text: "部署文档站", link: "/zh/docs-deploy" }
];

const enSidebar: DefaultTheme.Sidebar = [
  {
    text: "Start",
    items: [
      { text: "Overview", link: "/" },
      { text: "Getting Started", link: "/getting-started" },
      { text: "Development Workflow", link: "/development" }
    ]
  },
  {
    text: "System Design",
    items: [
      { text: "Architecture", link: "/architecture" },
      { text: "Backend API", link: "/api" },
      { text: "Frontend App", link: "/frontend" },
      { text: "Storage and WebDAV", link: "/storage-webdav" },
      { text: "App Format", link: "/app-format" }
    ]
  },
  {
    text: "Appliance",
    items: [
      { text: "OTA and Release", link: "/ota-release" },
      { text: "Installer and Recovery", link: "/installer-recovery" },
      { text: "Security Model", link: "/security" }
    ]
  },
  {
    text: "Engineering",
    items: [
      { text: "Testing Strategy", link: "/testing" },
      { text: "Contributing", link: "/contributing" },
      { text: "Wrangler Deploy", link: "/docs-deploy" }
    ]
  },
  {
    text: "Reference",
    items: [
      { text: "API Routes", link: "/reference/api-routes" },
      { text: "Command Cheatsheet", link: "/reference/commands" }
    ]
  }
];

const zhSidebar: DefaultTheme.Sidebar = [
  {
    text: "开始",
    items: [
      { text: "总览", link: "/zh/" },
      { text: "快速上手", link: "/zh/getting-started" },
      { text: "开发工作流", link: "/zh/development" }
    ]
  },
  {
    text: "系统设计",
    items: [
      { text: "整体架构", link: "/zh/architecture" },
      { text: "后端 API", link: "/zh/api" },
      { text: "前端应用", link: "/zh/frontend" },
      { text: "存储与 WebDAV", link: "/zh/storage-webdav" },
      { text: "应用格式", link: "/zh/app-format" }
    ]
  },
  {
    text: "Appliance",
    items: [
      { text: "OTA 与发布", link: "/zh/ota-release" },
      { text: "安装器与恢复", link: "/zh/installer-recovery" },
      { text: "安全模型", link: "/zh/security" }
    ]
  },
  {
    text: "工程实践",
    items: [
      { text: "测试策略", link: "/zh/testing" },
      { text: "贡献指南", link: "/zh/contributing" },
      { text: "Wrangler 部署", link: "/zh/docs-deploy" }
    ]
  },
  {
    text: "参考",
    items: [
      { text: "API 路由表", link: "/zh/reference/api-routes" },
      { text: "命令速查", link: "/zh/reference/commands" }
    ]
  }
];

const commonTheme = {
  logo: "/logo.svg",
  socialLinks: [
    { icon: "github", link: "https://github.com/akaishi-tech/home-harbor" }
  ],
  search: {
    provider: "local"
  }
} satisfies Partial<DefaultTheme.Config>;

export default withMermaid(defineConfig({
  lang: "en-US",
  title: "HomeHarbor Docs",
  description: "HomeHarbor appliance control plane documentation",
  cleanUrls: true,
  lastUpdated: true,
  head: [
    ["link", { rel: "icon", type: "image/svg+xml", href: "/logo.svg" }]
  ],
  themeConfig: {
    ...commonTheme,
    nav: enNav,
    sidebar: enSidebar,
    footer: {
      message: "HomeHarbor appliance control plane documentation.",
      copyright: "GPL-3.0-only"
    },
    editLink: {
      pattern: "https://github.com/akaishi-tech/home-harbor/edit/main/docs/:path",
      text: "Edit this page"
    },
    lastUpdated: {
      text: "Last updated"
    },
    docFooter: {
      prev: "Previous",
      next: "Next"
    },
    outline: {
      label: "On This Page",
      level: [2, 3]
    },
    darkModeSwitchLabel: "Theme",
    sidebarMenuLabel: "Menu",
    returnToTopLabel: "Return to top",
    langMenuLabel: "Language"
  },
  locales: {
    root: {
      label: "English",
      lang: "en-US",
      link: "/"
    },
    zh: {
      label: "简体中文",
      lang: "zh-CN",
      title: "HomeHarbor 文档",
      description: "HomeHarbor appliance control plane documentation",
      link: "/zh/",
      themeConfig: {
        ...commonTheme,
        nav: zhNav,
        sidebar: zhSidebar,
        footer: {
          message: "HomeHarbor appliance control plane documentation.",
          copyright: "GPL-3.0-only"
        },
        editLink: {
          pattern: "https://github.com/akaishi-tech/home-harbor/edit/main/docs/:path",
          text: "编辑此页"
        },
        lastUpdated: {
          text: "最后更新"
        },
        docFooter: {
          prev: "上一页",
          next: "下一页"
        },
        outline: {
          label: "本页目录",
          level: [2, 3]
        },
        darkModeSwitchLabel: "主题",
        sidebarMenuLabel: "菜单",
        returnToTopLabel: "返回顶部",
        langMenuLabel: "语言"
      }
    }
  },
  vite: {
    resolve: {
      alias: [
        { find: /^dayjs$/, replacement: dayjsEsmEntry }
      ]
    }
  },
  mermaid: {},
  markdown: {
    lineNumbers: true
  }
}));
