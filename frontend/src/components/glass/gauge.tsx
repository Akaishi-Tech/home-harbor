import { type ReactNode, useId } from "react";
import { cn } from "@/lib/utils";
import { clampProgress } from "@/lib/format";

type GaugeProps = {
  /** 0–100. */
  value: number;
  size?: number;
  strokeWidth?: number;
  label?: ReactNode;
  caption?: ReactNode;
  className?: string;
};

/**
 * Radial progress gauge drawn as SVG. The arc transitions via CSS when the
 * value changes (no JS animation loop), so it is cheap and respects
 * reduced-motion through the global transition reset.
 */
export function Gauge({
  value,
  size = 132,
  strokeWidth = 12,
  label,
  caption,
  className,
}: GaugeProps) {
  const pct = clampProgress(value);
  const radius = (size - strokeWidth) / 2;
  const circumference = 2 * Math.PI * radius;
  const offset = circumference * (1 - pct / 100);
  const gradientId = useId();

  return (
    <div
      className={cn(
        "relative inline-flex items-center justify-center",
        className,
      )}
      style={{ width: size, height: size }}
    >
      <svg
        width={size}
        height={size}
        viewBox={`0 0 ${size} ${size}`}
        className="-rotate-90"
      >
        <defs>
          <linearGradient id={gradientId} x1="0" y1="0" x2="1" y2="1">
            <stop offset="0%" stopColor="var(--chart-1)" />
            <stop offset="55%" stopColor="var(--chart-3)" />
            <stop offset="100%" stopColor="var(--chart-2)" />
          </linearGradient>
        </defs>
        <circle
          cx={size / 2}
          cy={size / 2}
          r={radius}
          fill="none"
          stroke="var(--border)"
          strokeWidth={strokeWidth}
        />
        <circle
          cx={size / 2}
          cy={size / 2}
          r={radius}
          fill="none"
          stroke={`url(#${gradientId})`}
          strokeWidth={strokeWidth}
          strokeLinecap="round"
          strokeDasharray={circumference}
          strokeDashoffset={offset}
          style={{
            transition: "stroke-dashoffset 0.8s cubic-bezier(0.2, 0.8, 0.2, 1)",
          }}
        />
      </svg>
      <div className="absolute inset-0 flex flex-col items-center justify-center gap-0.5">
        {label ? (
          <span className="text-2xl font-semibold tabular-nums tracking-tight">
            {label}
          </span>
        ) : null}
        {caption ? (
          <span className="text-[0.7rem] font-medium uppercase tracking-wide text-muted-foreground">
            {caption}
          </span>
        ) : null}
      </div>
    </div>
  );
}
