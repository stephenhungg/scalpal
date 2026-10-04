"use client";

import { motion } from "motion/react";
import { useEffect, useState } from "react";

// OR-monitor / imaging overlay for the landing: scan-style corner readouts. Values are a
// simulation (labelled SIM), not patient data.

const label = "text-[11px] uppercase leading-[16px] tracking-[0.08em] text-white/45";
const strong = "text-white/80";

function useVitals() {
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
  return v;
}

function useClock() {
  const [t, setT] = useState(0);
  useEffect(() => {
    const t0 = performance.now();
    let raf = 0;
    const tick = () => {
      setT((performance.now() - t0) / 1000);
      raf = requestAnimationFrame(tick);
    };
    raf = requestAnimationFrame(tick);
    return () => cancelAnimationFrame(raf);
  }, []);
  return t;
}

const pad = (n: number, w: number) => String(Math.floor(n)).padStart(w, "0");

export function MonitorHud({ show, instant = false }: { show: boolean; instant?: boolean }) {
  const v = useVitals();
  const t = useClock();
  const frameNo = Math.floor(t * 30);

  return (
    <motion.div
      aria-hidden
      className="pointer-events-none absolute inset-0 z-[5] font-[family-name:var(--font-mono)]"
      initial={instant ? false : { opacity: 0 }}
      animate={{ opacity: show ? 1 : 0 }}
      transition={{ duration: 0.8, ease: [0.44, 0, 0.56, 1] }}
    >
      {/* top right: case */}
      <div className={`absolute right-[15px] top-[24px] text-right min-[810px]:right-[30px] min-[810px]:top-[28px] ${label}`}>
        <div className={strong}>Case 001 · Lap appendectomy</div>
        <div>Sim · synthetic patient</div>
      </div>

      {/* bottom left: vitals */}
      <div className={`absolute bottom-[18px] left-[15px] min-[810px]:left-[30px] ${label}`}>
        <div>
          HR <span className={strong}>{v.hr}</span> · SpO<sub className="text-[8px]">2</sub>{" "}
          <span className={strong}>{v.spo2}%</span>
        </div>
        <div>
          NIBP <span className={strong}>{v.sys}/{v.dia}</span> mmHg
        </div>
      </div>

      {/* bottom right: frame + clock */}
      <div className={`absolute bottom-[18px] right-[15px] text-right min-[810px]:right-[30px] ${label}`}>
        <div>
          Frame <span className={strong}>{pad(frameNo, 5)}</span> · Zoom 1.0
        </div>
        <div>
          T+ <span className={strong}>{pad(t / 60, 2)}:{pad(t % 60, 2)}.{pad((t % 1) * 100, 2)}</span>
        </div>
      </div>
    </motion.div>
  );
}
