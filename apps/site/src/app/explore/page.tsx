import type { Metadata } from "next";
import { ExploreBody } from "@/components/ExploreBody";
import { APK_URL, DEMO_VIDEO_URL, youtubeEmbed } from "@/lib/site";

export const metadata: Metadata = { title: "Scalpal · Demo" };

export default function Explore() {
  // Asset URLs resolve on the server (env or files in /public); the page itself is a client component.
  return <ExploreBody apkUrl={APK_URL} videoUrl={DEMO_VIDEO_URL} embedUrl={youtubeEmbed(DEMO_VIDEO_URL)} />;
}
