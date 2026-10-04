import { BlurWords } from "@/components/BlurWords";
import { wordCount } from "@/lib/text";
import { FadeIn } from "@/components/FadeIn";
import { AdamLayer } from "@/components/AdamLayer";
import { WaveBackground } from "@/components/WaveBackground";
import { RollButton } from "@/components/RollButton";
import { Shell } from "@/components/Shell";

const TITLE = "Scalpal.";
const LEDE = "Practice surgery in mixed reality with a voice coach, and turn every rep into motion a robot hand can replay.";

export default function Home() {
  return (
    <main className="h-dvh overflow-hidden">
      <Shell className="h-dvh">
        {/* One stacking context so the Adam layer can multiply onto the waves. */}
        <div className="absolute inset-0 z-0 bg-bg">
          <WaveBackground />
          <AdamLayer />
        </div>
        <section className="relative z-10 flex flex-col items-center px-[30px] pt-[148px] text-center min-[810px]:pt-[98px]">
          <h1 className="display">
            <BlurWords text={TITLE} />
          </h1>
          <p className="lede mt-4 max-w-[660px]">
            <BlurWords text={LEDE} start={wordCount(TITLE)} />
          </p>
          <FadeIn delay={0.595} className="mt-[26px] min-[810px]:mt-9">
            <RollButton href="/explore" label="Explore" hoverLabel="Watch the demo" />
          </FadeIn>
        </section>
      </Shell>
    </main>
  );
}
