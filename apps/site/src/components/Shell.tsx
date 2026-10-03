import Link from "next/link";
import type { ReactNode } from "react";

// The 1200px column with 1px rules on both sides, and the wordmark nav (80px tall, 30px inset).
export function Shell({ children, className = "" }: { children: ReactNode; className?: string }) {
  return (
    <div className={`relative mx-auto min-h-dvh w-full max-w-[var(--container)] ${className}`}>
      <div aria-hidden className="pointer-events-none absolute inset-y-0 left-0 hidden w-px bg-rule min-[1201px]:block" />
      <div aria-hidden className="pointer-events-none absolute inset-y-0 right-0 hidden w-px bg-rule min-[1201px]:block" />
      <nav className="relative z-20 flex h-[75px] items-center px-[15px] min-[810px]:h-20 min-[810px]:px-[30px]">
        <Link href="/" className="text-[28px] font-semibold leading-[19.6px] tracking-normal">
          SCALPAL
        </Link>
      </nav>
      {children}
    </div>
  );
}
