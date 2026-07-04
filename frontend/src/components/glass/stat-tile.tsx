import { type ComponentType, type ReactNode } from "react";
import { Link } from "react-router";
import { type LucideProps } from "lucide-react";
import { cn } from "@/lib/utils";
import { GlassCard } from "@/components/glass/glass-card";
import { Sparkline } from "@/components/glass/sparkline";

export type StatTone = "primary" | "info" | "success" | "warning" | "danger";

const toneText: Record<StatTone, string> = {
  primary: "text-primary",
  info: "text-info",
  success: "text-success",
  warning: "text-warning",
  danger: "text-destructive",
};

type StatTileProps = {
  label: ReactNode;
  value: ReactNode;
  detail?: ReactNode;
  icon?: ComponentType<LucideProps>;
  tone?: StatTone;
  sparkline?: number[];
  to?: string;
  className?: string;
};

export function StatTile({
  label,
  value,
  detail,
  icon: Icon,
  tone = "primary",
  sparkline,
  to,
  className,
}: StatTileProps) {
  const tile = (
    <GlassCard interactive className={cn("h-full p-5", className)}>
      <div className="flex items-start justify-between gap-3">
        <span className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">
          {label}
        </span>
        {Icon ? (
          <span
            className={cn(
              "glass-surface flex size-9 items-center justify-center rounded-xl",
              toneText[tone],
            )}
          >
            <Icon className="size-4" />
          </span>
        ) : null}
      </div>
      <div className="mt-3 flex items-end justify-between gap-3">
        <span className="text-3xl font-semibold leading-none tracking-tight tabular-nums">
          {value}
        </span>
        {sparkline && sparkline.length > 1 ? (
          <Sparkline
            data={sparkline}
            className={cn("h-9 w-24", toneText[tone])}
          />
        ) : null}
      </div>
      {detail ? (
        <p className="mt-2 text-sm text-muted-foreground">{detail}</p>
      ) : null}
    </GlassCard>
  );

  return to ? (
    <Link
      to={to}
      className="block focus-visible:outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50 rounded-2xl"
    >
      {tile}
    </Link>
  ) : (
    tile
  );
}
