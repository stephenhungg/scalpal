"use client";

import { motion } from "motion/react";
import Link from "next/link";
import type { ComponentProps } from "react";

// Label rolls up 34px on hover. Spring fitted to the measured trace on aeterna:
// 1.5% overshoot peaking at ~345ms, settled by ~600ms.
const ROLL = { type: "spring" as const, stiffness: 230, damping: 24, mass: 1 };
const MotionLink = motion.create(Link);

type Props = {
  label: string;
  hoverLabel?: string;
  variant?: "primary" | "secondary";
  href: string;
  download?: boolean;
  external?: boolean;
  /** "roll" swaps to hoverLabel; "invert" keeps the label and flips the colours. */
  hoverStyle?: "roll" | "invert";
} & Omit<ComponentProps<"a">, "href">;

export function RollButton({ label, hoverLabel = label, variant = "primary", href, download, external, hoverStyle = "roll", className = "", ...rest }: Props) {
  const base =
    "group relative inline-flex h-[46px] items-center justify-center overflow-visible px-5 text-[14px] font-semibold leading-[22px] min-[810px]:h-[50px] min-[810px]:px-9";
  const invert =
    "border border-ink transition-colors duration-200 ease-out " +
    (variant === "primary" ? "hover:bg-bg hover:text-ink" : "hover:bg-ink hover:text-bg");
  const look = (variant === "primary" ? "bg-ink text-bg" : "bg-bg text-ink") + (hoverStyle === "invert" ? ` ${invert}` : "");
  const inner = (
    <>
      {variant === "secondary" && <Crosshairs />}
      {hoverStyle === "invert" ? (
        <span className="block h-[22px] whitespace-nowrap">{label}</span>
      ) : (
      <span className="relative block h-[22px] overflow-hidden">
        <motion.span className="flex flex-col items-center gap-3" variants={{ rest: { y: 0 }, hover: { y: -34 } }} transition={ROLL}>
          <span className="block h-[22px] whitespace-nowrap">{label}</span>
          <span aria-hidden className="block h-[22px] whitespace-nowrap">{hoverLabel}</span>
        </motion.span>
      </span>
      )}
    </>
  );
  if (download || external) {
    return (
      <motion.a href={href} download={download || undefined} className={`${base} ${look} ${className}`} initial="rest" animate="rest" whileHover="hover" {...(rest as object)}>
        {inner}
      </motion.a>
    );
  }
  return (
    <MotionLink href={href} className={`${base} ${look} ${className}`} initial="rest" animate="rest" whileHover="hover">
      {inner}
    </MotionLink>
  );
}

// Secondary buttons carry small registration crosses at each corner; on hover they turn into x's.
function Crosshairs() {
  const arm = "absolute bg-[var(--cross)]";
  const corner = (pos: string) => (
    <span
      aria-hidden
      className={`pointer-events-none absolute h-[13px] w-[13px] transition-transform duration-300 ease-[cubic-bezier(0.34,1.56,0.64,1)] group-hover:rotate-45 ${pos}`}
    >
      <span className={`${arm} left-1/2 top-0 h-full w-px -translate-x-1/2`} />
      <span className={`${arm} left-0 top-1/2 h-px w-full -translate-y-1/2`} />
    </span>
  );
  return (
    <>
      {corner("-left-[6px] -top-[6px]")}
      {corner("-right-[6px] -top-[6px]")}
      {corner("-left-[6px] -bottom-[6px]")}
      {corner("-right-[6px] -bottom-[6px]")}
    </>
  );
}
