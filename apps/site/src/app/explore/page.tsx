import type { Metadata } from "next";
import { BlurWords } from "@/components/BlurWords";
import { wordCount } from "@/lib/text";
import { FadeIn } from "@/components/FadeIn";
import { RollButton } from "@/components/RollButton";
import { Shell } from "@/components/Shell";
import { APK_URL, DEMO_VIDEO_URL } from "@/lib/site";

export const metadata: Metadata = { title: "Scalpal · Demo" };

const TITLE = "See it work.";
const LEDE = "One rep, from the first cut to the robot replay.";

export default function Explore() {
  return (
    <main>
      <Shell>
        <section className="relative z-10 flex flex-col items-center px-[15px] pb-16 pt-8 text-center min-[810px]:px-[30px] min-[810px]:pt-12">
          <h1 className="display !text-[clamp(44px,5vw,72px)]">
            <BlurWords text={TITLE} />
          </h1>
          <p className="lede mt-3 max-w-[560px]">
            <BlurWords text={LEDE} start={wordCount(TITLE)} />
          </p>

          <FadeIn delay={0.55} className="mt-8 w-full max-w-[800px]">
            <Framed>
              {DEMO_VIDEO_URL ? (
                <video className="block aspect-video w-full bg-black" src={DEMO_VIDEO_URL} controls playsInline preload="metadata" />
              ) : (
                <div className="flex aspect-video w-full items-center justify-center bg-[rgb(236,236,236)] text-[14px] text-black/50">
                  Demo video coming soon
                </div>
              )}
            </Framed>
          </FadeIn>

          <FadeIn delay={0.75} className="mt-10 flex flex-col items-center gap-4">
            <div className="flex items-center gap-6">
              {APK_URL ? (
                <RollButton href={APK_URL} download label="Download for Quest" hoverLabel="Get the APK" />
              ) : (
                <span className="inline-flex h-[46px] cursor-not-allowed items-center bg-black/40 px-5 text-[14px] font-semibold text-white min-[810px]:h-[50px] min-[810px]:px-9">
                  Download soon
                </span>
              )}
              <RollButton href="/" variant="secondary" label="Back" hoverLabel="Home" />
            </div>
            <p className="text-[14px] leading-[22px] text-black/50">Meta Quest 3 and 3S. Install with SideQuest or adb.</p>
          </FadeIn>
        </section>
      </Shell>
    </main>
  );
}

// 1px rule with registration crosses on the corners, same language as the secondary button.
function Framed({ children }: { children: React.ReactNode }) {
  const cross = (pos: string) => (
    <span aria-hidden className={`pointer-events-none absolute h-[17px] w-[17px] ${pos}`}>
      <span className="absolute left-1/2 top-0 h-full w-px -translate-x-1/2 bg-[var(--cross)]" />
      <span className="absolute left-0 top-1/2 h-px w-full -translate-y-1/2 bg-[var(--cross)]" />
    </span>
  );
  return (
    <div className="relative border border-rule p-2">
      {cross("-left-[9px] -top-[9px]")}
      {cross("-right-[9px] -top-[9px]")}
      {cross("-left-[9px] -bottom-[9px]")}
      {cross("-right-[9px] -bottom-[9px]")}
      {children}
    </div>
  );
}
