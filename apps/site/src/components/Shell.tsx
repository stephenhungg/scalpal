import Link from "next/link";
import type { ReactNode } from "react";

// Full-bleed page with the wordmark nav (80px tall, 30px inset).
export function Shell({ children, className = "" }: { children: ReactNode; className?: string }) {
  return (
    <div className={`relative min-h-dvh w-full ${className}`}>
      <nav className="relative z-20 flex h-[75px] items-center px-[15px] min-[810px]:h-20 min-[810px]:px-[30px]">
        <Link href="/" className="text-[28px] font-semibold leading-[19.6px] tracking-normal">
          SCALPAL
        </Link>
      </nav>
      {children}
    </div>
  );
}
