import { type ComponentType, type ReactNode } from "react";
import { type LucideProps } from "lucide-react";
import { cn } from "@/lib/utils";

type EmptyStateProps = {
  icon?: ComponentType<LucideProps>;
  title: ReactNode;
  description?: ReactNode;
  children?: ReactNode;
  className?: string;
};

export function EmptyState({
  icon: Icon,
  title,
  description,
  children,
  className,
}: EmptyStateProps) {
  return (
    <div
      className={cn(
        "flex flex-col items-center justify-center gap-3 px-6 py-12 text-center",
        className,
      )}
    >
      {Icon ? (
        <span className="glass-surface flex size-12 items-center justify-center rounded-2xl text-primary">
          <Icon className="size-5" />
        </span>
      ) : null}
      <div className="space-y-1">
        <p className="text-sm font-medium">{title}</p>
        {description ? (
          <p className="text-sm text-muted-foreground">{description}</p>
        ) : null}
      </div>
      {children}
    </div>
  );
}
