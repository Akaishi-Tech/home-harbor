import { useId } from "react";
import { cn } from "@/lib/utils";

type SparklineProps = {
  data: number[];
  width?: number;
  height?: number;
  className?: string;
  /** Stroke/fill color; defaults to the current text color. */
  color?: string;
  filled?: boolean;
};

export function Sparkline({
  data,
  width = 120,
  height = 36,
  className,
  color = "currentColor",
  filled = true,
}: SparklineProps) {
  if (data.length < 2) {
    return (
      <svg
        width={width}
        height={height}
        className={className}
        aria-hidden="true"
      />
    );
  }

  const min = Math.min(...data);
  const max = Math.max(...data);
  const span = max - min || 1;
  const stepX = width / (data.length - 1);
  const pad = 2;
  const usableHeight = height - pad * 2;

  const points = data.map((value, index) => {
    const x = index * stepX;
    const y = pad + usableHeight - ((value - min) / span) * usableHeight;
    return [x, y] as const;
  });

  const line = points
    .map(
      ([x, y], index) =>
        `${index === 0 ? "M" : "L"}${x.toFixed(2)} ${y.toFixed(2)}`,
    )
    .join(" ");
  const area = `${line} L${width} ${height} L0 ${height} Z`;
  const gradientId = useId();

  return (
    <svg
      width={width}
      height={height}
      viewBox={`0 0 ${width} ${height}`}
      className={cn("overflow-visible", className)}
      style={{ color }}
      aria-hidden="true"
    >
      {filled ? (
        <>
          <defs>
            <linearGradient id={gradientId} x1="0" y1="0" x2="0" y2="1">
              <stop offset="0%" stopColor="currentColor" stopOpacity="0.28" />
              <stop offset="100%" stopColor="currentColor" stopOpacity="0" />
            </linearGradient>
          </defs>
          <path d={area} fill={`url(#${gradientId})`} stroke="none" />
        </>
      ) : null}
      <path
        d={line}
        fill="none"
        stroke="currentColor"
        strokeWidth={2}
        strokeLinecap="round"
        strokeLinejoin="round"
      />
    </svg>
  );
}
