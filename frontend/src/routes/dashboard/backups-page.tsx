import { useMemo } from "react";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { toast } from "sonner";
import { Archive, Plus } from "lucide-react";
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
import { useBackupTargets, useCreateBackup } from "@/hooks/queries";
import { errorMessage } from "@/lib/format";

type BackupValues = {
  repositoryUri: string;
};

const defaults: BackupValues = {
  repositoryUri: "file:///mnt/homeharbor-backup",
};

export function BackupsPage() {
  const targets = useBackupTargets();
  const createBackup = useCreateBackup();
  const { t } = useTranslation();
  const schema = useMemo(
    () =>
      z.object({
        repositoryUri: z
          .string()
          .trim()
          .min(1, t("validation.backupRepositoryRequired")),
      }),
    [t],
  );

  const form = useForm<BackupValues>({
    resolver: zodResolver(schema),
    defaultValues: defaults,
  });

  function onSubmit(values: BackupValues) {
    createBackup.mutate(values, {
      onSuccess: () => {
        toast.success(t("toast.backupCreated"));
        form.reset(defaults);
      },
      onError: (error) => toast.error(errorMessage(error)),
    });
  }

  return (
    <div className="space-y-6">
      <SectionHeader
        eyebrow={t("pages.backups.eyebrow")}
        title={t("pages.backups.title")}
        description={t("pages.backups.description")}
      />

      <div className="grid gap-3 lg:grid-cols-[minmax(0,380px)_1fr]">
        <GlassCard>
          <GlassCardHeader>
            <GlassCardTitle>{t("pages.backups.createTitle")}</GlassCardTitle>
            <GlassCardDescription>
              {t("pages.backups.createDescription")}
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
                  name="repositoryUri"
                  render={({ field }) => (
                    <FormItem>
                      <FormLabel>{t("fields.backupRepository")}</FormLabel>
                      <FormControl>
                        <Input
                          placeholder="file:///mnt/homeharbor-backup"
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
                  disabled={createBackup.isPending}
                >
                  <Plus className="size-4" />
                  {createBackup.isPending
                    ? t("pages.backups.createPending")
                    : t("pages.backups.create")}
                </Button>
              </form>
            </Form>
          </GlassCardContent>
        </GlassCard>

        <GlassCard>
          <GlassCardHeader>
            <GlassCardTitle>{t("pages.backups.targetsTitle")}</GlassCardTitle>
          </GlassCardHeader>
          <GlassCardContent>
            {targets.isPending ? (
              <div className="space-y-2">
                {Array.from({ length: 3 }).map((_, index) => (
                  <Skeleton key={index} className="h-14 rounded-xl" />
                ))}
              </div>
            ) : targets.data && targets.data.length > 0 ? (
              <ul className="space-y-2">
                {targets.data.map((target) => (
                  <li
                    key={target.id}
                    className="flex items-center justify-between gap-3 rounded-xl border border-border/60 bg-background/30 px-3 py-2.5"
                  >
                    <div className="flex min-w-0 items-center gap-3">
                      <span className="flex size-9 shrink-0 items-center justify-center rounded-lg bg-primary/10 text-primary">
                        <Archive className="size-4" />
                      </span>
                      <div className="min-w-0">
                        <p className="truncate text-sm font-medium">
                          {target.name}
                        </p>
                        <p className="break-all font-mono text-xs text-muted-foreground">
                          {target.repositoryUri}
                        </p>
                      </div>
                    </div>
                  </li>
                ))}
              </ul>
            ) : (
              <EmptyState
                icon={Archive}
                title={t("pages.backups.emptyTitle")}
                description={t("pages.backups.emptyDescription")}
              />
            )}
          </GlassCardContent>
        </GlassCard>
      </div>
    </div>
  );
}
