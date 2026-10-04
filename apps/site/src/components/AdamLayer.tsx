"use client";

import { AsciiAdam } from "./AsciiAdam";

// Matthew's ASCII Adam as in his repo: full-screen canvas behind the hero content.
export function AdamLayer() {
  return (
    <div className="pointer-events-none absolute inset-0">
      <AsciiAdam />
    </div>
  );
}
