"use client";

import { motion } from "motion/react";
import { LINKS } from "@/lib/links";

// Landing footer: credit on the left, project links on the right.
const text = "text-[12px] leading-[16px] text-white/50";

export function Footer({ show, instant = false }: { show: boolean; instant?: boolean }) {
  return (
    <motion.footer
      className="pointer-events-none absolute inset-x-0 bottom-0 z-[5] flex items-end justify-between px-[15px] pb-[18px] min-[810px]:px-[30px] min-[810px]:pb-[24px]"
      initial={instant ? false : { opacity: 0 }}
      animate={{ opacity: show ? 1 : 0 }}
      transition={{ duration: 0.8, ease: [0.44, 0, 0.56, 1] }}
    >
      <p className={text}>Bloomed at MHacks 2026</p>
      <nav aria-label="Project links" className={`pointer-events-auto flex gap-5 ${text}`}>
        {LINKS.map((l) => (
          <a key={l.label} href={l.href} target="_blank" rel="noreferrer" className="transition-colors hover:text-white">
            {l.label} ↗
          </a>
        ))}
      </nav>
    </motion.footer>
  );
}
