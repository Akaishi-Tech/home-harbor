import { useEffect, useMemo, useState, type ReactNode } from "react";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { toast } from "sonner";
import { useTranslation } from "react-i18next";
import {
  Archive,
  ArrowRight,
  Boxes,
  Globe,
  KeyRound,
  Monitor,
  Moon,
  Plus,
  Sun,
  type LucideIcon,
} from "lucide-react";
import {
  GlassCard,
  GlassCardContent,
  GlassCardDescription,
  GlassCardHeader,
  GlassCardTitle,
} from "@/components/glass/glass-card";
import {
  ResultSecretDialog,
  type SecretField,
} from "@/components/glass/result-secret-dialog";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
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
import {
  useCatalog,
  useCreateBackup,
  useCreateSmbCredential,
  useInstallApp,
} from "@/hooks/queries";
import { useTheme, type ThemePreference } from "@/providers/theme-provider";
import { errorMessage } from "@/lib/format";
import { jsonSecret, stringSecret } from "@/lib/secrets";
import { cn } from "@/lib/utils";

/**
 * Optional post-creation onboarding step. Reached after the family space is created
 * (so the session is already authenticated), it bundles a few high-value services the
 * user usually forgets to configure: backup, remote access, SMB, recommended apps and
 * appearance. Every section is independent and skippable — finishing never requires input.
 */
export function ServicesStep({ onFinish }: { onFinish: () => void }) {
  const { t } = useTranslation();

  return (
    <div className="space-y-4">
      <p className="text-sm text-muted-foreground">
        {t("services.intro")}
      </p>
      <BackupSection />
      <RemoteSection />
      <SmbSection />
      <AppsSection />
      <AppearanceSection />
      <Button type="button" className="w-full" onClick={onFinish}>
        <ArrowRight className="size-4" />
        {t("services.finish")}
      </Button>
    </div>
  );
}

function SectionCard({
  icon: Icon,
  title,
  description,
  children,
}: {
  icon: LucideIcon;
  title: string;
  description: string;
  children: ReactNode;
}) {
  return (
    <GlassCard>
      <GlassCardHeader>
        <GlassCardTitle className="flex items-center gap-2 text-base">
          <Icon className="size-4 text-primary" />
          {title}
        </GlassCardTitle>
        <GlassCardDescription>{description}</GlassCardDescription>
      </GlassCardHeader>
      <GlassCardContent>{children}</GlassCardContent>
    </GlassCard>
  );
}

type BackupValues = {
  repositoryUri: string;
};

const backupDefaults: BackupValues = {
  repositoryUri: "file:///mnt/homeharbor-backup",
};

function BackupSection() {
  const createBackup = useCreateBackup();
  const { t } = useTranslation();
  const backupSchema = useMemo(
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
    resolver: zodResolver(backupSchema),
    defaultValues: backupDefaults,
  });

  function onSubmit(values: BackupValues) {
    createBackup.mutate(values, {
      onSuccess: () => {
        toast.success(t("toast.backupCreated"));
        form.reset(backupDefaults);
      },
      onError: (error) => toast.error(errorMessage(error)),
    });
  }

  return (
    <SectionCard
      icon={Archive}
      title={t("services.backup.title")}
      description={t("services.backup.description")}
    >
      <Form {...form}>
        <form className="space-y-3" onSubmit={form.handleSubmit(onSubmit)}>
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
            variant="outline"
            disabled={createBackup.isPending}
          >
            <Plus className="size-4" />
            {createBackup.isPending
              ? t("services.backup.createPending")
              : t("services.backup.create")}
          </Button>
        </form>
      </Form>
    </SectionCard>
  );
}

function RemoteSection() {
  const { t } = useTranslation();

  return (
    <SectionCard
      icon={Globe}
      title={t("services.remote.title")}
      description={t("services.remote.description")}
    >
      <p className="rounded-xl border border-warning/40 bg-warning/10 px-3 py-2 text-sm text-warning-foreground">
        {t("services.remote.unavailable")}
      </p>
    </SectionCard>
  );
}

type SmbValues = {
  displayName: string;
};

const smbDefaults: SmbValues = { displayName: "Laptop SMB" };

function SmbSection() {
  const createCredential = useCreateSmbCredential();
  const [secrets, setSecrets] = useState<SecretField[] | null>(null);
  const { t } = useTranslation();
  const smbSchema = useMemo(
    () =>
      z.object({
        displayName: z
          .string()
          .trim()
          .min(1, t("validation.credentialNameRequired")),
      }),
    [t],
  );
  const form = useForm<SmbValues>({
    resolver: zodResolver(smbSchema),
    defaultValues: smbDefaults,
  });

  function onSubmit(values: SmbValues) {
    // Omitting shareId tells the backend to auto-create the default SMB share.
    createCredential.mutate(
      { displayName: values.displayName, readOnly: false },
      {
        onSuccess: (response) => {
          toast.success(t("toast.smbCredentialGenerated"));
          const found = [
            ...stringSecret(response, "username", t("secretLabels.username")),
            ...stringSecret(response, "password", t("secretLabels.smbPassword")),
          ];
          setSecrets(
            found.length
              ? found
              : jsonSecret(response, t("secretLabels.credentials")),
          );
          form.reset(smbDefaults);
          createCredential.reset();
        },
        onError: (error) => {
          toast.error(errorMessage(error));
          createCredential.reset();
        },
      },
    );
  }

  return (
    <SectionCard
      icon={KeyRound}
      title={t("services.smb.title")}
      description={t("services.smb.description")}
    >
      <Form {...form}>
        <form className="space-y-3" onSubmit={form.handleSubmit(onSubmit)}>
          <FormField
            control={form.control}
            name="displayName"
                render={({ field }) => (
                  <FormItem>
                <FormLabel>{t("fields.credentialName")}</FormLabel>
                <FormControl>
                  <Input placeholder="Laptop SMB" {...field} />
                </FormControl>
                <FormMessage />
              </FormItem>
            )}
          />
          <Button
            type="submit"
            variant="outline"
            disabled={createCredential.isPending}
          >
            <KeyRound className="size-4" />
            {createCredential.isPending
              ? t("services.smb.generatePending")
              : t("services.smb.generateAuto")}
          </Button>
        </form>
      </Form>

      <ResultSecretDialog
        open={secrets !== null}
        onOpenChange={(open) => {
          if (!open) setSecrets(null);
        }}
        title={t("services.smb.title")}
        description={t("services.smb.dialogDescription")}
        secrets={secrets ?? []}
      />
    </SectionCard>
  );
}

type AppsValues = {
  appKey: string;
};

function AppsSection() {
  const catalog = useCatalog();
  const installApp = useInstallApp();
  const { t } = useTranslation();
  const appsSchema = useMemo(
    () =>
      z.object({
        appKey: z.string().min(1, t("validation.appRequired")),
      }),
    [t],
  );
  const form = useForm<AppsValues>({
    resolver: zodResolver(appsSchema),
    defaultValues: { appKey: "" },
  });

  const catalogItems = (catalog.data ?? []).filter(
    (item) =>
      item.kind === "container" &&
      item.recommendedInSetup &&
      item.available &&
      !item.installed,
  );
  const firstAppKey = catalogItems[0]?.appKey ?? "";

  useEffect(() => {
    const selected = form.getValues("appKey");
    if (!catalogItems.some((item) => item.appKey === selected)) {
      form.setValue("appKey", firstAppKey);
    }
  }, [catalogItems, firstAppKey, form]);

  function onInstall(values: AppsValues) {
    installApp.mutate(values, {
      onSuccess: () => {
        toast.success(t("toast.appInstallQueued"));
        form.reset({ appKey: firstAppKey });
      },
      onError: (error) => toast.error(errorMessage(error)),
    });
  }

  return (
    <SectionCard
      icon={Boxes}
      title={t("services.apps.title")}
      description={t("services.apps.description")}
    >
      <Form {...form}>
        <form className="space-y-3" onSubmit={form.handleSubmit(onInstall)}>
          <FormField
            control={form.control}
            name="appKey"
                render={({ field }) => (
                  <FormItem>
                <FormLabel>{t("fields.app")}</FormLabel>
                <Select
                  onValueChange={field.onChange}
                  value={field.value}
                  disabled={catalogItems.length === 0}
                >
                  <FormControl>
                    <SelectTrigger className="w-full">
                      <SelectValue placeholder={t("services.apps.placeholder")} />
                    </SelectTrigger>
                  </FormControl>
                  <SelectContent>
                    {catalogItems.map((item) => (
                      <SelectItem key={item.appKey} value={item.appKey}>
                        {item.displayName}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
                <FormMessage />
              </FormItem>
            )}
          />
          {catalogItems.length === 0 && !catalog.isPending ? (
            <p className="text-sm text-muted-foreground">
              {t("services.apps.empty")}
            </p>
          ) : null}
          <Button
            type="submit"
            variant="outline"
            disabled={installApp.isPending || catalogItems.length === 0}
          >
            <Plus className="size-4" />
            {installApp.isPending
              ? t("services.apps.queued")
              : t("services.apps.install")}
          </Button>
        </form>
      </Form>
    </SectionCard>
  );
}

const THEME_OPTIONS: Array<{
  value: ThemePreference;
  labelKey: string;
  icon: LucideIcon;
}> = [
  { value: "light", labelKey: "common.light", icon: Sun },
  { value: "dark", labelKey: "common.dark", icon: Moon },
  { value: "system", labelKey: "common.system", icon: Monitor },
];

function AppearanceSection() {
  const { preference, setPreference } = useTheme();
  const { t } = useTranslation();

  return (
    <SectionCard
      icon={Sun}
      title={t("services.appearance.title")}
      description={t("services.appearance.description")}
    >
      <div className="grid grid-cols-3 gap-2">
        {THEME_OPTIONS.map((option) => {
          const Icon = option.icon;
          const active = preference === option.value;
          return (
            <button
              key={option.value}
              type="button"
              onClick={() => setPreference(option.value)}
              aria-pressed={active}
              className={cn(
                "flex flex-col items-center gap-1.5 rounded-xl border px-3 py-3 text-sm transition-colors",
                active
                  ? "border-primary/50 bg-primary/10 text-primary"
                  : "border-border/60 bg-background/30 hover:bg-background/50",
              )}
            >
              <Icon className="size-4" />
              {t(option.labelKey)}
            </button>
          );
        })}
      </div>
    </SectionCard>
  );
}
