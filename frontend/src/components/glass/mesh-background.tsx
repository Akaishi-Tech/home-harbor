import { useEffect, useRef } from "react";

/**
 * Fixed gradient-mesh backdrop. CSS drives the drift animation (and respects
 * prefers-reduced-motion); this component additionally pauses it while the tab
 * is hidden to save GPU on low-end appliance clients.
 */
export function MeshBackground() {
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const node = ref.current;
    if (!node) return;
    const sync = () => {
      node.dataset.paused = document.hidden ? "true" : "false";
    };
    sync();
    document.addEventListener("visibilitychange", sync);
    return () => document.removeEventListener("visibilitychange", sync);
  }, []);

  return <div ref={ref} className="mesh-bg" aria-hidden="true" />;
}
