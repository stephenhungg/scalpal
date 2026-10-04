"use client";

import PatternWaves from "./PatternWaves";

// ReactBits Pattern Waves (https://reactbits.dev/backgrounds/pattern-waves), tuned for the light
// page: faint black dots on a transparent background, fading out at the edges.
export function WaveBackground() {
  return (
    <PatternWaves
      preset="silk"
      color="#000000"
      backgroundColor="transparent"
      opacity={0.35}
      fade="edges"
      className="h-full w-full"
    />
  );
}
