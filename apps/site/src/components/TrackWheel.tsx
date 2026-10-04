"use client";

// Infinite marquee of the MHacks tracks / sponsor integrations Scalpal is built on. Logos are
// rendered as white silhouettes so the row matches the page; the band is centered and fades
// out at both ends instead of running edge to edge. Pauses on hover.
const TRACKS = [
  { name: "MHacks · Actually Intelligent", logo: "/tracks/mhacks.png" },
  { name: "ElevenLabs", logo: "/tracks/elevenlabs.svg" },
  { name: "SpacetimeDB", logo: "/tracks/spacetimedb.svg" },
  { name: "FinchNode · HealthTech", logo: "/tracks/finchnode.svg" },
];

function Row({ hidden }: { hidden?: boolean }) {
  return (
    <ul aria-hidden={hidden} className="flex shrink-0 items-center gap-14 pr-14">
      {TRACKS.map((t) => (
        <li key={t.name} className="flex items-center gap-3 whitespace-nowrap text-[13px] text-white/60">
          {/* eslint-disable-next-line @next/next/no-img-element */}
          <img src={t.logo} alt="" className="h-5 w-auto opacity-80 [filter:brightness(0)_invert(1)]" />
          {t.name}
        </li>
      ))}
    </ul>
  );
}

export function TrackWheel({ className = "" }: { className?: string }) {
  return (
    <div
      className={`group mx-auto w-full max-w-[720px] overflow-hidden ${className}`}
      style={{
        maskImage: "linear-gradient(90deg, transparent, black 18%, black 82%, transparent)",
        WebkitMaskImage: "linear-gradient(90deg, transparent, black 18%, black 82%, transparent)",
      }}
    >
      <p className="sr-only">Built for: {TRACKS.map((t) => t.name).join(", ")}</p>
      {/* two copies side by side; sliding by one copy's width loops seamlessly */}
      <div className="flex w-max animate-[track-wheel_26s_linear_infinite] group-hover:[animation-play-state:paused]">
        <Row />
        <Row hidden />
      </div>
    </div>
  );
}
