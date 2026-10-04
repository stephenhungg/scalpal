"use client";

import { motion } from "motion/react";
import { useEffect } from "react";
import { BlurWords } from "./BlurWords";
import { ExploreTitle } from "./ExploreTitle";
import { FadeIn } from "./FadeIn";
import { HandsBackdrop } from "./HandsBackdrop";
import { RollButton } from "./RollButton";
import { Shell } from "./Shell";
import { useLeave } from "./useLeave";
import { EXIT } from "@/lib/scene";
import { session } from "@/lib/session";
import { wordCount } from "@/lib/text";

const TITLE = "The future of surgery.";
const LEDE = "One rep, from the first cut to the robot replay.";

export function ExploreBody({ apkUrl, videoUrl }: { apkUrl: string; videoUrl: string }) {
  const { leaving, go } = useLeave("/");
  // Reaching this page counts as having seen the site: Back returns to the finished landing.
  // Set in an effect so it only ever happens in the browser; module state on the server is
  // shared across requests and would make the server render a different landing than the client.
  useEffect(() => {
    session.introPlayed = true;
  }, []);
  return (
    <main className="relative">
      <HandsBackdrop />
      <Shell className="z-10">
        <motion.section
          className="relative z-10 flex flex-col items-center px-[15px] pb-16 pt-8 text-center min-[810px]:px-[30px] min-[810px]:pt-12"
          animate={leaving ? { opacity: 0, filter: "blur(6px)", y: -10 } : { opacity: 1, filter: "blur(0px)", y: 0 }}
          transition={EXIT}
        >
          <h1 className="display !text-[clamp(44px,5vw,72px)]">
            <ExploreTitle />
          </h1>
          <p className="lede mt-3 max-w-[560px]">
            <BlurWords text={LEDE} start={wordCount(TITLE)} />
          </p>

          <FadeIn delay={0.55} className="mt-8 w-full max-w-[800px]">
            {videoUrl ? (
                <video className="block aspect-video w-full bg-black" src={videoUrl} controls playsInline preload="metadata" />
              ) : (
                <div className="flex aspect-video w-full items-center justify-center bg-[rgb(18,18,18)] text-[14px] text-white/50">
                  Demo video coming soon
                </div>
              )}
          </FadeIn>

          <FadeIn delay={0.75} className="mt-10 flex flex-col items-center gap-4">
            <div className="flex items-center gap-6">
              {apkUrl ? (
                <RollButton href={apkUrl} download label="Download for Quest" hoverLabel="Get the APK" />
              ) : (
                <span className="inline-flex h-[46px] cursor-not-allowed items-center bg-white/30 px-5 text-[14px] font-semibold text-black min-[810px]:h-[50px] min-[810px]:px-9">
                  Download soon
                </span>
              )}
              <RollButton href="/" variant="secondary" label="Back" hoverLabel="Home" onClick={go("/")} />
            </div>
            <p className="text-[14px] leading-[22px] text-white/50">Meta Quest 3 and 3S. Install with SideQuest or adb.</p>
          </FadeIn>
        </motion.section>
      </Shell>
    </main>
  );
}
