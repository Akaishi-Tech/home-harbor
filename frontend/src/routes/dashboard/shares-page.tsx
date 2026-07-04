import { useMemo, useState } from "react";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { toast } from "sonner";
import { HardDrive, Plus, KeyRound, Trash2 } from "lucide-react";
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
import {
  useCreateSmbCredential,
  useCreateSmbShare,
  useRevokeSmbCredential,
  useSmbCredentials,
  useSmbShares,
} from "@/hooks/queries";
import { errorMessage } from "@/lib/format";
import { jsonSecret, stringSecret } from "@/lib/secrets";

type ShareValues = {
  name: string;
  shareName: string;
  readOnly: boolean;
};

const shareDefaults: ShareValues = {
  name: "Family data",
  shareName: "homeharbor",
  readOnly: false,
};

type CredentialValues = {
  shareId: string;
  displayName: string;
  readOnly: boolean;
};

const credentialDefaults: CredentialValues = {
  shareId: "auto",
  displayName: "Laptop SMB",
  readOnly: false,
};

export function SharesPage() {
  const shares = useSmbShares();
  const credentials = useSmbCredentials();
  const createShare = useCreateSmbShare();
  const createCredential = useCreateSmbCredential();
  const revokeCredential = useRevokeSmbCredential();
  const [secrets, setSecrets] = useState<SecretField[] | null>(null);
  const { t } = useTranslation();
  const shareSchema = useMemo(
    () =>
      z.object({
        name: z.string().trim().min(1, t("validation.shareNameRequired")),
        shareName: z.string().trim().min(1, t("validation.shareSlugRequired")),
        readOnly: z.boolean(),
      }),
    [t],
  );
  const credentialSchema = useMemo(
    () =>
      z.object({
        shareId: z.string().min(1),
        displayName: z
          .string()
          .trim()
          .min(1, t("validation.credentialNameRequired")),
        readOnly: z.boolean(),
      }),
    [t],
  );

  const shareForm = useForm<ShareValues>({
    resolver: zodResolver(shareSchema),
    defaultValues: shareDefaults,
  });

  const credentialForm = useForm<CredentialValues>({
    resolver: zodResolver(credentialSchema),
    defaultValues: credentialDefaults,
  });

  function onCreateShare(values: ShareValues) {
    createShare.mutate(values, {
      onSuccess: () => {
        toast.success(t("toast.shareUpdated"));
        shareForm.reset(shareDefaults);
      },
      onError: (error) => toast.error(errorMessage(error)),
    });
  }

  function onCreateCredential(values: CredentialValues) {
    const shareId = values.shareId === "auto" ? undefined : values.shareId;
    createCredential.mutate(
      { shareId, displayName: values.displayName, readOnly: values.readOnly },
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
          credentialForm.reset(credentialDefaults);
        },
        onError: (error) => toast.error(errorMessage(error)),
      },
    );
  }

  return (
    <div className="space-y-6">
      <SectionHeader
        eyebrow={t("pages.shares.eyebrow")}
        title={t("pages.shares.title")}
        description={t("pages.shares.description")}
      />

      <div className="grid gap-3 lg:grid-cols-[minmax(0,380px)_1fr]">
        <div className="space-y-3">
          <GlassCard>
            <GlassCardHeader>
              <GlassCardTitle>{t("pages.shares.newShare")}</GlassCardTitle>
              <GlassCardDescription>
                {t("pages.shares.newShareDescription")}
              </GlassCardDescription>
            </GlassCardHeader>
            <GlassCardContent>
              <Form {...shareForm}>
                <form
                  className="space-y-4"
                  onSubmit={shareForm.handleSubmit(onCreateShare)}
                >
                  <FormField
                    control={shareForm.control}
                    name="name"
                      render={({ field }) => (
                        <FormItem>
                        <FormLabel>{t("fields.shareName")}</FormLabel>
                        <FormControl>
                          <Input placeholder="Family data" {...field} />
                        </FormControl>
                        <FormMessage />
                      </FormItem>
                    )}
                  />
                  <FormField
                    control={shareForm.control}
                    name="shareName"
                      render={({ field }) => (
                        <FormItem>
                        <FormLabel>{t("fields.shareSlug")}</FormLabel>
                        <FormControl>
                          <Input placeholder="homeharbor" {...field} />
                        </FormControl>
                        <FormMessage />
                      </FormItem>
                    )}
                  />
                  <FormField
                    control={shareForm.control}
                    name="readOnly"
                    render={({ field }) => (
                      <FormItem className="flex flex-row items-center gap-2 space-y-0">
                        <FormControl>
                          <Checkbox
                            checked={field.value}
                            onCheckedChange={field.onChange}
                          />
                        </FormControl>
                        <FormLabel className="mt-0">
                          {t("fields.readOnly")}
                        </FormLabel>
                      </FormItem>
                    )}
                  />
                  <Button
                    type="submit"
                    className="w-full"
                    disabled={createShare.isPending}
                  >
                    <Plus className="size-4" />
                    {createShare.isPending
                      ? t("pages.shares.savePending")
                      : t("pages.shares.saveShare")}
                  </Button>
                </form>
              </Form>
            </GlassCardContent>
          </GlassCard>

          <GlassCard>
            <GlassCardHeader>
              <GlassCardTitle>
                {t("pages.shares.configuredShares")}
              </GlassCardTitle>
            </GlassCardHeader>
            <GlassCardContent>
              {shares.isPending ? (
                <div className="space-y-2">
                  {Array.from({ length: 2 }).map((_, index) => (
                    <Skeleton key={index} className="h-16 rounded-xl" />
                  ))}
                </div>
              ) : shares.data && shares.data.length > 0 ? (
                <ul className="space-y-2">
                  {shares.data.map((share) => (
                    <li
                      key={share.id}
                      className="flex items-center justify-between gap-3 rounded-xl border border-border/60 bg-background/30 px-3 py-2.5"
                    >
                      <div className="flex min-w-0 items-center gap-3">
                        <span className="flex size-9 shrink-0 items-center justify-center rounded-lg bg-primary/10 text-primary">
                          <HardDrive className="size-4" />
                        </span>
                        <div className="min-w-0">
                          <p className="truncate text-sm font-medium">
                            {share.shareName}
                          </p>
                          {share.unc ? (
                            <p className="truncate font-mono text-xs text-muted-foreground">
                              {share.unc}
                            </p>
                          ) : null}
                        </div>
                      </div>
                      <div className="flex shrink-0 items-center gap-2">
                        <span className="text-xs text-muted-foreground">
                          {t("common.countCredentials", {
                            count: share.credentialCount,
                          })}
                        </span>
                        <Badge variant="secondary">{share.runtimeState}</Badge>
                      </div>
                    </li>
                  ))}
                </ul>
              ) : (
                <EmptyState
                  icon={HardDrive}
                  title={t("pages.shares.noSharesTitle")}
                  description={t("pages.shares.noSharesDescription")}
                />
              )}
            </GlassCardContent>
          </GlassCard>
        </div>

        <div className="space-y-3">
          <GlassCard>
            <GlassCardHeader>
              <GlassCardTitle>
                {t("pages.shares.generateCredential")}
              </GlassCardTitle>
              <GlassCardDescription>
                {t("pages.shares.generateCredentialDescription")}
              </GlassCardDescription>
            </GlassCardHeader>
            <GlassCardContent>
              <Form {...credentialForm}>
                <form
                  className="space-y-4"
                  onSubmit={credentialForm.handleSubmit(onCreateCredential)}
                >
                  <FormField
                    control={credentialForm.control}
                    name="shareId"
                      render={({ field }) => (
                        <FormItem>
                        <FormLabel>{t("fields.targetShare")}</FormLabel>
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
                            <SelectItem value="auto">
                              {t("pages.shares.autoCreate")}
                            </SelectItem>
                            {shares.data?.map((share) => (
                              <SelectItem key={share.id} value={share.id}>
                                {share.shareName}
                              </SelectItem>
                            ))}
                          </SelectContent>
                        </Select>
                        <FormMessage />
                      </FormItem>
                    )}
                  />
                  <FormField
                    control={credentialForm.control}
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
                  <FormField
                    control={credentialForm.control}
                    name="readOnly"
                    render={({ field }) => (
                      <FormItem className="flex flex-row items-center gap-2 space-y-0">
                        <FormControl>
                          <Checkbox
                            checked={field.value}
                            onCheckedChange={field.onChange}
                          />
                        </FormControl>
                        <FormLabel className="mt-0">
                          {t("fields.readOnly")}
                        </FormLabel>
                      </FormItem>
                    )}
                  />
                  <Button
                    type="submit"
                    className="w-full"
                    disabled={createCredential.isPending}
                  >
                    <KeyRound className="size-4" />
                    {createCredential.isPending
                      ? t("pages.shares.generatePending")
                      : t("pages.shares.generateCredential")}
                  </Button>
                </form>
              </Form>
            </GlassCardContent>
          </GlassCard>

          <GlassCard>
            <GlassCardHeader>
              <GlassCardTitle>
                {t("pages.shares.credentialsTitle")}
              </GlassCardTitle>
            </GlassCardHeader>
            <GlassCardContent>
              {credentials.isPending ? (
                <div className="space-y-2">
                  {Array.from({ length: 3 }).map((_, index) => (
                    <Skeleton key={index} className="h-14 rounded-xl" />
                  ))}
                </div>
              ) : credentials.data && credentials.data.length > 0 ? (
                <ul className="space-y-2">
                  {credentials.data.map((credential) => (
                    <li
                      key={credential.id}
                      className="flex items-center justify-between gap-3 rounded-xl border border-border/60 bg-background/30 px-3 py-2.5"
                    >
                      <div className="flex min-w-0 items-center gap-3">
                        <span className="flex size-9 shrink-0 items-center justify-center rounded-lg bg-primary/10 text-primary">
                          <KeyRound className="size-4" />
                        </span>
                        <div className="min-w-0">
                          <p className="truncate text-sm font-medium">
                            {credential.displayName}
                          </p>
                          <p className="truncate font-mono text-xs text-muted-foreground">
                            {credential.username}
                          </p>
                        </div>
                      </div>
                      <div className="flex shrink-0 items-center gap-2">
                        <Badge variant="secondary">
                          {credential.runtimeState}
                        </Badge>
                        <Button
                          type="button"
                          variant="ghost"
                          size="sm"
                          className="text-destructive hover:text-destructive"
                          disabled={revokeCredential.isPending}
                          onClick={() =>
                            revokeCredential.mutate(credential.id, {
                              onSuccess: () =>
                                toast.success(t("toast.smbCredentialRevoked")),
                              onError: (error) =>
                                toast.error(errorMessage(error)),
                            })
                          }
                        >
                          <Trash2 className="size-4" />
                          {t("pages.shares.revoke")}
                        </Button>
                      </div>
                    </li>
                  ))}
                </ul>
              ) : (
                <EmptyState
                  icon={KeyRound}
                  title={t("pages.shares.noCredentialsTitle")}
                  description={t("pages.shares.noCredentialsDescription")}
                />
              )}
            </GlassCardContent>
          </GlassCard>
        </div>
      </div>

      <ResultSecretDialog
        open={secrets !== null}
        onOpenChange={(open) => {
          if (!open) setSecrets(null);
        }}
        title={t("pages.shares.dialogTitle")}
        description={t("pages.shares.dialogDescription")}
        secrets={secrets ?? []}
      />
    </div>
  );
}
