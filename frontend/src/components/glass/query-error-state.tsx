import { TriangleAlert } from "lucide-react";
import { useTranslation } from "react-i18next";
import { Button } from "@/components/ui/button";
import { EmptyState } from "@/components/glass/empty-state";
import { errorMessage } from "@/lib/format";

export function QueryErrorState({
  error,
  onRetry,
  className,
}: {
  error: unknown;
  onRetry: () => void;
  className?: string;
}) {
  const { t } = useTranslation();

  return (
    <EmptyState
      icon={TriangleAlert}
      title={t("common.loadFailed")}
      description={errorMessage(error)}
      className={className}
    >
      <Button type="button" variant="outline" onClick={onRetry}>
        {t("common.retry")}
      </Button>
    </EmptyState>
  );
}
