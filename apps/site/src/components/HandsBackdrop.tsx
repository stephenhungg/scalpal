"use client";

import { AdamLayer } from "./AdamLayer";
import { useSparkHole } from "./useSparkHole";
import { DIM_EXPLORE, SHIFT_Y } from "@/lib/scene";

// Explore page backdrop: the same hands as the landing (same position, already touching and
// breathing, spark left bright), dimmed further so the video and copy stay on top.
export function HandsBackdrop() {
  const sparkHole = useSparkHole();
  return (
    <div aria-hidden className="pointer-events-none fixed inset-0 z-0">
      <AdamLayer holdAfterTouch startTouched shiftY={SHIFT_Y} />
      <div
        className="absolute inset-0 bg-black"
        style={{ opacity: DIM_EXPLORE, maskImage: sparkHole, WebkitMaskImage: sparkHole }}
      />
    </div>
  );
}
