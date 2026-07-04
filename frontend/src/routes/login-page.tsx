import { useMemo } from "react";
import { useNavigate, useLoaderData } from "react-router";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { toast } from "sonner";
import { LockKeyhole } from "lucide-react";
import { useTranslation } from "react-i18next";
import { Brand } from "@/components/app-shell/brand";
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
  Form,
  FormControl,
  FormField,
  FormItem,
  FormLabel,
  FormMessage,
} from "@/components/ui/form";
import { useLogin } from "@/hooks/queries";
import { errorMessage } from "@/lib/format";
import type { SetupStatus } from "@/types";

type LoginValues = {
  displayName: string;
  password: string;
};

export function LoginPage() {
  const setup = useLoaderData() as SetupStatus;
  const navigate = useNavigate();
  const login = useLogin();
  const { t } = useTranslation();
  const schema = useMemo(
    () =>
      z.object({
        displayName: z.string().trim().min(1, t("validation.displayNameRequired")),
        password: z.string().min(1, t("validation.localPasswordRequired")),
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

  function onSubmit(values: LoginValues) {
    login.mutate(values, {
      onSuccess: () => {
        toast.success(t("toast.loggedIn"));
        navigate("/dashboard");
      },
      onError: (error) => toast.error(errorMessage(error)),
    });
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
          <Form {...form}>
            <form className="space-y-4" onSubmit={form.handleSubmit(onSubmit)}>
              <FormField
                control={form.control}
                name="displayName"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel>{t("fields.displayName")}</FormLabel>
                    <FormControl>
                      <Input
                        autoComplete="username"
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
            </form>
          </Form>
        </GlassCardContent>
      </GlassCard>
    </div>
  );
}
