"use client";

import { useEffect, useRef, useState } from "react";
import { BAYER, MARK } from "@/lib/mark";

// The headline's entrance: the Scalpal mark dithers in where the title sits, holds, then dithers
// into the "Scalpal." text. The real <h1> text stays in the layout (invisible) so the canvas can
// draw the letters exactly where they will end up, and the swap at the end is seamless.
const APPEAR = 450; // ms, mark dithers in
const HOLD = 1000; // ms, mark on its own
const MORPH = 800; // ms, mark dithers into the text
const CELL = 3; // dither cell, CSS px
const PAD = 40; // canvas bleed around the heading, CSS px

export function TitleMorph({ text, onDone }: { text: string; onDone: () => void }) {
  const h1 = useRef<HTMLHeadingElement>(null);
  const textRef = useRef<HTMLSpanElement>(null);
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const [done, setDone] = useState(false);
  const doneRef = useRef(onDone);
  doneRef.current = onDone;

  useEffect(() => {
    const heading = h1.current, span = textRef.current, canvas = canvasRef.current;
    if (!heading || !span || !canvas) return;
    let raf = 0;
    let cancelled = false;
    const finish = () => {
      if (cancelled) return;
      setDone(true);
      doneRef.current();
    };
    if (window.matchMedia("(prefers-reduced-motion: reduce)").matches) {
      finish();
      return;
    }

    document.fonts.ready.then(() => {
      if (cancelled) return;
      const box = heading.getBoundingClientRect();
      const w = box.width + PAD * 2, h = box.height + PAD * 2;
      const dpr = Math.min(window.devicePixelRatio || 1, 2);
      const W = Math.round(w * dpr), H = Math.round(h * dpr);
      canvas.width = W;
      canvas.height = H;
      canvas.style.width = `${w}px`;
      canvas.style.height = `${h}px`;
      const ctx = canvas.getContext("2d")!;

      // Text mask, placed where the real text renders.
      const cs = getComputedStyle(span);
      const range = document.createRange();
      range.selectNodeContents(span);
      const tr = range.getBoundingClientRect();
      const textMask = document.createElement("canvas");
      textMask.width = W;
      textMask.height = H;
      const tctx = textMask.getContext("2d")!;
      tctx.scale(dpr, dpr);
      tctx.font = `${cs.fontWeight} ${cs.fontSize} ${cs.fontFamily}`;
      if ("letterSpacing" in tctx) (tctx as CanvasRenderingContext2D & { letterSpacing: string }).letterSpacing = cs.letterSpacing;
      tctx.textBaseline = "alphabetic";
      const fm = tctx.measureText(text);
      const baseline = tr.top - box.top + PAD + fm.fontBoundingBoxAscent;
      tctx.fillStyle = "#fff";
      tctx.fillText(text, tr.left - box.left + PAD, baseline);

      // Mark mask, centered on the heading, about as tall as the cap height.
      const markMask = document.createElement("canvas");
      markMask.width = W;
      markMask.height = H;
      const mctx = markMask.getContext("2d")!;
      const markSize = box.height * 1.55; // 100-unit viewBox; the mark itself spans ~45 units tall
      mctx.scale(dpr, dpr);
      mctx.translate(w / 2 - markSize / 2, h / 2 - markSize * 0.5);
      mctx.scale(markSize / 100, markSize / 100);
      mctx.fillStyle = "#fff";
      mctx.fill(new Path2D(MARK));

      const tData = tctx.getImageData(0, 0, W, H).data;
      const mData = mctx.getImageData(0, 0, W, H).data;
      const cell = Math.max(1, Math.round(CELL * dpr));
      const ink = getComputedStyle(heading).color || "#fff";
      const alphaAt = (data: Uint8ClampedArray, x: number, y: number) => data[(Math.min(H - 1, y) * W + Math.min(W - 1, x)) * 4 + 3];

      const draw = (logoLevel: number, textLevel: number) => {
        ctx.clearRect(0, 0, W, H);
        ctx.fillStyle = ink;
        for (let y = 0; y < H; y += cell) {
          for (let x = 0; x < W; x += cell) {
            const cx = x + (cell >> 1), cy = y + (cell >> 1);
            const t = BAYER[(((y / cell) | 0) & 3) * 4 + (((x / cell) | 0) & 3)];
            const onText = alphaAt(tData, cx, cy) > 110 && t < textLevel;
            const onMark = alphaAt(mData, cx, cy) > 110 && t < logoLevel;
            if (onText || onMark) ctx.fillRect(x, y, cell, cell);
          }
        }
      };
      const drawMarkSolid = () => {
        ctx.clearRect(0, 0, W, H);
        ctx.drawImage(markMask, 0, 0);
      };

      const t0 = performance.now();
      const frame = (now: number) => {
        if (cancelled) return;
        const t = now - t0;
        if (t < APPEAR) {
          draw(t / APPEAR, 0);
        } else if (t < APPEAR + HOLD) {
          drawMarkSolid();
        } else if (t < APPEAR + HOLD + MORPH) {
          // ease the crossover so the middle of the morph lingers in the mixed state
          const p = (t - APPEAR - HOLD) / MORPH;
          const e = p * p * (3 - 2 * p);
          draw(1 - e, e);
        } else {
          ctx.clearRect(0, 0, W, H);
          finish();
          return;
        }
        raf = requestAnimationFrame(frame);
      };
      raf = requestAnimationFrame(frame);
    });

    return () => {
      cancelled = true;
      cancelAnimationFrame(raf);
    };
  }, [text]);

  return (
    <h1 ref={h1} className="display relative">
      <span ref={textRef} style={{ visibility: done ? "visible" : "hidden" }}>
        {text}
      </span>
      <canvas
        ref={canvasRef}
        aria-hidden
        className="pointer-events-none absolute"
        style={{ left: -PAD, top: -PAD, display: done ? "none" : "block" }}
      />
    </h1>
  );
}
