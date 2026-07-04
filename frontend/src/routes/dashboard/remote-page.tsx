import { useMemo, useState } from "react";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { toast } from "sonner";
import { Globe, Plus } from "lucide-react";
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
import {
  ResultSecretDialog,
  type SecretField,
} from "@/components/glass/result-secret-dialog";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import {
  Form,
  FormControl,
  FormField,
  FormItem,
  FormLabel,
  FormMessage,
} from "@/components/ui/form";
import { useCreatePeer, usePeers } from "@/hooks/queries";
import { errorMessage } from "@/lib/format";
import { jsonSecret, stringSecret } from "@/lib/secrets";

type RemoteValues = {
  name: string;
  endpoint: string;
};

const defaults: RemoteValues = {
  name: "Travel phone",
  endpoint: "homeharbor.local:51820",
};

export function RemotePage() {
  const peers = usePeers();
  const createPeer = useCreatePeer();
  const [secrets, setSecrets] = useState<SecretField[] | null>(null);
  const { t } = useTranslation();
  const schema = useMemo(
    () =>
      z.object({
        name: z.string().trim().min(1, t("validation.deviceNameRequired")),
        endpoint: z.string().trim().min(1, t("validation.endpointRequired")),
      }),
    [t],
  );

  const form = useForm<RemoteValues>({
    resolver: zodResolver(schema),
    defaultValues: defaults,
  });

  function onSubmit(values: RemoteValues) {
    createPeer.mutate(values, {
      onSuccess: (response) => {
        toast.success(t("toast.remoteConfigGenerated"));
        const found = stringSecret(
          response,
          "config",
          t("secretLabels.wireGuardConfig"),
          true,
        );
        setSecrets(
          found.length
            ? found
            : jsonSecret(response, t("secretLabels.config")),
        );
        form.reset(defaults);
      },
      onError: (error) => toast.error(errorMessage(error)),
    });
  }

  return (
    <div className="space-y-6">
      <SectionHeader
        eyebrow={t("pages.remote.eyebrow")}
        title={t("pages.remote.title")}
        description={t("pages.remote.description")}
      />

      <div className="grid gap-3 lg:grid-cols-[minmax(0,380px)_1fr]">
        <GlassCard>
          <GlassCardHeader>
            <GlassCardTitle>{t("pages.remote.createTitle")}</GlassCardTitle>
            <GlassCardDescription>
              {t("pages.remote.createDescription")}
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
                  name="name"
                  render={({ field }) => (
                    <FormItem>
                      <FormLabel>{t("fields.deviceName")}</FormLabel>
                      <FormControl>
                        <Input placeholder="Travel phone" {...field} />
                      </FormControl>
                      <FormMessage />
                    </FormItem>
                  )}
                />
                <FormField
                  control={form.control}
                  name="endpoint"
                  render={({ field }) => (
                    <FormItem>
                      <FormLabel>{t("fields.endpoint")}</FormLabel>
                      <FormControl>
                        <Input
                          placeholder="homeharbor.local:51820"
                          {...field}
                        />
                      </FormControl>
                      <FormMessage />
                    </FormItem>
                  )}
                />
                <Button
                  type="submit"
                  className="w-full"
                  disabled={createPeer.isPending}
                >
                  <Plus className="size-4" />
                  {createPeer.isPending
                    ? t("pages.remote.createPending")
                    : t("pages.remote.create")}
                </Button>
              </form>
            </Form>
          </GlassCardContent>
        </GlassCard>

        <GlassCard>
          <GlassCardHeader>
            <GlassCardTitle>{t("pages.remote.peersTitle")}</GlassCardTitle>
          </GlassCardHeader>
          <GlassCardContent>
            {peers.isPending ? (
              <div className="space-y-2">
                {Array.from({ length: 3 }).map((_, index) => (
                  <Skeleton key={index} className="h-14 rounded-xl" />
                ))}
              </div>
            ) : peers.data && peers.data.length > 0 ? (
              <ul className="space-y-2">
                {peers.data.map((peer) => (
                  <li
                    key={peer.id}
                    className="flex items-center justify-between gap-3 rounded-xl border border-border/60 bg-background/30 px-3 py-2.5"
                  >
                    <div className="flex min-w-0 items-center gap-3">
                      <span className="flex size-9 shrink-0 items-center justify-center rounded-lg bg-primary/10 text-primary">
                        <Globe className="size-4" />
                      </span>
                      <div className="min-w-0">
                        <p className="truncate text-sm font-medium">
                          {peer.name}
                        </p>
                        <p className="font-mono text-xs text-muted-foreground">
                          {peer.address}
                        </p>
                      </div>
                    </div>
                  </li>
                ))}
              </ul>
            ) : (
              <EmptyState
                icon={Globe}
                title={t("pages.remote.emptyTitle")}
                description={t("pages.remote.emptyDescription")}
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
        title={t("pages.remote.dialogTitle")}
        description={t("pages.remote.dialogDescription")}
        secrets={secrets ?? []}
      />
    </div>
  );
}
