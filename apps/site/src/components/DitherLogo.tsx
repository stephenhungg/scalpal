"use client";

import { useEffect, useRef } from 'react';
import { BAYER, MARK } from '@/lib/mark';

// Scalpal mark (traced from the team logo). Solid white at rest; on hover a dither wave
// sweeps across it: cells thin out through a 4x4 Bayer pattern, the wave front glints green,
// then the mark resolves back to solid.
const DURATION = 750; // ms

export function DitherLogo({ size = 34 }: { size?: number }) {
  const ref = useRef<HTMLCanvasElement>(null);

  useEffect(() => {
    const canvas = ref.current!;
    const ctx = canvas.getContext('2d')!;
    const dpr = Math.min(window.devicePixelRatio || 1, 3);
    const px = Math.round(size * dpr);
    canvas.width = canvas.height = px;
    const path = new Path2D(MARK);
    const css = getComputedStyle(canvas);
    const ink = css.getPropertyValue('--ink').trim() || '#fff';
    const glint = '#8ef08a'; // landing spark green

    // Rasterize the mark once to read the mask.
    const off = document.createElement('canvas');
    off.width = off.height = px;
    const octx = off.getContext('2d')!;
    octx.scale(px / 100, px / 100);
    octx.fill(path);
    const mask = octx.getImageData(0, 0, px, px).data;

    const cell = Math.max(2, Math.round(1.25 * dpr)); // dither cell in device px
    const drawSolid = () => {
      ctx.setTransform(1, 0, 0, 1, 0, 0);
      ctx.clearRect(0, 0, px, px);
      ctx.setTransform(px / 100, 0, 0, px / 100, 0, 0);
      ctx.fillStyle = ink;
      ctx.fill(path);
    };
    const drawDither = (p: number) => {
      ctx.setTransform(1, 0, 0, 1, 0, 0);
      ctx.clearRect(0, 0, px, px);
      for (let y = 0; y < px; y += cell) {
        for (let x = 0; x < px; x += cell) {
          const cx = Math.min(px - 1, x + (cell >> 1)), cy = Math.min(px - 1, y + (cell >> 1));
          if (mask[(cy * px + cx) * 4 + 3] < 128) continue;
          // wave front moves left to right (slight diagonal) across the mark
          const f = p * 1.7 - (x / px) * 0.55 - (y / px) * 0.15;
          const w = Math.sin(Math.PI * Math.min(1, Math.max(0, f)));
          const density = 1 - 0.85 * w;
          const t = BAYER[((y / cell) & 3) * 4 + ((x / cell) & 3)];
          if (t >= density) continue;
          ctx.fillStyle = w > 0.82 ? glint : ink;
          ctx.fillRect(x, y, cell - (w > 0.05 ? 1 : 0), cell - (w > 0.05 ? 1 : 0));
        }
      }
    };

    drawSolid();
    let raf = 0;
    let start = 0;
    const reduce = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    const tick = (now: number) => {
      const p = (now - start) / DURATION;
      if (p >= 1) { drawSolid(); raf = 0; return; }
      drawDither(p);
      raf = requestAnimationFrame(tick);
    };
    const play = () => {
      if (reduce || raf) return;
      start = performance.now();
      raf = requestAnimationFrame(tick);
    };
    const host = canvas.closest('a') ?? canvas;
    host.addEventListener('pointerenter', play);
    host.addEventListener('focus', play);
    return () => {
      cancelAnimationFrame(raf);
      host.removeEventListener('pointerenter', play);
      host.removeEventListener('focus', play);
    };
  }, [size]);

  return <canvas ref={ref} className="block" role="img" aria-label="Scalpal" style={{ width: size, height: size }} />;
}
