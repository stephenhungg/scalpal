"use client";

import { motion } from "motion/react";

// Word-by-word entrance measured on aeterna: each word goes from opacity 0 + blur(5px)
// to sharp over ~0.5s on an ease-out curve, 50ms apart. `start` continues the stagger
// from a previous block so the subtitle picks up where the headline left off.
export const WORD_STAGGER = 0.05;
export const WORD_DURATION = 0.5;
export const WORD_EASE = [0.215, 0.61, 0.355, 1] as const;
export const FIRST_WORD_DELAY = 0.2;

export function BlurWords({ text, start = 0, className = "", instant = false }: { text: string; start?: number; className?: string; instant?: boolean }) {
  const words = text.split(" ");
  return (
    <span className={className}>
      {words.map((w, i) => (
        <span key={i}>
          <motion.span
            className="inline-block"
            initial={instant ? false : { opacity: 0.001, filter: "blur(5px)" }}
            animate={{ opacity: 1, filter: "blur(0px)" }}
            transition={{ duration: WORD_DURATION, ease: WORD_EASE, delay: FIRST_WORD_DELAY + (start + i) * WORD_STAGGER }}
          >
            {w}
          </motion.span>
          {i < words.length - 1 ? " " : null}
        </span>
      ))}
    </span>
  );
}

