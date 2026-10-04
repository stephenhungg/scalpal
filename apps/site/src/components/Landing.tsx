"use client";

import { motion } from "motion/react";
import { BrandMark } from "./BrandMark";
import { useState } from "react";
import { AdamLayer } from "./AdamLayer";
import { BlurWords } from "./BlurWords";
import { FadeIn } from "./FadeIn";
import { RollButton } from "./RollButton";
import { TitleMorph } from "./TitleMorph";

const TITLE = "scalpal.";
const LEDE = "Practice surgery in mixed reality with a voice coach, and turn every rep into motion a robot hand can replay.";

// Loader: only the hands are on screen until the fingertips touch. Then the Scalpal mark dithers
// in where the title goes, holds, and dithers into "Scalpal."; after that the nav, subtitle
// and button come in.
// Set once the intro has played. Module state survives client-side navigation (Back from
// /explore) but resets on a full reload, so coming back shows the finished page while a
// refresh plays the intro again.
let introPlayed = false;

export function Landing() {
  const [skip] = useState(() => introPlayed);
  const [launched, setLaunched] = useState(skip);
  const [titled, setTitled] = useState(skip);

  const launch = () => setLaunched(true);

  return (
    <main className="relative h-dvh overflow-hidden">
      <div className="absolute inset-0 z-0">
        <AdamLayer onTouch={launch} introSpeed={3.3} holdAfterTouch startTouched={skip} />
      </div>
      {/* Once the page launches, dim the hands so the bright glyphs don't fight the text. */}
      <motion.div
        aria-hidden
        className="pointer-events-none absolute inset-0 z-[1] bg-black"
        initial={skip ? false : { opacity: 0 }}
        animate={{ opacity: launched ? 0.55 : 0 }}
        transition={{ duration: 0.9, ease: [0.44, 0, 0.56, 1] }}
      />
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
          <section className="relative z-10 flex flex-col items-center px-[30px] pt-[148px] text-center min-[810px]:pt-[98px]">
            <TitleMorph
              text={TITLE}
              instant={skip}
              onDone={() => {
                introPlayed = true;
                setTitled(true);
              }}
            />
            <p className="lede mt-4 max-w-[660px] min-h-[56px]">{titled && <BlurWords text={LEDE} start={-3} instant={skip} />}</p>
            <div className="mt-[26px] min-h-[50px] min-[810px]:mt-9">
              {titled && (
                <FadeIn delay={0.4} instant={skip}>
                  <RollButton href="/explore" label="Explore" hoverLabel="Watch the demo" />
                </FadeIn>
              )}
            </div>
          </section>
        </>
      )}
    </main>
  );
}
