"use client";

import { motion } from "motion/react";
import { useEffect, useRef, useState } from "react";

// A monitor-style ECG sweep that runs behind the "scalpal." title. Simulated, slowly drifting HR.
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

export function Ecg({ hr, className = "h-[44px]" }: { hr: number; className?: string }) {
  const ref = useRef<HTMLCanvasElement>(null);
  const hrRef = useRef(hr);
  hrRef.current = hr;

  useEffect(() => {
    const canvas = ref.current!;
    const ctx = canvas.getContext("2d")!;
    const SPEED = 170; // px per second, like a monitor sweep
    const GAP = 26; // erased lead ahead of the write head
    let w = 0, h = 0, dpr = 1;
    let ys: Float32Array = new Float32Array(0);
    let head = 0, phase = 0, carry = 0, last = performance.now(), raf = 0;

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

    const frame = (now: number) => {
      const dt = Math.min(0.05, (now - last) / 1000);
      last = now;
      const period = 60 / hrRef.current;
      // carry fractional pixels so the sweep speed (and so the beat spacing) is exact at any
      // refresh rate
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

      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      ctx.clearRect(0, 0, w, h);
      const mid = h * 0.62, amp = h * 0.5;
      ctx.lineWidth = 1.25;
      ctx.lineJoin = "round";
      ctx.strokeStyle = "rgba(255,255,255,0.22)";
      ctx.beginPath();
      let pen = false;
      for (let x = 0; x < ys.length; x++) {
        const v = ys[x];
        if (Number.isNaN(v)) { pen = false; continue; }
        const y = mid - v * amp;
        if (pen) ctx.lineTo(x, y); else ctx.moveTo(x, y);
        pen = true;
      }
      ctx.stroke();
      // bright write head
      const hx = (Math.floor(head) - 1 + ys.length) % ys.length;
      const hv = ys[hx];
      if (!Number.isNaN(hv)) {
        ctx.fillStyle = "rgba(255,255,255,0.7)";
        ctx.beginPath();
        ctx.arc(hx, mid - hv * amp, 2, 0, Math.PI * 2);
        ctx.fill();
      }
      raf = requestAnimationFrame(frame);
    };
    raf = requestAnimationFrame(frame);
    return () => {
      cancelAnimationFrame(raf);
      ro.disconnect();
    };
  }, []);

  return <canvas ref={ref} aria-hidden className={`block w-full ${className}`} />;
}


export function TitleHeartbeat({ show, instant = false }: { show: boolean; instant?: boolean }) {
  const hr = useHeartRate();
  return (
    <motion.div
      aria-hidden
      className="pointer-events-none absolute left-1/2 top-1/2 -z-10 w-screen -translate-x-1/2 -translate-y-1/2"
      initial={instant ? false : { opacity: 0 }}
      animate={{ opacity: show ? 1 : 0 }}
      transition={{ duration: 1, ease: [0.44, 0, 0.56, 1] }}
    >
      <Ecg hr={hr} className="h-[110px]" />
    </motion.div>
  );
}
