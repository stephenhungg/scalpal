"use client";

import { motion } from "motion/react";
import { useEffect, useRef, useState } from "react";
import { BlurWords, FIRST_WORD_DELAY, WORD_DURATION, WORD_EASE, WORD_STAGGER } from "./BlurWords";
import TechText from "./TechText";

// "The future of surgery." with "future" drawn by React Bits TechText. The real word stays in the
// line (invisible) to hold its width and baseline; TechText sits over it in a padded box, at the
// heading's measured font size and tracking, nudged so its ink lines up with the neighbours.
const PAD_X = 0.15; // extra width each side, as a fraction of the word's width
const PAD_Y = 0.45; // extra height above and below, as a fraction of the line box

type Fit = { size: number; tracking: number; shiftY: number };

function FutureWord({ index }: { index: number }) {
  const word = useRef<HTMLSpanElement>(null);
  const base = useRef<HTMLSpanElement>(null);
  const [fit, setFit] = useState<Fit | null>(null);

  useEffect(() => {
    const el = word.current, marker = base.current;
    if (!el || !marker) return;
    const measure = () => {
      const cs = getComputedStyle(el);
      const size = parseFloat(cs.fontSize);
      const tracking = (parseFloat(cs.letterSpacing) || 0) / size;
      const ctx = document.createElement("canvas").getContext("2d");
      if (!ctx) return;
      ctx.font = `${cs.fontWeight} ${size}px ${cs.fontFamily}`;
      const m = ctx.measureText("future");
      const baseline = marker.offsetTop; // baseline y inside the word box
      const inkCenter = baseline - (m.actualBoundingBoxAscent - m.actualBoundingBoxDescent) / 2;
      setFit({ size, tracking, shiftY: inkCenter - el.offsetHeight / 2 });
    };
    document.fonts.ready.then(measure);
    const ro = new ResizeObserver(measure);
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  return (
    <span ref={word} className="relative inline-block">
      <span className="invisible">future</span>
      <span ref={base} aria-hidden className="inline-block h-0 w-0 align-baseline" />
      {fit && (
        <motion.span
          className="absolute"
          style={{
            left: `${-PAD_X * 100}%`,
            right: `${-PAD_X * 100}%`,
            top: `${-PAD_Y * 100}%`,
            bottom: `${-PAD_Y * 100}%`,
            translateY: fit.shiftY,
          }}
          initial={{ opacity: 0.001, filter: "blur(5px)" }}
          animate={{ opacity: 1, filter: "blur(0px)" }}
          transition={{ duration: WORD_DURATION, ease: WORD_EASE, delay: FIRST_WORD_DELAY + index * WORD_STAGGER }}
        >
          <TechText
            text="future"
            fontWeight={400}
            fontSize={fit.size}
            letterSpacing={fit.tracking}
            color="#ffffff"
            accentColor="#ffffff"
            reveal="letter"
            dashLength={4}
            dashGap={2}
            specks={15}
            style={{}}
          />
        </motion.span>
      )}
    </span>
  );
}

export function ExploreTitle() {
  return (
    <>
      <BlurWords text="The" /> <FutureWord index={1} /> <BlurWords text="of surgery." start={2} />
    </>
  );
}
