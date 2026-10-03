"use client";

import { motion } from "motion/react";
import type { ReactNode } from "react";

// Button row entrance on aeterna: plain opacity fade, starting as the subtitle words land.
export function FadeIn({ children, delay = 0, className = "" }: { children: ReactNode; delay?: number; className?: string }) {
  return (
    <motion.div
      className={className}
      initial={{ opacity: 0.001 }}
      animate={{ opacity: 1 }}
      transition={{ duration: 0.4, ease: [0.44, 0, 0.56, 1], delay }}
    >
      {children}
    </motion.div>
  );
}
