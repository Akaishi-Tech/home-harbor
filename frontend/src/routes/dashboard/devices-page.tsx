import { useMemo, useState } from "react";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { toast } from "sonner";
import { Smartphone, Plus } from "lucide-react";
import { useTranslation } from "react-i18next";
import { SectionHeader } from "@/components/glass/section-header";
import {
  GlassCard,
  GlassCardContent,
  GlassCardDescription,
  GlassCardHeader,
  GlassCardTitle,
} from "@/components/glass/glass-card";
import { EmptyState } from "@/components/glass/empty-state";
import { QueryErrorState } from "@/components/glass/query-error-state";
import {
  ResultSecretDialog,
  type SecretField,
} from "@/components/glass/result-secret-dialog";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Badge } from "@/components/ui/badge";
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
import { useCreateDevice, useDevices } from "@/hooks/queries";
import { errorMessage, formatDateTime } from "@/lib/format";
import { webDavSecrets } from "@/lib/secrets";
import { useAuth } from "@/hooks/use-auth";
import { isFamilyAdmin } from "@/lib/auth";

type DeviceValues = {
  displayName: string;
  kind: "mobile" | "desktop" | "tablet";
};

export function DevicesPage() {
  const devices = useDevices();
  const createDevice = useCreateDevice();
  const canManage = isFamilyAdmin(useAuth());
  const [secrets, setSecrets] = useState<SecretField[] | null>(null);
  const { t } = useTranslation();
  const schema = useMemo(
    () =>
      z.object({
        displayName: z.string().trim().min(1, t("validation.deviceNameRequired")),
        kind: z.enum(["mobile", "desktop", "tablet"]),
      }),
    [t],
  );

  const form = useForm<DeviceValues>({
    resolver: zodResolver(schema),
    defaultValues: { displayName: "Phone", kind: "mobile" },
  });

  function onSubmit(values: DeviceValues) {
    createDevice.mutate(values, {
      onSuccess: (response) => {
        toast.success(t("toast.deviceCredentialsGenerated"));
        const found = webDavSecrets(response);
        if (found.length) setSecrets(found);
        form.reset({ displayName: "Phone", kind: "mobile" });
        createDevice.reset();
      },
      onError: (error) => {
        toast.error(errorMessage(error));
        createDevice.reset();
      },
    });
  }

  return (
    <div className="space-y-6">
      <SectionHeader
        eyebrow={t("pages.devices.eyebrow")}
        title={t("pages.devices.title")}
        description={t("pages.devices.description")}
      />

      <div
        className={
          canManage
            ? "grid gap-3 lg:grid-cols-[minmax(0,380px)_1fr]"
            : "grid gap-3"
        }
      >
        {canManage ? <GlassCard>
          <GlassCardHeader>
            <GlassCardTitle>{t("pages.devices.onboardTitle")}</GlassCardTitle>
            <GlassCardDescription>
              {t("pages.devices.onboardDescription")}
            </GlassCardDescription>
          </GlassCardHeader>
          <GlassCardContent>
            <Form {...form}>
              <form
                className="space-y-4"
                onSubmit={form.handleSubmit(onSubmit)}
              >
                <FormField
                  control={form.control}
                  name="displayName"
                  render={({ field }) => (
                    <FormItem>
                      <FormLabel>{t("fields.deviceName")}</FormLabel>
                      <FormControl>
                        <Input placeholder="Phone" {...field} />
                      </FormControl>
                      <FormMessage />
                    </FormItem>
                  )}
                />
                <FormField
                  control={form.control}
                  name="kind"
                  render={({ field }) => (
                    <FormItem>
                      <FormLabel>{t("fields.type")}</FormLabel>
                      <Select
                        onValueChange={field.onChange}
                        value={field.value}
                      >
                        <FormControl>
                          <SelectTrigger className="w-full">
                            <SelectValue />
                          </SelectTrigger>
                        </FormControl>
                        <SelectContent>
                          <SelectItem value="mobile">
                            {t("deviceKinds.mobile")}
                          </SelectItem>
                          <SelectItem value="desktop">
                            {t("deviceKinds.desktop")}
                          </SelectItem>
                          <SelectItem value="tablet">
                            {t("deviceKinds.tablet")}
                          </SelectItem>
                        </SelectContent>
                      </Select>
                      <FormMessage />
                    </FormItem>
                  )}
                />
                <Button
                  type="submit"
                  className="w-full"
                  disabled={createDevice.isPending}
                >
                  <Plus className="size-4" />
                  {createDevice.isPending
                    ? t("pages.devices.generatePending")
                    : t("pages.devices.generate")}
                </Button>
              </form>
            </Form>
          </GlassCardContent>
        </GlassCard> : null}

        <GlassCard>
          <GlassCardHeader>
            <GlassCardTitle>{t("pages.devices.onboarded")}</GlassCardTitle>
          </GlassCardHeader>
          <GlassCardContent>
            {devices.isPending ? (
              <div className="space-y-2">
                {Array.from({ length: 3 }).map((_, index) => (
                  <Skeleton key={index} className="h-14 rounded-xl" />
                ))}
              </div>
            ) : devices.isError ? (
              <QueryErrorState
                error={devices.error}
                onRetry={() => void devices.refetch()}
              />
            ) : devices.data && devices.data.length > 0 ? (
              <ul className="space-y-2">
                {devices.data.map((device) => (
                  <li
                    key={device.id}
                    className="flex items-center justify-between gap-3 rounded-xl border border-border/60 bg-background/30 px-3 py-2.5"
                  >
                    <div className="flex min-w-0 items-center gap-3">
                      <span className="flex size-9 shrink-0 items-center justify-center rounded-lg bg-primary/10 text-primary">
                        <Smartphone className="size-4" />
                      </span>
                      <div className="min-w-0">
                        <p className="truncate text-sm font-medium">
                          {device.displayName}
                        </p>
                        <p className="text-xs text-muted-foreground">
                          {t("pages.devices.lastActive", {
                            date: formatDateTime(device.lastSeenAt),
                          })}
                        </p>
                      </div>
                    </div>
                    <Badge variant="secondary">
                      {t(`deviceKinds.${device.kind}`, {
                        defaultValue: device.kind,
                      })}
                    </Badge>
                  </li>
                ))}
              </ul>
            ) : (
              <EmptyState
                icon={Smartphone}
                title={t("pages.devices.emptyTitle")}
                description={t("pages.devices.emptyDescription")}
              />
            )}
          </GlassCardContent>
        </GlassCard>
      </div>

      <ResultSecretDialog
        open={secrets !== null}
        onOpenChange={(open) => {
          if (!open) setSecrets(null);
        }}
        title={t("pages.devices.dialogTitle")}
        description={t("pages.devices.dialogDescription")}
        secrets={secrets ?? []}
      />
    </div>
  );
}
