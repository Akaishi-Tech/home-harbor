import { useEffect, useMemo, useState } from "react";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { toast } from "sonner";
import type { TFunction } from "i18next";
import { useTranslation } from "react-i18next";
import {
  Boxes,
  Download,
  Play,
  Plus,
  RotateCw,
  ServerCog,
  ShieldCheck,
  Square,
  Trash2,
} from "lucide-react";
import { SectionHeader } from "@/components/glass/section-header";
import {
  GlassCard,
  GlassCardContent,
  GlassCardDescription,
  GlassCardHeader,
  GlassCardTitle,
} from "@/components/glass/glass-card";
import { EmptyState } from "@/components/glass/empty-state";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Badge } from "@/components/ui/badge";
import { Checkbox } from "@/components/ui/checkbox";
import { Skeleton } from "@/components/ui/skeleton";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import {
  Form,
  FormControl,
  FormField,
  FormItem,
  FormLabel,
  FormMessage,
} from "@/components/ui/form";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import {
  useCatalog,
  useContainerAction,
  useContainers,
  useCreateContainer,
  useInstallApp,
  useUninstallApp,
  type ContainerAction,
} from "@/hooks/queries";
import { errorMessage } from "@/lib/format";
import { cn } from "@/lib/utils";
import type { CatalogItem } from "@/types";

export function AppsPage() {
  const { t } = useTranslation();

  return (
    <div className="space-y-6">
      <SectionHeader
        eyebrow={t("pages.apps.eyebrow")}
        title={t("pages.apps.title")}
        description={t("pages.apps.description")}
      />

      <Tabs defaultValue="market">
        <TabsList>
          <TabsTrigger value="market">{t("pages.apps.tabs.market")}</TabsTrigger>
          <TabsTrigger value="runtime">
            {t("pages.apps.tabs.runtime")}
          </TabsTrigger>
        </TabsList>
        <TabsContent value="market">
          <MarketTab />
        </TabsContent>
        <TabsContent value="runtime">
          <RuntimeTab />
        </TabsContent>
      </Tabs>
    </div>
  );
}

function MarketTab() {
  const catalog = useCatalog();
  const installApp = useInstallApp();
  const uninstallApp = useUninstallApp();
  const [selectedAppKey, setSelectedAppKey] = useState("");
  const { t } = useTranslation();

  const catalogItems = catalog.data ?? [];
  const firstAppKey = catalogItems[0]?.appKey ?? "";
  const selected = catalogItems.find((item) => item.appKey === selectedAppKey);

  useEffect(() => {
    if (firstAppKey && !selectedAppKey) {
      setSelectedAppKey(firstAppKey);
    }
  }, [firstAppKey, selectedAppKey]);

  function onInstall(item: CatalogItem) {
    installApp.mutate({ appKey: item.appKey }, {
      onSuccess: () => {
        toast.success(
          item.kind === "system"
            ? t("toast.systemAppDownloadQueued")
            : t("toast.appInstallQueued"),
        );
      },
      onError: (error) => toast.error(errorMessage(error)),
    });
  }

  function onUninstall(item: CatalogItem) {
    if (!item.installId) return;
    uninstallApp.mutate(item.installId, {
      onSuccess: () => toast.success(t("toast.appUninstallQueued")),
      onError: (error) => toast.error(errorMessage(error)),
    });
  }

  return (
    <div className="grid gap-3 lg:grid-cols-[minmax(0,420px)_1fr]">
      <GlassCard>
        <GlassCardHeader>
          <GlassCardTitle>{t("pages.apps.storeTitle")}</GlassCardTitle>
          <GlassCardDescription>
            {t("pages.apps.storeDescription")}
          </GlassCardDescription>
        </GlassCardHeader>
        <GlassCardContent>
          {catalog.isPending ? (
            <div className="space-y-2">
              {Array.from({ length: 4 }).map((_, index) => (
                <Skeleton key={index} className="h-16 rounded-xl" />
              ))}
            </div>
          ) : catalogItems.length > 0 ? (
            <ul className="space-y-2">
              {catalogItems.map((item) => (
                <li key={item.appKey}>
                  <button
                    type="button"
                    onClick={() => setSelectedAppKey(item.appKey)}
                    className={cn(
                      "flex w-full items-center justify-between gap-3 rounded-xl border px-3 py-2.5 text-left transition-colors",
                      selectedAppKey === item.appKey
                        ? "border-primary/50 bg-primary/10"
                        : "border-border/60 bg-background/30 hover:bg-background/50",
                    )}
                  >
                    <div className="flex min-w-0 items-center gap-3">
                      <span className="flex size-9 shrink-0 items-center justify-center rounded-lg bg-primary/10 text-primary">
                        {item.kind === "system" ? (
                          <ServerCog className="size-4" />
                        ) : (
                          <Boxes className="size-4" />
                        )}
                      </span>
                      <div className="min-w-0">
                        <p className="truncate text-sm font-medium">
                          {item.title || item.displayName}
                        </p>
                        <p className="truncate text-xs text-muted-foreground">
                          {item.description}
                        </p>
                      </div>
                    </div>
                    <div className="flex shrink-0 items-center gap-1.5">
                      <Badge variant="secondary">
                        {kindLabel(item.kind, t)}
                      </Badge>
                      {item.installed ? (
                        <Badge variant="outline">{stateLabel(item, t)}</Badge>
                      ) : null}
                    </div>
                  </button>
                </li>
              ))}
            </ul>
          ) : (
            <EmptyState
              icon={Boxes}
              title={t("pages.apps.emptyStoreTitle")}
              description={t("pages.apps.emptyStoreDescription")}
            />
          )}
        </GlassCardContent>
      </GlassCard>

      <GlassCard>
        <GlassCardHeader>
          <GlassCardTitle>
            {selected?.title ?? t("pages.apps.detailsTitle")}
          </GlassCardTitle>
          <GlassCardDescription>
            {selected?.description ?? t("pages.apps.detailsDescription")}
          </GlassCardDescription>
        </GlassCardHeader>
        <GlassCardContent>
          {selected ? (
            <div className="space-y-4">
              <div className="grid gap-2 sm:grid-cols-2">
                <InfoRow
                  label={t("fields.type")}
                  value={kindLabel(selected.kind, t)}
                />
                <InfoRow
                  label={t("fields.status")}
                  value={stateLabel(selected, t)}
                />
                <InfoRow
                  label={t("fields.version")}
                  value={selected.activeVersion || selected.version}
                />
                <InfoRow
                  label={t("fields.category")}
                  value={selected.category}
                />
              </div>
              {selected.kind === "container" ? (
                <div className="rounded-xl border border-border/60 bg-background/30 p-3">
                  <p className="text-xs text-muted-foreground">
                    {t("pages.apps.image")}
                  </p>
                  <p className="mt-1 break-all font-mono text-xs">
                    {selected.image}
                  </p>
                  {selected.port ? (
                    <p className="mt-2 text-xs text-muted-foreground">
                      {t("pages.apps.defaultPort", { port: selected.port })}
                    </p>
                  ) : null}
                </div>
              ) : (
                <div className="rounded-xl border border-border/60 bg-background/30 p-3">
                  <div className="flex items-center gap-2 text-sm font-medium">
                    <ShieldCheck className="size-4 text-primary" />
                    {t("pages.apps.signedSource")}
                  </div>
                  <p className="mt-2 break-all font-mono text-xs text-muted-foreground">
                    {selected.manifestUrl}
                  </p>
                  {selected.appRequiresReboot ? (
                    <Badge variant="outline" className="mt-3">
                      {t("pages.apps.rebootRequired")}
                    </Badge>
                  ) : null}
                </div>
              )}
              {selected.lastError ? (
                <p className="rounded-xl border border-destructive/40 bg-destructive/10 px-3 py-2 text-sm text-destructive">
                  {selected.lastError}
                </p>
              ) : null}
              <div className="flex flex-wrap gap-2">
                {selected.installed ? (
                  <Button
                    variant="outline"
                    disabled={uninstallApp.isPending}
                    onClick={() => onUninstall(selected)}
                  >
                    <Trash2 className="size-4" />
                    {uninstallApp.isPending
                      ? t("pages.apps.uninstallPending")
                      : t("pages.apps.uninstall")}
                  </Button>
                ) : (
                  <Button
                    disabled={installApp.isPending}
                    onClick={() => onInstall(selected)}
                  >
                    {selected.kind === "system" ? (
                      <Download className="size-4" />
                    ) : (
                      <Plus className="size-4" />
                    )}
                    {installApp.isPending
                      ? t("pages.apps.installPending")
                      : selected.kind === "system"
                        ? t("pages.apps.downloadInstall")
                        : t("pages.apps.installStart")}
                  </Button>
                )}
              </div>
            </div>
          ) : (
            <EmptyState
              icon={Boxes}
              title={t("pages.apps.selectTitle")}
              description={t("pages.apps.selectDescription")}
            />
          )}
        </GlassCardContent>
      </GlassCard>
    </div>
  );
}

function InfoRow({ label, value }: { label: string; value: string }) {
  const { t } = useTranslation();

  return (
    <div className="rounded-xl border border-border/60 bg-background/30 px-3 py-2">
      <p className="text-xs text-muted-foreground">{label}</p>
      <p className="mt-1 truncate text-sm font-medium">
        {value || t("common.notSet")}
      </p>
    </div>
  );
}

function kindLabel(kind: string, t: TFunction) {
  return kind === "system"
    ? t("pages.apps.kinds.system")
    : t("pages.apps.kinds.container");
}

function stateLabel(item: CatalogItem, t: TFunction) {
  if (!item.installed) return t("pages.apps.states.notInstalled");
  const state = item.runtimeState ?? item.desiredState ?? "planned";
  const labels: Record<string, string> = {
    "active-hot": t("pages.apps.states.activeHot"),
    "active-pending-reboot": t("pages.apps.states.activePendingReboot"),
    "pending-download": t("pages.apps.states.pendingDownload"),
    "pending-delete": t("pages.apps.states.pendingDelete"),
    "remove-pending-reboot": t("pages.apps.states.removePendingReboot"),
    pending: t("pages.apps.states.pending"),
    planned: t("pages.apps.states.planned"),
    running: t("pages.apps.states.running"),
    stopped: t("pages.apps.states.stopped"),
    failed: t("pages.apps.states.failed"),
  };
  return labels[state] ?? state;
}

const portString = (t: TFunction, min: number, max: number, label: string) =>
  z.string().refine((value) => {
    const port = Number(value);
    return Number.isInteger(port) && port >= min && port <= max;
  }, t("validation.portRange", { label, min, max }));

function createContainerSchema(t: TFunction) {
  return z.object({
    name: z.string().trim().min(1, t("validation.containerNameRequired")),
    image: z.string().trim().min(1, t("validation.containerImageRequired")),
    hostPort: portString(t, 1024, 65535, t("fields.hostPort")),
    containerPort: portString(t, 1, 65535, t("fields.containerPort")),
    protocol: z.enum(["tcp", "udp"]),
    containerPath: z.string().optional(),
    hostPath: z.string().optional(),
    volumeReadOnly: z.boolean(),
  });
}

type ContainerValues = {
  name: string;
  image: string;
  hostPort: string;
  containerPort: string;
  protocol: "tcp" | "udp";
  containerPath?: string;
  hostPath?: string;
  volumeReadOnly: boolean;
};

const containerDefaults: ContainerValues = {
  name: "Media tool",
  image: "docker.io/library/nginx:latest",
  hostPort: "8088",
  containerPort: "80",
  protocol: "tcp",
  containerPath: "/data",
  hostPath: "",
  volumeReadOnly: false,
};

function RuntimeTab() {
  const containers = useContainers();
  const createContainer = useCreateContainer();
  const containerAction = useContainerAction();
  const { t } = useTranslation();
  const containerSchema = useMemo(() => createContainerSchema(t), [t]);

  const form = useForm<ContainerValues>({
    resolver: zodResolver(containerSchema),
    defaultValues: containerDefaults,
  });

  function onCreate(values: ContainerValues) {
    const ports = [
      {
        hostPort: Number(values.hostPort),
        containerPort: Number(values.containerPort),
        protocol: values.protocol,
      },
    ];
    const trimmedHost = (values.hostPath ?? "").trim();
    const volumes = trimmedHost
      ? [
          {
            hostPath: trimmedHost,
            containerPath: values.containerPath?.trim() || "/data",
            readOnly: values.volumeReadOnly,
          },
        ]
      : undefined;

    createContainer.mutate(
      { name: values.name, image: values.image, ports, volumes },
      {
        onSuccess: () => {
          toast.success(t("toast.containerCreated"));
          form.reset(containerDefaults);
        },
        onError: (error) => toast.error(errorMessage(error)),
      },
    );
  }

  function onAction(id: string, action: ContainerAction) {
    containerAction.mutate(
      { id, action },
      {
        onSuccess: () => toast.success(t("toast.containerUpdated")),
        onError: (error) => toast.error(errorMessage(error)),
      },
    );
  }

  return (
    <div className="grid gap-3 lg:grid-cols-[minmax(0,380px)_1fr]">
      <GlassCard>
        <GlassCardHeader>
          <GlassCardTitle>{t("pages.apps.runtime.customTitle")}</GlassCardTitle>
          <GlassCardDescription>
            {t("pages.apps.runtime.customDescription")}
          </GlassCardDescription>
        </GlassCardHeader>
        <GlassCardContent>
          <Form {...form}>
            <form className="space-y-4" onSubmit={form.handleSubmit(onCreate)}>
              <FormField
                control={form.control}
                name="name"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel>{t("fields.containerName")}</FormLabel>
                    <FormControl>
                      <Input placeholder="Media tool" {...field} />
                    </FormControl>
                    <FormMessage />
                  </FormItem>
                )}
              />
              <FormField
                control={form.control}
                name="image"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel>{t("fields.image")}</FormLabel>
                    <FormControl>
                      <Input
                        placeholder="docker.io/library/nginx:latest"
                        {...field}
                      />
                    </FormControl>
                    <FormMessage />
                  </FormItem>
                )}
              />
              <div className="grid grid-cols-2 gap-3">
                <FormField
                  control={form.control}
                  name="hostPort"
                  render={({ field }) => (
                    <FormItem>
                      <FormLabel>{t("fields.hostPort")}</FormLabel>
                      <FormControl>
                        <Input type="number" {...field} />
                      </FormControl>
                      <FormMessage />
                    </FormItem>
                  )}
                />
                <FormField
                  control={form.control}
                  name="containerPort"
                  render={({ field }) => (
                    <FormItem>
                      <FormLabel>{t("fields.containerPort")}</FormLabel>
                      <FormControl>
                        <Input type="number" {...field} />
                      </FormControl>
                      <FormMessage />
                    </FormItem>
                  )}
                />
              </div>
              <FormField
                control={form.control}
                name="protocol"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel>{t("fields.protocol")}</FormLabel>
                    <Select onValueChange={field.onChange} value={field.value}>
                      <FormControl>
                        <SelectTrigger className="w-full">
                          <SelectValue />
                        </SelectTrigger>
                      </FormControl>
                      <SelectContent>
                        <SelectItem value="tcp">TCP</SelectItem>
                        <SelectItem value="udp">UDP</SelectItem>
                      </SelectContent>
                    </Select>
                    <FormMessage />
                  </FormItem>
                )}
              />
              <FormField
                control={form.control}
                name="hostPath"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel>{t("fields.hostPath")}</FormLabel>
                    <FormControl>
                      <Input
                        placeholder={t("pages.apps.runtime.hostPathPlaceholder")}
                        {...field}
                      />
                    </FormControl>
                    <FormMessage />
                  </FormItem>
                )}
              />
              <FormField
                control={form.control}
                name="containerPath"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel>{t("fields.containerPath")}</FormLabel>
                    <FormControl>
                      <Input placeholder="/data" {...field} />
                    </FormControl>
                    <FormMessage />
                  </FormItem>
                )}
              />
              <FormField
                control={form.control}
                name="volumeReadOnly"
                render={({ field }) => (
                  <FormItem className="flex flex-row items-center gap-2 space-y-0">
                    <FormControl>
                      <Checkbox
                        checked={field.value}
                        onCheckedChange={field.onChange}
                      />
                    </FormControl>
                    <FormLabel className="mt-0">
                      {t("fields.readOnlyMount")}
                    </FormLabel>
                  </FormItem>
                )}
              />
              <Button
                type="submit"
                className="w-full"
                disabled={createContainer.isPending}
              >
                <Plus className="size-4" />
                {createContainer.isPending
                  ? t("pages.apps.runtime.createPending")
                  : t("pages.apps.runtime.create")}
              </Button>
            </form>
          </Form>
        </GlassCardContent>
      </GlassCard>

      <GlassCard>
        <GlassCardHeader>
          <GlassCardTitle>
            {t("pages.apps.runtime.containersTitle")}
          </GlassCardTitle>
        </GlassCardHeader>
        <GlassCardContent>
          {containers.isPending ? (
            <div className="space-y-2">
              {Array.from({ length: 3 }).map((_, index) => (
                <Skeleton key={index} className="h-16 rounded-xl" />
              ))}
            </div>
          ) : containers.data && containers.data.length > 0 ? (
            <ul className="space-y-2">
              {containers.data.map((container) => (
                <li
                  key={container.id}
                  className="flex items-center justify-between gap-3 rounded-xl border border-border/60 bg-background/30 px-3 py-2.5"
                >
                  <div className="flex min-w-0 items-center gap-3">
                    <span className="flex size-9 shrink-0 items-center justify-center rounded-lg bg-primary/10 text-primary">
                      <Boxes className="size-4" />
                    </span>
                    <div className="min-w-0">
                      <p className="truncate text-sm font-medium">
                        {container.name}
                      </p>
                      <p className="truncate font-mono text-xs text-muted-foreground">
                        {container.image}
                      </p>
                    </div>
                  </div>
                  <div className="flex shrink-0 items-center gap-2">
                    <Badge variant="secondary">{container.runtimeState}</Badge>
                    <div className="flex items-center gap-1.5">
                      <Button
                        variant="outline"
                        size="sm"
                        disabled={containerAction.isPending}
                        onClick={() => onAction(container.id, "start")}
                      >
                        <Play className="size-4" />
                        {t("pages.apps.runtime.start")}
                      </Button>
                      <Button
                        variant="outline"
                        size="sm"
                        disabled={containerAction.isPending}
                        onClick={() => onAction(container.id, "stop")}
                      >
                        <Square className="size-4" />
                        {t("pages.apps.runtime.stop")}
                      </Button>
                      <Button
                        variant="outline"
                        size="sm"
                        disabled={containerAction.isPending}
                        onClick={() => onAction(container.id, "restart")}
                      >
                        <RotateCw className="size-4" />
                        {t("pages.apps.runtime.restart")}
                      </Button>
                    </div>
                  </div>
                </li>
              ))}
            </ul>
          ) : (
            <EmptyState
              icon={Boxes}
              title={t("pages.apps.runtime.emptyTitle")}
              description={t("pages.apps.runtime.emptyDescription")}
            />
          )}
        </GlassCardContent>
      </GlassCard>
    </div>
  );
}
