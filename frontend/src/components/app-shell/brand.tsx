import { useId } from "react";
import { cn } from "@/lib/utils";

export function Brand({
  className,
  hideWordmark = false,
}: {
  className?: string;
  hideWordmark?: boolean;
}) {
  return (
    <div className={cn("flex items-center gap-2.5", className)}>
      <HomeHarborMark />
      {hideWordmark ? null : (
        <span className="text-base font-semibold tracking-tight">
          HomeHarbor
        </span>
      )}
    </div>
  );
}

function HomeHarborMark() {
  const clipId = useId().replaceAll(":", "");

  return (
    <svg
      aria-hidden="true"
      className="size-9 shrink-0 drop-shadow-[0_12px_18px_rgba(3,47,76,0.24)]"
      focusable="false"
      viewBox="0 0 64 64"
      xmlns="http://www.w3.org/2000/svg"
    >
      <defs>
        <clipPath id={clipId}>
          <circle cx="32" cy="32" r="30" />
        </clipPath>
      </defs>
      <g clipPath={`url(#${clipId})`}>
        <circle cx="32" cy="32" r="30" fill="#062f51" />
        <path
          d="M14 32.6 32 16l18 16.6h-5.4v13.8c-3.6 2.1-7.8 3.1-12.6 3.1s-9-1-12.6-3.1V32.6H14Z"
          fill="#f8fbff"
        />
        <path
          d="M24.1 32.1h5.5v6h4.8v-6h5.5V47h-5.5v-5.6h-4.8V47h-5.5V32.1Z"
          fill="#062f51"
        />
        <path
          d="M-3 44.2c10.8-4.6 18.8-4.6 28.4-.2 9.3 4.3 17.9 4.3 31.6-1.8 3.7-1.6 7.1-2.6 10-3v13.4c-8.8 1.3-16.6 5-28.6 2.3-11-2.5-18.5-9.2-41.4-2.1V44.2Z"
          fill="#f8fbff"
        />
        <path
          d="M-3 48.5c10.9-4.6 18.8-4.5 28.5-.1 9.2 4.2 17.8 4.2 31.5-1.8 3.7-1.6 7.1-2.6 10-3v21.3H-3V48.5Z"
          fill="#0ea5b7"
        />
        <path
          d="M-3 56.2c10.8-4.5 18.9-4.4 28.6-.1 9.2 4.1 17.9 4.1 31.4-1.7 3.7-1.6 7.1-2.5 10-2.9v13.4H-3V56.2Z"
          fill="#062f51"
        />
      </g>
    </svg>
  );
}
