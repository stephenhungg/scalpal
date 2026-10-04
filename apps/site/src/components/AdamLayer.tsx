"use client";

import { AsciiAdam } from "./AsciiAdam";

// Matthew's ASCII Adam: a screen-sized canvas, shifted down so the fingertips meet below the
// Explore button instead of behind it.
export function AdamLayer({ onTouch, introSpeed, holdAfterTouch }: { onTouch?: () => void; introSpeed?: number; holdAfterTouch?: boolean }) {
  return (
    <div className="pointer-events-none absolute inset-x-0 top-[22dvh] h-dvh">
      <AsciiAdam onTouch={onTouch} introSpeed={introSpeed} holdAfterTouch={holdAfterTouch} />
    </div>
  );
}
