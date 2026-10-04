"use client";

import { motion } from "motion/react";
import { useEffect, useRef, useState } from "react";

// An ASCII ECG sweep behind the "scalpal." title, reaching a little past it on each side. Simulated, slowly drifting HR.
function useHeartRate() {
  const [v, setV] = useState({ hr: 72, spo2: 98, sys: 118, dia: 76 });
  useEffect(() => {
    const id = setInterval(() => {
      setV((p) => ({
        hr: Math.max(68, Math.min(76, p.hr + Math.round((Math.random() - 0.5) * 2))),
        spo2: Math.random() < 0.15 ? (p.spo2 === 98 ? 99 : 98) : p.spo2,
        sys: Math.max(114, Math.min(122, p.sys + Math.round((Math.random() - 0.5) * 2))),
        dia: Math.max(72, Math.min(80, p.dia + Math.round((Math.random() - 0.5) * 2))),
      }));
    }, 1800);
    return () => clearInterval(id);
  }, []);
  return v.hr;
}

// One PQRST beat, x in [0,1) of the beat, y upward in [-0.3, 1].
function beat(x: number) {
  const g = (c: number, w: number, a: number) => a * Math.exp(-((x - c) ** 2) / (2 * w * w));
  return g(0.18, 0.025, 0.12) - g(0.29, 0.008, 0.18) + g(0.31, 0.009, 1) - g(0.335, 0.01, 0.28) + g(0.55, 0.045, 0.22);
}

const SCRAMBLE = "#%*+=:.@&$?/\\|<>";

// ASCII ECG: the trace is drawn as monospace glyphs on a grid ("-" flat, "/" "\\" slopes,
// "|" spikes, "@" write head), fading with age like monitor phosphor. Moving the pointer over it
// scrambles the glyphs nearby, the same hover language as the ASCII hands.
export function Ecg({ hr, className = "h-[44px]" }: { hr: number; className?: string }) {
  const ref = useRef<HTMLCanvasElement>(null);
  const hrRef = useRef(hr);
  hrRef.current = hr;

  useEffect(() => {
    const canvas = ref.current!;
    const ctx = canvas.getContext("2d")!;
    const SPEED = 170; // px per second, like a monitor sweep
    const GAP = 28; // erased lead ahead of the write head, px
    const CW = 5, CH = 8; // glyph cell, CSS px (small, so it reads as texture)
    const RADIUS = 40; // hover scramble radius, CSS px
    let w = 0, h = 0, dpr = 1;
    let ys: Float32Array = new Float32Array(0);
    let head = 0, phase = 0, carry = 0, last = performance.now(), raf = 0;
    const pointer = { x: -1e4, y: -1e4 };
    let scramble = 0, lastMove = 0;
    const font = `${getComputedStyle(document.body).getPropertyValue("--font-mono") || "monospace"}`;

    const resize = () => {
      dpr = Math.min(window.devicePixelRatio || 1, 2);
      w = canvas.clientWidth;
      h = canvas.clientHeight;
      canvas.width = Math.round(w * dpr);
      canvas.height = Math.round(h * dpr);
      ys = new Float32Array(Math.ceil(w)).fill(NaN);
      head = 0;
    };
    resize();
    const ro = new ResizeObserver(resize);
    ro.observe(canvas);
    const onMove = (e: PointerEvent) => {
      const r = canvas.getBoundingClientRect();
      pointer.x = e.clientX - r.left;
      pointer.y = e.clientY - r.top;
      const inside = pointer.x > -RADIUS && pointer.y > -RADIUS && pointer.x < r.width + RADIUS && pointer.y < r.height + RADIUS;
      if (inside) lastMove = performance.now();
    };
    window.addEventListener("pointermove", onMove, { passive: true });

    const hash = (a: number, b: number) => {
      let x = Math.imul(a * 374761393 + b * 668265263, 1274126177);
      x ^= x >>> 13;
      return ((x >>> 0) % 10007) / 10007;
    };

    const frame = (now: number) => {
      const dt = Math.min(0.05, (now - last) / 1000);
      last = now;
      const period = 60 / hrRef.current;
      carry += SPEED * dt;
      const steps = Math.floor(carry);
      carry -= steps;
      for (let i = 0; i < steps; i++) {
        phase = (phase + 1 / SPEED / period) % 1;
        const x = Math.floor(head) % ys.length;
        ys[x] = beat(phase);
        for (let k = 1; k <= GAP; k++) ys[(x + k) % ys.length] = NaN;
        head = (head + 1) % ys.length;
      }
      // scramble rises while the pointer moves near the trace and settles when it rests
      const target = now - lastMove < 120 ? 1 : 0;
      scramble += (target - scramble) * (1 - Math.exp(-dt * (target > scramble ? 14 : 2.5)));

      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      ctx.clearRect(0, 0, w, h);
      ctx.font = `${CH}px ${font}`;
      ctx.textBaseline = "top";
      const mid = h * 0.6, amp = h * 0.38;
      const cols = Math.floor(w / CW), rows = Math.floor(h / CH);
      const tick = Math.floor(now / 70);
      const headX = (Math.floor(head) - 1 + ys.length) % ys.length;

      for (let c = 0; c < cols; c++) {
        const x0 = c * CW, x1 = Math.min(ys.length, x0 + CW);
        let lo = Infinity, hi = -Infinity, first = NaN, lastV = NaN;
        for (let x = x0; x < x1; x++) {
          const v = ys[x];
          if (Number.isNaN(v)) continue;
          const y = mid - v * amp;
          lo = Math.min(lo, y);
          hi = Math.max(hi, y);
          if (Number.isNaN(first)) first = y;
          lastV = y;
        }
        const cx = x0 + CW / 2;
        const near = scramble * Math.max(0, 1 - Math.hypot(cx - pointer.x, mid - pointer.y) / RADIUS);
        if (lo === Infinity) {
          // empty cell: a little noise around the pointer, like the hands' hover
          if (near > 0.2 && hash(c, tick) < near * 0.25) {
            const r = Math.floor(hash(c + 7, tick) * rows);
            ctx.fillStyle = "rgba(255,255,255,0.14)";
            ctx.fillText(SCRAMBLE[Math.floor(hash(c, r + tick) * SCRAMBLE.length)], x0, r * CH);
          }
          continue;
        }
        // age behind the write head, for the phosphor fade
        const age = (headX - x0 + ys.length) % ys.length;
        const alpha = 0.12 + 0.45 * Math.exp(-age / 260);
        const r0 = Math.max(0, Math.floor(lo / CH)), r1 = Math.min(rows - 1, Math.floor(hi / CH));
        const slope = lastV - first;
        for (let r = r0; r <= r1; r++) {
          let ch: string;
          if (r1 > r0) ch = "|";
          else if (Math.abs(slope) < 1.2) ch = (lo - r * CH) > CH * 0.55 ? "_" : "-";
          else ch = slope < 0 ? "/" : "\\";
          if (age < CW) ch = "@";
          if (near > 0 && hash(c * 31 + r, tick) < near) ch = SCRAMBLE[Math.floor(hash(r, c + tick) * SCRAMBLE.length)];
          ctx.fillStyle = `rgba(255,255,255,${age < CW ? 0.75 : alpha})`;
          ctx.fillText(ch, x0, r * CH);
        }
      }
      raf = requestAnimationFrame(frame);
    };
    raf = requestAnimationFrame(frame);
    return () => {
      cancelAnimationFrame(raf);
      ro.disconnect();
      window.removeEventListener("pointermove", onMove);
    };
  }, []);

  return <canvas ref={ref} aria-hidden className={`block w-full ${className}`} />;
}

export function TitleHeartbeat({ show, instant = false }: { show: boolean; instant?: boolean }) {
  const hr = useHeartRate();
  return (
    <motion.div
      aria-hidden
      className="pointer-events-none absolute -inset-x-[120px] top-1/2 -z-10 -translate-y-1/2"
      // just past the title on each side, fading out at the ends
      style={{
        maskImage: "linear-gradient(90deg, transparent, black 22%, black 78%, transparent)",
        WebkitMaskImage: "linear-gradient(90deg, transparent, black 22%, black 78%, transparent)",
      }}
      initial={instant ? false : { opacity: 0 }}
      animate={{ opacity: show ? 1 : 0 }}
      transition={{ duration: 1, ease: [0.44, 0, 0.56, 1] }}
    >
      <Ecg hr={hr} className="h-[110px]" />
    </motion.div>
  );
}
