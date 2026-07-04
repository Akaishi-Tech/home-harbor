import { type ComponentProps, type PointerEvent } from "react";
import { cn } from "@/lib/utils";
import { usePrefersReducedMotion } from "@/hooks/use-reduced-motion";

type GlassCardProps = ComponentProps<"div"> & {
  /** Adds lift + pointer-tracked sheen on hover. */
  interactive?: boolean;
};

export function GlassCard({
  className,
  interactive = false,
  onPointerMove,
  ...props
}: GlassCardProps) {
  const reducedMotion = usePrefersReducedMotion();

  function handlePointerMove(event: PointerEvent<HTMLDivElement>) {
    if (interactive && !reducedMotion) {
      const node = event.currentTarget;
      const rect = node.getBoundingClientRect();
      node.style.setProperty("--mx", `${event.clientX - rect.left}px`);
      node.style.setProperty("--my", `${event.clientY - rect.top}px`);
    }
    onPointerMove?.(event);
  }

  return (
    <div
      data-slot="glass-card"
      className={cn(
        "glass-surface rounded-2xl",
        interactive && "glass-interactive",
        className,
      )}
      onPointerMove={handlePointerMove}
      {...props}
    />
  );
}

export function GlassCardHeader({
  className,
  ...props
}: ComponentProps<"div">) {
  return (
    <div
      className={cn("flex flex-col gap-1.5 p-5 sm:p-6", className)}
      {...props}
    />
  );
}

export function GlassCardTitle({ className, ...props }: ComponentProps<"h3">) {
  return (
    <h3
      className={cn(
        "text-base font-semibold leading-tight tracking-tight",
        className,
      )}
      {...props}
    />
  );
}

export function GlassCardDescription({
  className,
  ...props
}: ComponentProps<"p">) {
  return (
    <p className={cn("text-sm text-muted-foreground", className)} {...props} />
  );
}

export function GlassCardContent({
  className,
  ...props
}: ComponentProps<"div">) {
  return (
    <div className={cn("p-5 pt-0 sm:p-6 sm:pt-0", className)} {...props} />
  );
}

export function GlassCardFooter({
  className,
  ...props
}: ComponentProps<"div">) {
  return (
    <div
      className={cn(
        "flex items-center gap-2 p-5 pt-0 sm:p-6 sm:pt-0",
        className,
      )}
      {...props}
    />
  );
}
