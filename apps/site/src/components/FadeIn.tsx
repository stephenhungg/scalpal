"use client";

import { motion } from "motion/react";
import type { ReactNode } from "react";

// Button row entrance on aeterna: plain opacity fade, starting as the subtitle words land.
export function FadeIn({ children, delay = 0, className = "", instant = false }: { children: ReactNode; delay?: number; className?: string; instant?: boolean }) {
  return (
    <motion.div
      className={className}
      initial={instant ? false : { opacity: 0.001 }}
      animate={{ opacity: 1 }}
      transition={{ duration: 0.4, ease: [0.44, 0, 0.56, 1], delay }}
    >
      {children}
    </motion.div>
  );
}
