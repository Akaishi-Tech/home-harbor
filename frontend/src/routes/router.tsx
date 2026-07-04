import { createBrowserRouter, redirect } from "react-router";
import {
  configureAuthTokenProvider,
  configureUnauthorizedHandler,
} from "@/lib/api";
import { authStore } from "@/lib/auth-store";
import {
  RootError,
  RootLayout,
  Splash,
  dashboardGuardLoader,
  loginGuardLoader,
  rootIndexLoader,
  setupGuardLoader,
} from "@/routes/root";
import { AppShell } from "@/components/app-shell/app-shell";

// The api client reads the bearer token from the auth store on every request.
configureAuthTokenProvider(() => authStore.token());

export const router = createBrowserRouter([
  {
    element: <RootLayout />,
    errorElement: <RootError />,
    HydrateFallback: Splash,
    children: [
      { path: "/", loader: rootIndexLoader },
      {
        path: "/setup",
        loader: setupGuardLoader,
        lazy: async () => ({
          Component: (await import("@/routes/setup/setup-wizard")).SetupWizard,
        }),
      },
      {
        path: "/login",
        loader: loginGuardLoader,
        lazy: async () => ({
          Component: (await import("@/routes/login-page")).LoginPage,
        }),
      },
      {
        path: "/dashboard",
        loader: dashboardGuardLoader,
        element: <AppShell />,
        children: [
          {
            index: true,
            lazy: async () => ({
              Component: (await import("@/routes/dashboard/overview-page"))
                .OverviewPage,
            }),
          },
          {
            path: "devices",
            lazy: async () => ({
              Component: (await import("@/routes/dashboard/devices-page"))
                .DevicesPage,
            }),
          },
          {
            path: "family",
            lazy: async () => ({
              Component: (await import("@/routes/dashboard/family-page"))
                .FamilyPage,
            }),
          },
          {
            path: "apps",
            lazy: async () => ({
              Component: (await import("@/routes/dashboard/apps-page"))
                .AppsPage,
            }),
          },
          {
            path: "shares",
            lazy: async () => ({
              Component: (await import("@/routes/dashboard/shares-page"))
                .SharesPage,
            }),
          },
          {
            path: "backups",
            lazy: async () => ({
              Component: (await import("@/routes/dashboard/backups-page"))
                .BackupsPage,
            }),
          },
          {
            path: "remote",
            lazy: async () => ({
              Component: (await import("@/routes/dashboard/remote-page"))
                .RemotePage,
            }),
          },
          {
            path: "vault",
            lazy: async () => ({
              Component: (await import("@/routes/dashboard/vault-page"))
                .VaultPage,
            }),
          },
          {
            path: "system",
            lazy: async () => ({
              Component: (await import("@/routes/dashboard/system-page"))
                .SystemPage,
            }),
          },
        ],
      },
      { path: "*", loader: () => redirect("/") },
    ],
  },
]);

// A 401 anywhere clears the session and bounces to login.
configureUnauthorizedHandler(() => {
  authStore.clear();
  void router.navigate("/login");
});
