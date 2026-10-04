// Footer links on the landing. Devpost points at the hackathon until the project page exists.
export const LINKS = [
  { label: "GitHub", href: "https://github.com/stephenhungg/scalpal" },
  { label: "Devpost", href: process.env.NEXT_PUBLIC_DEVPOST_URL ?? "https://mhacks-2026.devpost.com/" },
  { label: "Companion", href: "https://scalpal-companion.vercel.app" },
  { label: "Docs", href: "https://docs.scalpal.tech" },
];
