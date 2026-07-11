import { ShieldCheck } from "lucide-react";
import { useTranslation } from "react-i18next";

const CA_CERTIFICATE_URL = "http://homeharbor.local/homeharbor-ca.crt";

export function TlsTrustNotice() {
  const { t } = useTranslation();

  return (
    <div
      role="note"
      className="rounded-xl border border-primary/25 bg-primary/5 p-3 text-sm"
    >
      <div className="flex items-start gap-2">
        <ShieldCheck className="mt-0.5 size-4 shrink-0 text-primary" />
        <div className="space-y-1">
          <p className="font-medium">{t("tlsTrust.title")}</p>
          <p className="text-xs leading-relaxed text-muted-foreground">
            {t("tlsTrust.description")}
          </p>
          <a
            className="inline-block text-xs font-medium text-primary underline underline-offset-4"
            href={CA_CERTIFICATE_URL}
            target="_blank"
            rel="noreferrer"
          >
            {t("tlsTrust.download")}
          </a>
        </div>
      </div>
    </div>
  );
}
