"use client";

import { AsciiAdam } from "./AsciiAdam";

// Matthew's ASCII Adam: a screen-sized canvas, shifted down so the fingertips meet below the
// Explore button instead of behind it.
export function AdamLayer({
  onTouch,
  introSpeed,
  onUnavailable,
}: {
  onTouch?: () => void;
  introSpeed?: number;
  onUnavailable?: () => void;
}) {
  return (
    <div className="pointer-events-none absolute inset-x-0 top-[22dvh] h-dvh">
      <AsciiAdam onTouch={onTouch} introSpeed={introSpeed} onUnavailable={onUnavailable} />
    </div>
  );
}
