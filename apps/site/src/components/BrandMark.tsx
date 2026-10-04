import Link from "next/link";
import { DitherLogo } from "./DitherLogo";

// Logo + period, like the end of a sentence (matches the "Scalpal." headline).
export function BrandMark({ onClick }: { onClick?: (e: React.MouseEvent) => void }) {
  return (
    <Link href="/" onClick={onClick} aria-label="Scalpal home" className="flex items-end gap-px">
      <DitherLogo size={34} />
      <span aria-hidden className="-ml-[3px] -translate-y-[5px] font-[family-name:var(--font-serif)] text-[36px] leading-[0.62]">
        .
      </span>
    </Link>
  );
}
