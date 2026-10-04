import type { Metadata } from "next";
import { BlurWords } from "@/components/BlurWords";
import { ExploreTitle } from "@/components/ExploreTitle";
import { wordCount } from "@/lib/text";
import { FadeIn } from "@/components/FadeIn";
import { RollButton } from "@/components/RollButton";
import { Shell } from "@/components/Shell";
import { APK_URL, DEMO_VIDEO_URL } from "@/lib/site";

export const metadata: Metadata = { title: "Scalpal · Demo" };

const TITLE = "The future of surgery.";
const LEDE = "One rep, from the first cut to the robot replay.";

export default function Explore() {
  return (
    <main>
      <Shell>
        <section className="relative z-10 flex flex-col items-center px-[15px] pb-16 pt-8 text-center min-[810px]:px-[30px] min-[810px]:pt-12">
          <h1 className="display !text-[clamp(44px,5vw,72px)]">
            <ExploreTitle />
          </h1>
          <p className="lede mt-3 max-w-[560px]">
            <BlurWords text={LEDE} start={wordCount(TITLE)} />
          </p>

          <FadeIn delay={0.55} className="mt-8 w-full max-w-[800px]">
            {DEMO_VIDEO_URL ? (
                <video className="block aspect-video w-full bg-black" src={DEMO_VIDEO_URL} controls playsInline preload="metadata" />
              ) : (
                <div className="flex aspect-video w-full items-center justify-center bg-[rgb(18,18,18)] text-[14px] text-white/50">
                  Demo video coming soon
                </div>
              )}
          </FadeIn>

          <FadeIn delay={0.75} className="mt-10 flex flex-col items-center gap-4">
            <div className="flex items-center gap-6">
              {APK_URL ? (
                <RollButton href={APK_URL} download label="Download for Quest" hoverLabel="Get the APK" />
              ) : (
                <span className="inline-flex h-[46px] cursor-not-allowed items-center bg-white/30 px-5 text-[14px] font-semibold text-black min-[810px]:h-[50px] min-[810px]:px-9">
                  Download soon
                </span>
              )}
              <RollButton href="/" variant="secondary" label="Back" hoverLabel="Home" />
            </div>
            <p className="text-[14px] leading-[22px] text-white/50">Meta Quest 3 and 3S. Install with SideQuest or adb.</p>
          </FadeIn>
        </section>
      </Shell>
    </main>
  );
}
