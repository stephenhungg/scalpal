"use client";

import { AsciiAdam } from "./AsciiAdam";

// Matthew's ASCII Adam is drawn for a black page. Inverting it (with a hue turn so the warm arm
// stays warm) and multiplying it onto the light background drops the black and keeps the glyphs.
// The backdrop blur under it softens the wave background where the arms pass over it.
// Soft ovals over where the two arms rest (left arm low-left, right arm mid-right).
const ARMS_MASK = [
  "radial-gradient(ellipse 30% 16% at 22% 68%, black 40%, transparent 100%)",
  "radial-gradient(ellipse 34% 18% at 75% 62%, black 40%, transparent 100%)",
].join(", ");

export function AdamLayer() {
  return (
    <div className="pointer-events-none absolute inset-0">
      <div
        aria-hidden
        className="absolute inset-0 backdrop-blur-[14px]"
        style={{ maskImage: ARMS_MASK, WebkitMaskImage: ARMS_MASK }}
      />
      {/* Sits below the button: Matthew's frame is cover-fit into the lower part of the screen. */}
      <div className="absolute inset-x-0 bottom-0 top-[44%] [filter:invert(1)_hue-rotate(180deg)] mix-blend-multiply [&_canvas]:block [&_canvas]:h-full [&_canvas]:w-full">
        <AsciiAdam />
      </div>
    </div>
  );
}
