// Footer links on the landing. Devpost points at the hackathon until the project page exists.
export const LINKS = [
  { label: "GitHub", href: "https://github.com/stephenhungg/scalpal" },
  { label: "Devpost", href: process.env.NEXT_PUBLIC_DEVPOST_URL ?? "https://mhacks-2026.devpost.com/" },
  { label: "Dashboard", href: "https://dashboard.scalpal.tech" },
  { label: "Docs", href: "https://docs.scalpal.tech" },
];

// Copied from the explore page: hands the case-authoring skill to any coding agent.
export const AGENT_PROMPT = `Set up the Scalpal case-authoring skill and use it to build a new patient case.

1. Clone https://github.com/stephenhungg/scalpal if you don't have it, and work inside it.
2. Read .claude/skills/authoring-scalpal-cases/SKILL.md and follow it exactly, reading its references when it points to them. (Claude Code loads it automatically from .claude/skills.)
3. Run its tool from services/preop after npm ci: npx tsx ../../.claude/skills/authoring-scalpal-cases/scripts/scalpal-case.mts list
4. Show me the patients, then ask which one and what story I want before writing anything.

Docs: https://docs.scalpal.tech/cases/overview`;
