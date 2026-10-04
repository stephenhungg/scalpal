"use client";

import { useEffect, useRef, useState } from "react";
import { MARK } from "@/lib/mark";

// The headline's entrance: the Scalpal mark fades in where the title sits, holds, then dithers
// into the "Scalpal." text. The real <h1> text stays in the layout (invisible) so the canvas can
// draw the letters exactly where they will end up, and the swap at the end is seamless.
const APPEAR = 500; // ms, mark fades in
const HOLD = 1000; // ms, mark on its own
const MORPH = 800; // ms, mark dithers into the text
const CELL = 3; // dither cell, CSS px
const PAD = 40; // canvas bleed around the heading, CSS px

export function TitleMorph({ text, onDone, instant = false }: { text: string; onDone: () => void; instant?: boolean }) {
  const h1 = useRef<HTMLHeadingElement>(null);
  const textRef = useRef<HTMLSpanElement>(null);
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const [done, setDone] = useState(instant);
  const doneRef = useRef(onDone);
  doneRef.current = onDone;

  useEffect(() => {
    const heading = h1.current, span = textRef.current, canvas = canvasRef.current;
    if (!heading || !span || !canvas || instant) return;
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

      // Per-cell thresholds (a stable hash) instead of a 4x4 Bayer: every cell flips at its own
      // moment, so the dissolve moves in hundreds of tiny steps rather than 16 visible clicks.
      const cols = Math.ceil(W / cell), rows = Math.ceil(H / cell);
      const thresh = new Float32Array(cols * rows);
      for (let i = 0; i < thresh.length; i++) {
        let h = Math.imul(i ^ 0x9e3779b9, 0x85ebca6b);
        h ^= h >>> 13;
        h = Math.imul(h, 0xc2b2ae35);
        h ^= h >>> 16;
        thresh[i] = (h >>> 0) / 4294967296;
      }
      const draw = (logoLevel: number, textLevel: number) => {
        ctx.clearRect(0, 0, W, H);
        ctx.fillStyle = ink;
        for (let y = 0; y < H; y += cell) {
          for (let x = 0; x < W; x += cell) {
            const cx = x + (cell >> 1), cy = y + (cell >> 1);
            const t = thresh[((y / cell) | 0) * cols + ((x / cell) | 0)];
            const onText = alphaAt(tData, cx, cy) > 110 && t < textLevel;
            const onMark = alphaAt(mData, cx, cy) > 110 && t < logoLevel;
            if (onText || onMark) ctx.fillRect(x, y, cell, cell);
          }
        }
      };
      const drawMarkSolid = (alpha = 1) => {
        ctx.clearRect(0, 0, W, H);
        ctx.globalAlpha = alpha;
        ctx.drawImage(markMask, 0, 0);
        ctx.globalAlpha = 1;
      };

      const t0 = performance.now();
      const frame = (now: number) => {
        if (cancelled) return;
        const t = now - t0;
        if (t < APPEAR) {
          const p = t / APPEAR;
          drawMarkSolid(p * p * (3 - 2 * p));
        } else if (t < APPEAR + HOLD) {
          drawMarkSolid();
        } else if (t < APPEAR + HOLD + MORPH) {
          // ease the crossover so the middle of the morph lingers in the mixed state
          const p = (t - APPEAR - HOLD) / MORPH;
          const e = p * p * (3 - 2 * p);
          draw(1 - e, e);
          // Both ends hand over between the crisp shapes and the blocky cells gradually, so the
          // morph never jumps: the crisp mark fades into the cells at the start, and the cells
          // fade into crisp letters at the end.
          const smooth = (x: number) => x * x * (3 - 2 * x);
          const fromMark = smooth(Math.max(0, 1 - p / 0.3));
          const toText = smooth(Math.max(0, (p - 0.55) / 0.45));
          const crisp = fromMark + toText;
          if (crisp > 0) {
            ctx.globalAlpha = 1 - crisp;
            ctx.globalCompositeOperation = "destination-in";
            ctx.fillRect(0, 0, W, H);
            ctx.globalCompositeOperation = "source-over";
            if (fromMark > 0) {
              ctx.globalAlpha = fromMark;
              ctx.drawImage(markMask, 0, 0);
            }
            if (toText > 0) {
              ctx.globalAlpha = toText;
              ctx.drawImage(textMask, 0, 0);
            }
            ctx.globalAlpha = 1;
          }
        } else {
          // Leave the crisp letters on the canvas: React swaps to the real text on its next
          // commit, and clearing here first would show an empty frame (a blink).
          ctx.clearRect(0, 0, W, H);
          ctx.drawImage(textMask, 0, 0);
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
  }, [text, instant]);

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
