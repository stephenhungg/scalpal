"use client";

import { useEffect, useState } from "react";
import { ASPECT, SCENE } from "./AsciiAdam";
import { SHIFT_Y } from "@/lib/scene";

// A CSS mask for the dim layer with a soft hole at the spark (same cover-fit + shift as the
// shader), so the spark stays bright while the hands are dimmed.
export function useSparkHole(radius = 90) {
  const [spark, setSpark] = useState<[number, number] | null>(null);
  useEffect(() => {
    const place = () => {
      const w = window.innerWidth, h = window.innerHeight;
      const unit = Math.max(w / ASPECT, h);
      setSpark([w / 2 + (SCENE.spark[0] - ASPECT / 2) * unit, h * (0.5 + SHIFT_Y) + (SCENE.spark[1] - 0.5) * unit]);
    };
    place();
    window.addEventListener("resize", place);
    return () => window.removeEventListener("resize", place);
  }, []);
  return spark
    ? `radial-gradient(circle ${radius}px at ${spark[0]}px ${spark[1]}px, transparent 0%, transparent 35%, black 100%)`
    : undefined;
}
