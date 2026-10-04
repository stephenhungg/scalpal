"use client";

import { AdamLayer } from "./AdamLayer";

// Explore page backdrop: the hands already touching (and breathing), dimmed well back so the
// video and copy stay on top.
export function HandsBackdrop() {
  return (
    <div aria-hidden className="pointer-events-none fixed inset-0 z-0">
      <AdamLayer holdAfterTouch startTouched shiftY={0.12} />
      <div className="absolute inset-0 bg-black/80" />
    </div>
  );
}
