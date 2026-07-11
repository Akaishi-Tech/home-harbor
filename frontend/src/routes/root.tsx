import { Outlet, redirect, useRouteError } from "react-router";
import { useTranslation } from "react-i18next";
import { MeshBackground } from "@/components/glass/mesh-background";
import { Brand } from "@/components/app-shell/brand";
import { Button } from "@/components/ui/button";
import { ApiError, api } from "@/lib/api";
import { authStore } from "@/lib/auth-store";
import { isFamilyAdmin, isFamilyMember } from "@/lib/auth";
import { errorMessage } from "@/lib/format";
import type { SetupStatus } from "@/types";

export function RootLayout() {
  return (
    <>
      <MeshBackground />
      <Outlet />
    </>
  );
}

export function Splash({ label }: { label?: string }) {
  const { t } = useTranslation();

  return (
    <div className="grid min-h-svh place-items-center p-6">
      <div className="glass-surface flex flex-col items-center gap-4 rounded-2xl px-10 py-8">
        <Brand />
        <div className="flex items-center gap-2 text-sm text-muted-foreground">
          <span className="size-2 animate-ping rounded-full bg-primary" />
          {label ?? t("common.loading")}
        </div>
      </div>
    </div>
  );
}

export function RootError() {
  const error = useRouteError();
  const { t } = useTranslation();

  return (
    <div className="grid min-h-svh place-items-center p-6">
      <div className="glass-surface w-full max-w-md space-y-4 rounded-2xl p-8 text-center">
        <Brand className="justify-center" />
        <div className="space-y-1.5">
          <h1 className="text-lg font-semibold">{t("pages.error.title")}</h1>
          <p className="text-sm text-muted-foreground">{errorMessage(error)}</p>
        </div>
        <Button onClick={() => window.location.assign("/")}>
          {t("common.reload")}
        </Button>
      </div>
    </div>
  );
}

export async function rootIndexLoader() {
  const setup = await api<SetupStatus>("/api/setup", { auth: false });
  if (!setup.initialized) return redirect("/setup");
  return (await refreshStoredSession())
    ? redirect("/dashboard")
    : redirect("/login");
}

export async function setupGuardLoader(): Promise<SetupStatus | Response> {
  const setup = await api<SetupStatus>("/api/setup", { auth: false });
  if (setup.initialized) return redirect("/login");
  return setup;
}

export async function loginGuardLoader(): Promise<SetupStatus | Response> {
  const setup = await api<SetupStatus>("/api/setup", { auth: false });
  if (!setup.initialized) return redirect("/setup");
  if (await refreshStoredSession()) return redirect("/dashboard");
  return setup;
}

export async function dashboardGuardLoader(): Promise<Response | null> {
  if (!authStore.get()) return redirect("/login");
  return (await refreshStoredSession()) ? null : redirect("/login");
}

export function familyAdminGuardLoader(): Response | null {
  const auth = authStore.get();
  if (!auth) return redirect("/login");
  return isFamilyAdmin(auth) ? null : redirect("/dashboard");
}

export function familyMemberGuardLoader(): Response | null {
  const auth = authStore.get();
  if (!auth) return redirect("/login");
  return isFamilyMember(auth) ? null : redirect("/dashboard");
}

async function refreshStoredSession(): Promise<boolean> {
  if (!authStore.get()) return false;

  try {
    const session = await api<unknown>("/api/identity/session");
    if (!authStore.setFromSession(session)) return false;
    return true;
  } catch (error) {
    if (error instanceof ApiError && error.status !== 401) throw error;
    authStore.clear();
    return false;
  }
}
