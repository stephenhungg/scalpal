"use client";

import { AsciiAdam } from "./AsciiAdam";

// Matthew's ASCII Adam as in his repo: full-screen canvas behind the hero content.
export function AdamLayer({ onTouch, introSpeed }: { onTouch?: () => void; introSpeed?: number }) {
  return (
    <div className="pointer-events-none absolute inset-0">
      <AsciiAdam onTouch={onTouch} introSpeed={introSpeed} />
    </div>
  );
}
