"use client";

import { AsciiAdam } from "./AsciiAdam";

// Matthew's ASCII Adam is drawn for a black page. Inverting it (with a hue turn so the warm arm
// stays warm) and multiplying it onto the light background drops the black and keeps the glyphs.
export function AdamLayer() {
  return (
    <div className="pointer-events-none absolute inset-0">
      {/* Sits below the button: Matthew's frame is cover-fit into the lower part of the screen. */}
      <div className="absolute inset-x-0 bottom-0 top-[44%] [filter:invert(1)_hue-rotate(180deg)] mix-blend-multiply [&_canvas]:block [&_canvas]:h-full [&_canvas]:w-full">
        <AsciiAdam />
      </div>
    </div>
  );
}
