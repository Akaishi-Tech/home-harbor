import { useMemo, useState } from "react";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { toast } from "sonner";
import { Users, UserPlus } from "lucide-react";
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
import {
  useCreateMember,
  useMembers,
  useRotateRecoveryCode,
} from "@/hooks/queries";
import { errorMessage } from "@/lib/format";
import { useAuth } from "@/hooks/use-auth";
import { isFamilyAdmin, isFamilyOwner } from "@/lib/auth";
import { jsonSecret, stringSecret } from "@/lib/secrets";

type MemberValues = {
  displayName: string;
  role: "member" | "admin" | "owner";
  password: string;
};

type RecoveryCodeValues = {
  currentPassword: string;
};

const defaults: MemberValues = {
  displayName: "Alex",
  role: "member",
  password: "",
};

export function FamilyPage() {
  const members = useMembers();
  const createMember = useCreateMember();
  const rotateRecoveryCode = useRotateRecoveryCode();
  const auth = useAuth();
  const canManage = isFamilyAdmin(auth);
  const canOwn = isFamilyOwner(auth);
  const [recoverySecrets, setRecoverySecrets] = useState<SecretField[] | null>(
    null,
  );
  const { t } = useTranslation();
  const schema = useMemo(
    () =>
      z.object({
        displayName: z
          .string()
          .trim()
          .min(1, t("validation.nameRequired"))
          .max(96, t("validation.nameTooLong")),
        role: z.enum(["member", "admin", "owner"]),
        password: z
          .string()
          .min(12, t("validation.passwordLength"))
          .max(128, t("validation.passwordLength")),
      }),
    [t],
  );

  const form = useForm<MemberValues>({
    resolver: zodResolver(schema),
    defaultValues: defaults,
  });
  const recoveryForm = useForm<RecoveryCodeValues>({
    defaultValues: { currentPassword: "" },
  });

  function onSubmit(values: MemberValues) {
    createMember.mutate(values, {
      onSuccess: () => {
        toast.success(t("toast.memberAdded"));
        form.reset(defaults);
        createMember.reset();
      },
      onError: (error) => {
        toast.error(errorMessage(error));
        createMember.reset();
      },
    });
  }

  function onRotateRecoveryCode(values: RecoveryCodeValues) {
    rotateRecoveryCode.mutate(values, {
      onSuccess: (response) => {
        const found = stringSecret(
          response,
          "recoveryCode",
          t("secretLabels.recoveryCode"),
        );
        setRecoverySecrets(
          found.length
            ? found
            : jsonSecret(response, t("secretLabels.recoveryCode")),
        );
        recoveryForm.reset();
        rotateRecoveryCode.reset();
        toast.success(t("toast.recoveryCodeRotated"));
      },
      onError: (error) => {
        recoveryForm.reset();
        rotateRecoveryCode.reset();
        toast.error(errorMessage(error));
      },
    });
  }

  return (
    <div className="space-y-6">
      <SectionHeader
        eyebrow={t("pages.family.eyebrow")}
        title={t("pages.family.title")}
        description={t("pages.family.description")}
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
            <GlassCardTitle>{t("pages.family.addTitle")}</GlassCardTitle>
            <GlassCardDescription>
              {t("pages.family.addDescription")}
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
                      <FormLabel>{t("fields.name")}</FormLabel>
                      <FormControl>
                        <Input maxLength={96} placeholder="Alex" {...field} />
                      </FormControl>
                      <FormMessage />
                    </FormItem>
                  )}
                />
                <FormField
                  control={form.control}
                  name="role"
                  render={({ field }) => (
                    <FormItem>
                      <FormLabel>{t("fields.role")}</FormLabel>
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
                          <SelectItem value="member">
                            {t("roles.member")}
                          </SelectItem>
                          <SelectItem value="admin">
                            {t("roles.admin")}
                          </SelectItem>
                          {canOwn ? (
                            <SelectItem value="owner">
                              {t("roles.owner")}
                            </SelectItem>
                          ) : null}
                        </SelectContent>
                      </Select>
                      <FormMessage />
                    </FormItem>
                  )}
                />
                <FormField
                  control={form.control}
                  name="password"
                  render={({ field }) => (
                    <FormItem>
                      <FormLabel>{t("fields.password")}</FormLabel>
                      <FormControl>
                        <Input
                          type="password"
                          autoComplete="new-password"
                          maxLength={128}
                          placeholder={t("pages.family.passwordPlaceholder")}
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
                  disabled={createMember.isPending}
                >
                  <UserPlus className="size-4" />
                  {createMember.isPending
                    ? t("pages.family.addPending")
                    : t("pages.family.add")}
                </Button>
              </form>
            </Form>
          </GlassCardContent>
        </GlassCard> : null}

        <GlassCard>
          <GlassCardHeader>
            <GlassCardTitle>{t("pages.family.membersTitle")}</GlassCardTitle>
          </GlassCardHeader>
          <GlassCardContent>
            {members.isPending ? (
              <div className="space-y-2">
                {Array.from({ length: 3 }).map((_, index) => (
                  <Skeleton key={index} className="h-14 rounded-xl" />
                ))}
              </div>
            ) : members.isError ? (
              <QueryErrorState
                error={members.error}
                onRetry={() => void members.refetch()}
              />
            ) : members.data && members.data.length > 0 ? (
              <ul className="space-y-2">
                {members.data.map((member) => (
                  <li
                    key={member.id}
                    className="flex items-center justify-between gap-3 rounded-xl border border-border/60 bg-background/30 px-3 py-2.5"
                  >
                    <div className="flex min-w-0 items-center gap-3">
                      <span className="flex size-9 shrink-0 items-center justify-center rounded-lg bg-primary/10 text-primary">
                        <Users className="size-4" />
                      </span>
                      <p className="truncate text-sm font-medium">
                        {member.displayName}
                      </p>
                    </div>
                    <Badge variant="secondary">
                      {t(`roles.${member.role}`, {
                        defaultValue: member.role,
                      })}
                    </Badge>
                  </li>
                ))}
              </ul>
            ) : (
              <EmptyState
                icon={Users}
                title={t("pages.family.emptyTitle")}
                description={t("pages.family.emptyDescription")}
              />
            )}
          </GlassCardContent>
        </GlassCard>
      </div>

      {canOwn ? (
        <GlassCard className="max-w-xl">
          <GlassCardHeader>
            <GlassCardTitle>{t("pages.family.recoveryTitle")}</GlassCardTitle>
            <GlassCardDescription>
              {t("pages.family.recoveryDescription")}
            </GlassCardDescription>
          </GlassCardHeader>
          <GlassCardContent>
            <Form {...recoveryForm}>
              <form
                className="space-y-4"
                onSubmit={recoveryForm.handleSubmit(onRotateRecoveryCode)}
              >
                <FormField
                  control={recoveryForm.control}
                  name="currentPassword"
                  rules={{ required: true, maxLength: 128 }}
                  render={({ field }) => (
                    <FormItem>
                      <FormLabel>{t("fields.currentPassword")}</FormLabel>
                      <FormControl>
                        <Input
                          type="password"
                          autoComplete="current-password"
                          maxLength={128}
                          {...field}
                        />
                      </FormControl>
                      <FormMessage />
                    </FormItem>
                  )}
                />
                <Button
                  type="submit"
                  disabled={rotateRecoveryCode.isPending}
                >
                  {rotateRecoveryCode.isPending
                    ? t("pages.family.recoveryPending")
                    : t("pages.family.recoveryRotate")}
                </Button>
              </form>
            </Form>
          </GlassCardContent>
        </GlassCard>
      ) : null}

      <ResultSecretDialog
        open={recoverySecrets !== null}
        onOpenChange={(open) => {
          if (!open) setRecoverySecrets(null);
        }}
        title={t("pages.family.recoveryDialogTitle")}
        description={t("pages.family.recoveryDialogDescription")}
        secrets={recoverySecrets ?? []}
      />
    </div>
  );
}
