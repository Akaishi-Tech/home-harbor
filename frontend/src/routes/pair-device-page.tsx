import { useMemo, useState } from "react";
import { useNavigate } from "react-router";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { Link2, Smartphone } from "lucide-react";
import { useTranslation } from "react-i18next";
import { Brand } from "@/components/app-shell/brand";
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
import { usePairDevice } from "@/hooks/queries";

const PAIRING_CODE_PATTERN = /^[A-HJ-NP-Z2-9]{4}(?:-[A-HJ-NP-Z2-9]{4}){3}$/;

type PairDeviceValues = {
  displayName: string;
  kind: "browser" | "mobile" | "desktop";
};

function takePairingCodeFromUrl(): string {
  const url = new URL(window.location.href);
  const fragment = new URLSearchParams(url.hash.replace(/^#/, ""));
  const supplied = fragment.get("code") ?? url.searchParams.get("code") ?? "";

  // Clear both modern fragment and legacy query forms before React mounts any
  // children, browser extensions inspect later navigation, or the user copies
  // the address bar. The code remains only in this component's memory.
  url.hash = "";
  url.searchParams.delete("code");
  window.history.replaceState(
    window.history.state,
    "",
    `${url.pathname}${url.search}${url.hash}`,
  );

  const normalized = supplied.trim().toUpperCase();
  return PAIRING_CODE_PATTERN.test(normalized) ? normalized : "";
}

export function PairDevicePage() {
  const navigate = useNavigate();
  const { t } = useTranslation();
  const [pairingCode, setPairingCode] = useState(takePairingCodeFromUrl);
  const [pairingFailed, setPairingFailed] = useState(false);
  const [secrets, setSecrets] = useState<SecretField[] | null>(null);
  const pairDevice = usePairDevice();
  const schema = useMemo(
    () =>
      z.object({
        displayName: z
          .string()
          .trim()
          .min(1, t("validation.deviceNameRequired"))
          .max(96, t("validation.nameTooLong")),
        kind: z.enum(["browser", "mobile", "desktop"]),
      }),
    [t],
  );
  const form = useForm<PairDeviceValues>({
    resolver: zodResolver(schema),
    defaultValues: { displayName: "", kind: "mobile" },
  });

  async function onSubmit(values: PairDeviceValues) {
    if (!pairingCode) return;
    try {
      const response = await pairDevice.mutateAsync({
        displayName: values.displayName.trim(),
        kind: values.kind,
        pairingCode,
      });
      const webDav = response.webDav;
      setPairingCode("");
      form.reset();
      if (!webDav?.username || !webDav.token) {
        setPairingFailed(true);
        return;
      }

      setSecrets([
        {
          label: t("secretLabels.webDavUsername"),
          value: webDav.username,
        },
        {
          label: t("secretLabels.webDavToken"),
          value: webDav.token,
        },
        {
          label: t("secretLabels.webDavEndpoint"),
          value: new URL("/dav/", window.location.origin).toString(),
        },
      ]);
    } catch {
      // The ticket may be expired or consumed. Never retain it in mutation
      // state or echo it into an error; require a fresh admin-generated QR.
      setPairingCode("");
      setPairingFailed(true);
    } finally {
      pairDevice.reset();
    }
  }

  const canPair = Boolean(pairingCode) && !pairingFailed;

  return (
    <div className="grid min-h-svh place-items-center p-4">
      <GlassCard className="w-full max-w-md">
        <GlassCardHeader className="items-start gap-4">
          <Brand />
          <div className="space-y-1">
            <GlassCardTitle className="flex items-center gap-2 text-xl">
              <Link2 className="size-5 text-primary" />
              {t("pages.pair.title")}
            </GlassCardTitle>
            <GlassCardDescription>
              {canPair ? t("pages.pair.description") : t("pages.pair.rescan")}
            </GlassCardDescription>
          </div>
        </GlassCardHeader>
        <GlassCardContent>
          {canPair ? (
            <Form {...form}>
              <form className="space-y-4" onSubmit={form.handleSubmit(onSubmit)}>
                <FormField
                  control={form.control}
                  name="displayName"
                  render={({ field }) => (
                    <FormItem>
                      <FormLabel>{t("fields.deviceName")}</FormLabel>
                      <FormControl>
                        <Input
                          autoComplete="name"
                          maxLength={96}
                          placeholder={t("pages.pair.devicePlaceholder")}
                          {...field}
                        />
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
                      <FormLabel>{t("pages.pair.deviceKind")}</FormLabel>
                      <Select onValueChange={field.onChange} value={field.value}>
                        <FormControl>
                          <SelectTrigger className="w-full">
                            <SelectValue />
                          </SelectTrigger>
                        </FormControl>
                        <SelectContent>
                          <SelectItem value="mobile">{t("pages.pair.kinds.mobile")}</SelectItem>
                          <SelectItem value="desktop">{t("pages.pair.kinds.desktop")}</SelectItem>
                          <SelectItem value="browser">{t("pages.pair.kinds.browser")}</SelectItem>
                        </SelectContent>
                      </Select>
                      <FormMessage />
                    </FormItem>
                  )}
                />
                <Button
                  type="submit"
                  className="w-full"
                  disabled={pairDevice.isPending}
                >
                  <Smartphone className="size-4" />
                  {pairDevice.isPending
                    ? t("pages.pair.pairing")
                    : t("pages.pair.submit")}
                </Button>
              </form>
            </Form>
          ) : (
            <div className="space-y-4">
              <p className="rounded-xl border border-warning/40 bg-warning/10 px-3 py-2 text-sm text-warning-foreground">
                {t("pages.pair.invalidOrExpired")}
              </p>
              <Button className="w-full" onClick={() => navigate("/", { replace: true })}>
                {t("common.back")}
              </Button>
            </div>
          )}
        </GlassCardContent>
      </GlassCard>

      <ResultSecretDialog
        open={secrets !== null}
        onOpenChange={(open) => {
          if (!open) {
            setSecrets(null);
            navigate("/", { replace: true });
          }
        }}
        title={t("pages.pair.successTitle")}
        description={t("pages.pair.successDescription")}
        secrets={secrets ?? []}
      />
    </div>
  );
}
