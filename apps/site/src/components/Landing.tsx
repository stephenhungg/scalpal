"use client";

import { motion } from "motion/react";
import { BrandMark } from "./BrandMark";
import { useState } from "react";
import { session } from "@/lib/session";
import { useLeave } from "./useLeave";
import { useSparkHole } from "./useSparkHole";
import { DIM_EXPLORE, DIM_LANDING, EXIT, SHIFT_Y } from "@/lib/scene";
import { AdamLayer } from "./AdamLayer";
import { BlurWords } from "./BlurWords";
import { FadeIn } from "./FadeIn";
import { RollButton } from "./RollButton";
import { TitleMorph } from "./TitleMorph";
import { Footer } from "./Footer";

const TITLE = "scalpal.";
const LEDE = "A mixed reality operating room experience guided by Jarvis, your all-knowing assistant.";

// Loader: only the hands are on screen until the fingertips touch. Then the Scalpal mark dithers
// in where the title goes, holds, and dithers into "Scalpal."; after that the nav, subtitle
// and button come in.

export function Landing() {
  // Coming back from /explore shows the finished page; a full reload plays the intro again.
  const [skip] = useState(() => session.introPlayed);
  const [launched, setLaunched] = useState(skip);
  const [titled, setTitled] = useState(skip);

  const launch = () => setLaunched(true);

  const sparkHole = useSparkHole();
  const { leaving, go } = useLeave("/explore");

  return (
    <main className="relative h-dvh overflow-hidden">
      <div className="absolute inset-0 z-0">
        <AdamLayer onTouch={launch} introSpeed={3.3} holdAfterTouch startTouched={skip} shiftY={SHIFT_Y} />
      </div>
      {/* Once the page launches, dim the hands so the bright glyphs don't fight the text. */}
      <motion.div
        aria-hidden
        className="pointer-events-none absolute inset-0 z-[1] bg-black"
        style={{ maskImage: sparkHole, WebkitMaskImage: sparkHole }}
        // coming back from /explore starts at that page's dim and eases up to the landing's
        initial={{ opacity: skip ? DIM_EXPLORE : 0 }}
        animate={{ opacity: leaving ? DIM_EXPLORE : launched ? DIM_LANDING : 0 }}
        transition={leaving ? EXIT : { duration: 0.9, ease: [0.44, 0, 0.56, 1] }}
      />
      <Footer show={titled && !leaving} instant={skip} />
      {launched && (
        <>
          <motion.nav
            className="relative z-20 flex h-[75px] items-center px-[15px] min-[810px]:h-20 min-[810px]:px-[30px]"
            initial={skip ? false : { opacity: 0 }}
            animate={{ opacity: titled ? 1 : 0 }}
            transition={{ duration: 0.4, ease: [0.44, 0, 0.56, 1] }}
          >
            <BrandMark />
          </motion.nav>
          <motion.section
            className="relative z-10 flex flex-col items-center px-[30px] pt-[148px] text-center min-[810px]:pt-[98px]"
            animate={leaving ? { opacity: 0, filter: "blur(6px)", y: -10 } : { opacity: 1, filter: "blur(0px)", y: 0 }}
            transition={EXIT}
          >
            <TitleMorph
              text={TITLE}
              instant={skip}
              onDone={() => {
                session.introPlayed = true;
                setTitled(true);
              }}
            />
            <p className="lede mt-4 max-w-[660px] min-h-[56px]">{titled && <BlurWords text={LEDE} start={-3} instant={skip} />}</p>
            <div className="mt-[26px] min-h-[50px] min-[810px]:mt-9">
              {titled && (
                <FadeIn delay={0.4} instant={skip}>
                  <RollButton href="/explore" label="Scrub in" hoverStyle="invert" onClick={go("/explore")} />
                </FadeIn>
              )}
            </div>
          </motion.section>
        </>
      )}
    </main>
  );
}
