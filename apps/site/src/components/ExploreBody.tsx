"use client";

import { motion } from "motion/react";
import { useEffect } from "react";
import { BlurWords } from "./BlurWords";
import { ExploreTitle } from "./ExploreTitle";
import { FadeIn } from "./FadeIn";
import { HandsBackdrop } from "./HandsBackdrop";
import { RollButton } from "./RollButton";
import { Shell } from "./Shell";
import { TrackWheel } from "./TrackWheel";
import { useLeave } from "./useLeave";
import { AGENT_PROMPT } from "@/lib/links";
import { EXIT } from "@/lib/scene";
import { session } from "@/lib/session";
import { wordCount } from "@/lib/text";

const TITLE = "The future of surgery.";
const LEDE = "One workflow, from the first cut to the robot replay.";

export function ExploreBody({ apkUrl, videoUrl, embedUrl = "" }: { apkUrl: string; videoUrl: string; embedUrl?: string }) {
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
      <Shell className="z-10 flex flex-col" onLogoClick={go("/")}>
        <motion.section
          className="relative z-10 flex flex-col items-center px-[15px] pb-4 pt-6 text-center min-[810px]:px-[30px] min-[810px]:pt-12"
          animate={leaving ? { opacity: 0, filter: "blur(6px)", y: -10 } : { opacity: 1, filter: "blur(0px)", y: 0 }}
          transition={EXIT}
        >
          <h1 className="display !text-[clamp(44px,5vw,72px)]">
            <ExploreTitle />
          </h1>
          <p className="lede mt-3 max-w-[560px]">
            <BlurWords text={LEDE} start={wordCount(TITLE)} />
          </p>

          <FadeIn delay={0.55} className="mt-6 w-full max-w-[min(800px,calc((100dvh-430px)*16/9))]">
            {embedUrl ? (
                <iframe
                  className="block aspect-video w-full bg-black"
                  src={embedUrl}
                  title="Scalpal demo"
                  allow="accelerometer; autoplay; clipboard-write; encrypted-media; gyroscope; picture-in-picture; web-share"
                  referrerPolicy="strict-origin-when-cross-origin"
                  allowFullScreen
                />
              ) : videoUrl ? (
                <video className="block aspect-video w-full bg-black" src={videoUrl} controls playsInline preload="metadata" />
              ) : (
                <div className="flex aspect-video w-full items-center justify-center bg-[rgb(18,18,18)] text-[14px] text-white/50">
                  Demo video coming soon
                </div>
              )}
          </FadeIn>

          <FadeIn delay={0.75} className="mt-7 flex flex-col items-center gap-3">
            <div className="flex items-center gap-6">
              {apkUrl ? (
                <RollButton href={apkUrl} download label="Download for Quest" hoverLabel="Get the APK" />
              ) : (
                <span className="inline-flex h-[46px] cursor-not-allowed items-center bg-white/30 px-5 text-[14px] font-semibold text-black min-[810px]:h-[50px] min-[810px]:px-9">
                  Download soon
                </span>
              )}
              <RollButton href="#" variant="secondary" copy={AGENT_PROMPT} label="Build your own case" hoverLabel="Copy agent prompt" />
            </div>
            <p className="text-[14px] leading-[22px] text-white/50">Meta Quest 3 and 3S. Install with SideQuest or adb.</p>
          </FadeIn>

        </motion.section>

        {/* bottom center of the page: pinned to the bottom when the content fits, after it otherwise */}
        <motion.div
          className="relative z-10 mt-auto w-full px-[15px] pb-5 pt-4 min-[810px]:px-[30px]"
          animate={leaving ? { opacity: 0 } : { opacity: 1 }}
          transition={EXIT}
        >
          <FadeIn delay={0.95}>
            <TrackWheel />
          </FadeIn>
        </motion.div>
      </Shell>
    </main>
  );
}
