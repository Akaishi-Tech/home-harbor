import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import {
  CheckIcon,
  CopyIcon,
  ShieldCheckIcon,
  TriangleAlertIcon,
} from "lucide-react";
import { toast } from "sonner";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/utils";

export type SecretField = {
  label: string;
  value: string;
  /** Render in a multi-line monospace block (e.g. a WireGuard config). */
  multiline?: boolean;
};

type ResultSecretDialogProps = {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  description?: string;
  secrets: SecretField[];
};

async function copyText(value: string): Promise<boolean> {
  try {
    await navigator.clipboard.writeText(value);
    return true;
  } catch {
    return false;
  }
}

function CopyButton({ value }: { value: string }) {
  const [copied, setCopied] = useState(false);
  const { t } = useTranslation();

  return (
    <Button
      type="button"
      variant="ghost"
      size="icon-sm"
      aria-label={t("common.copy")}
      onClick={async () => {
        const ok = await copyText(value);
        if (ok) {
          setCopied(true);
          toast.success(t("common.copiedClipboard"));
          window.setTimeout(() => setCopied(false), 1500);
        } else {
          toast.error(t("common.copyFailedManual"));
        }
      }}
    >
      {copied ? (
        <CheckIcon className="size-4 text-success" />
      ) : (
        <CopyIcon className="size-4" />
      )}
    </Button>
  );
}

/**
 * Shows server-generated secrets exactly once (sync tokens, recovery codes,
 * SMB passwords, WireGuard configs). Values live only in this component's
 * props — never in the query cache, router state, or the URL.
 */
export function ResultSecretDialog({
  open,
  onOpenChange,
  title,
  description,
  secrets,
}: ResultSecretDialogProps) {
  const { t } = useTranslation();

  useEffect(() => {
    if (!open) return;
    const warnBeforeUnload = (event: BeforeUnloadEvent) => {
      event.preventDefault();
      event.returnValue = "";
    };
    window.addEventListener("beforeunload", warnBeforeUnload);
    return () => window.removeEventListener("beforeunload", warnBeforeUnload);
  }, [open]);

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent
        className="sm:max-w-lg"
        showCloseButton={false}
        onEscapeKeyDown={(event) => event.preventDefault()}
        onPointerDownOutside={(event) => event.preventDefault()}
        onInteractOutside={(event) => event.preventDefault()}
      >
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2">
            <ShieldCheckIcon className="size-5 text-primary" />
            {title}
          </DialogTitle>
          {description ? (
            <DialogDescription>{description}</DialogDescription>
          ) : null}
        </DialogHeader>

        <div className="space-y-3">
          {secrets.map((secret) => (
            <div key={secret.label} className="glass-surface rounded-xl p-3">
              <div className="flex items-center justify-between gap-2">
                <span className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">
                  {secret.label}
                </span>
                <CopyButton value={secret.value} />
              </div>
              <div
                className={cn(
                  "mt-1 font-mono text-sm break-all",
                  secret.multiline &&
                    "whitespace-pre-wrap max-h-48 overflow-auto",
                )}
              >
                {secret.value}
              </div>
            </div>
          ))}
        </div>

        <div className="flex items-start gap-2 rounded-xl border border-warning/40 bg-warning/10 p-3 text-sm text-warning-foreground">
          <TriangleAlertIcon className="mt-0.5 size-4 shrink-0 text-warning" />
          <span>{t("common.safeSaveWarning")}</span>
        </div>

        <DialogFooter>
          <Button type="button" onClick={() => onOpenChange(false)}>
            {t("common.savedSecrets")}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
