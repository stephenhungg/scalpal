"use client";

import { useRouter } from "next/navigation";
import { useCallback, useEffect, useState } from "react";
import { EXIT_MS } from "@/lib/scene";

// Play the page's exit (content fades out, hands dim) before navigating, instead of a hard cut.
export function useLeave(prefetch?: string) {
  const router = useRouter();
  const [leaving, setLeaving] = useState(false);
  useEffect(() => {
    if (prefetch) router.prefetch(prefetch);
  }, [router, prefetch]);
  const go = useCallback(
    (href: string) => (e?: { preventDefault(): void; metaKey?: boolean; ctrlKey?: boolean }) => {
      if (e?.metaKey || e?.ctrlKey) return; // let new-tab clicks through
      e?.preventDefault();
      if (leaving) return;
      setLeaving(true);
      window.setTimeout(() => router.push(href), EXIT_MS);
    },
    [router, leaving],
  );
  return { leaving, go };
}
