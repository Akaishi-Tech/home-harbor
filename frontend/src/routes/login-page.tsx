import { useMemo, useState } from "react";
import { useNavigate, useLoaderData } from "react-router";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { toast } from "sonner";
import { LockKeyhole } from "lucide-react";
import { useTranslation } from "react-i18next";
import { Brand } from "@/components/app-shell/brand";
import { TlsTrustNotice } from "@/components/tls-trust-notice";
import {
  GlassCard,
  GlassCardContent,
  GlassCardDescription,
  GlassCardHeader,
  GlassCardTitle,
} from "@/components/glass/glass-card";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import {
  ResultSecretDialog,
  type SecretField,
} from "@/components/glass/result-secret-dialog";
import {
  Form,
  FormControl,
  FormField,
  FormItem,
  FormLabel,
  FormMessage,
} from "@/components/ui/form";
import { useLogin, useRecoverOwner } from "@/hooks/queries";
import { errorMessage } from "@/lib/format";
import { jsonSecret, stringSecret } from "@/lib/secrets";
import type { SetupStatus } from "@/types";

type LoginValues = {
  displayName: string;
  password: string;
};

type RecoveryValues = {
  recoveryCode: string;
  newPassword: string;
  confirmPassword: string;
};

export function LoginPage() {
  const setup = useLoaderData() as SetupStatus;
  const navigate = useNavigate();
  const login = useLogin();
  const recoverOwner = useRecoverOwner();
  const [recoveryOpen, setRecoveryOpen] = useState(false);
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
          .min(1, t("validation.displayNameRequired"))
          .max(96, t("validation.nameTooLong")),
        password: z
          .string()
          .min(1, t("validation.localPasswordRequired"))
          .max(128, t("validation.passwordTooLong")),
      }),
    [t],
  );

  const form = useForm<LoginValues>({
    resolver: zodResolver(schema),
    defaultValues: {
      displayName: setup?.family?.ownerDisplayName ?? "Owner",
      password: "",
    },
  });
  const recoverySchema = useMemo(
    () =>
      z
        .object({
          recoveryCode: z
            .string()
            .trim()
            .min(1, t("validation.recoveryCodeRequired")),
          newPassword: z
            .string()
            .min(12, t("validation.passwordLength"))
            .max(128, t("validation.passwordLength")),
          confirmPassword: z.string(),
        })
        .refine((value) => value.newPassword === value.confirmPassword, {
          path: ["confirmPassword"],
          message: t("validation.passwordMismatch"),
        }),
    [t],
  );
  const recoveryForm = useForm<RecoveryValues>({
    resolver: zodResolver(recoverySchema),
    defaultValues: {
      recoveryCode: "",
      newPassword: "",
      confirmPassword: "",
    },
  });

  function onSubmit(values: LoginValues) {
    login.mutate(values, {
      onSuccess: () => {
        toast.success(t("toast.loggedIn"));
        login.reset();
        navigate("/dashboard");
      },
      onError: (error) => {
        toast.error(errorMessage(error));
        // Do not retain the submitted password in mutation state.
        login.reset();
      },
    });
  }

  function onRecover(values: RecoveryValues) {
    recoverOwner.mutate(
      {
        recoveryCode: values.recoveryCode.trim().toUpperCase(),
        newPassword: values.newPassword,
      },
      {
        onSuccess: (response) => {
          const found = stringSecret(
            response,
            "replacementRecoveryCode",
            t("secretLabels.recoveryCode"),
          );
          setRecoverySecrets(
            found.length
              ? found
              : jsonSecret(response, t("secretLabels.recoveryCode")),
          );
          setRecoveryOpen(false);
          recoveryForm.reset();
          recoverOwner.reset();
          toast.success(t("toast.ownerRecovered"));
        },
        onError: (error) => {
          toast.error(errorMessage(error));
          recoverOwner.reset();
        },
      },
    );
  }

  return (
    <div className="grid min-h-svh place-items-center p-4">
      <GlassCard className="w-full max-w-sm">
        <GlassCardHeader className="items-start gap-4">
          <Brand />
          <div className="space-y-1">
            <GlassCardTitle className="text-xl">
              {setup?.family?.name ?? t("pages.login.title")}
            </GlassCardTitle>
            <GlassCardDescription>
              {t("pages.login.description")}
            </GlassCardDescription>
          </div>
        </GlassCardHeader>
        <GlassCardContent>
          <TlsTrustNotice />
          <Form {...form}>
            <form className="mt-4 space-y-4" onSubmit={form.handleSubmit(onSubmit)}>
              <FormField
                control={form.control}
                name="displayName"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel>{t("fields.displayName")}</FormLabel>
                    <FormControl>
                      <Input
                        autoComplete="username"
                        maxLength={96}
                        placeholder="Owner"
                        {...field}
                      />
                    </FormControl>
                    <FormMessage />
                  </FormItem>
                )}
              />
              <FormField
                control={form.control}
                name="password"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel>{t("fields.localPassword")}</FormLabel>
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
                className="w-full"
                disabled={login.isPending}
              >
                <LockKeyhole className="size-4" />
                {login.isPending ? t("pages.login.pending") : t("pages.login.submit")}
              </Button>
              <Button
                type="button"
                variant="ghost"
                className="w-full"
                onClick={() => setRecoveryOpen(true)}
              >
                {t("pages.login.recoverOwner")}
              </Button>
            </form>
          </Form>
        </GlassCardContent>
      </GlassCard>

      <Dialog
        open={recoveryOpen}
        onOpenChange={(open) => {
          if (!recoverOwner.isPending) setRecoveryOpen(open);
        }}
      >
        <DialogContent showCloseButton={!recoverOwner.isPending}>
          <DialogHeader>
            <DialogTitle>{t("pages.login.recoveryTitle")}</DialogTitle>
            <DialogDescription>
              {t("pages.login.recoveryDescription")}
            </DialogDescription>
          </DialogHeader>
          <Form {...recoveryForm}>
            <form
              className="space-y-4"
              onSubmit={recoveryForm.handleSubmit(onRecover)}
            >
              <FormField
                control={recoveryForm.control}
                name="recoveryCode"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel>{t("fields.recoveryCode")}</FormLabel>
                    <FormControl>
                      <Input
                        autoCapitalize="characters"
                        autoComplete="off"
                        className="font-mono uppercase"
                        spellCheck={false}
                        {...field}
                      />
                    </FormControl>
                    <FormMessage />
                  </FormItem>
                )}
              />
              <FormField
                control={recoveryForm.control}
                name="newPassword"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel>{t("fields.newPassword")}</FormLabel>
                    <FormControl>
                      <Input
                        type="password"
                        autoComplete="new-password"
                        maxLength={128}
                        {...field}
                      />
                    </FormControl>
                    <FormMessage />
                  </FormItem>
                )}
              />
              <FormField
                control={recoveryForm.control}
                name="confirmPassword"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel>{t("fields.confirmPassword")}</FormLabel>
                    <FormControl>
                      <Input
                        type="password"
                        autoComplete="new-password"
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
                className="w-full"
                disabled={recoverOwner.isPending}
              >
                {recoverOwner.isPending
                  ? t("pages.login.recoveryPending")
                  : t("pages.login.recoverySubmit")}
              </Button>
            </form>
          </Form>
        </DialogContent>
      </Dialog>

      <ResultSecretDialog
        open={recoverySecrets !== null}
        onOpenChange={(open) => {
          if (!open) setRecoverySecrets(null);
        }}
        title={t("pages.login.recoveryCompleteTitle")}
        description={t("pages.login.recoveryCompleteDescription")}
        secrets={recoverySecrets ?? []}
      />
    </div>
  );
}
