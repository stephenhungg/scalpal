"use client";

import { AsciiAdam } from "./AsciiAdam";

// Matthew's ASCII Adam on a full-screen canvas (so the pointer scramble works everywhere), with
// the scene shifted down so the fingertips meet below the Explore button instead of behind it.
export function AdamLayer({
  onTouch,
  introSpeed,
  holdAfterTouch,
  shiftY = 0.22,
  startTouched,
  onUnavailable,
}: {
  onTouch?: () => void;
  introSpeed?: number;
  holdAfterTouch?: boolean;
  shiftY?: number;
  startTouched?: boolean;
  onUnavailable?: () => void;
}) {
  return (
    <div className="pointer-events-none absolute inset-0">
      <AsciiAdam onTouch={onTouch} introSpeed={introSpeed} holdAfterTouch={holdAfterTouch} shiftY={shiftY} startTouched={startTouched} onUnavailable={onUnavailable} />
    </div>
  );
}
