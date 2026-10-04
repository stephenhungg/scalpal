# MHacks Sponsor Alignment and Submission Context

> Scope update: Solana, wallets, payouts, and monetary completion rewards are removed. Nathan owns the companion website + SpacetimeDB/routing lane; Matthew continues Scalpal. This earlier proposal contains superseded reward/challenge references. Read [current direction](current-direction.md) and [Nathan's implementation plan](nathan-plan.md) first.

Snapshot: October 3, 2026. Event categories below were read from the live [MHacks 2026 Devpost](https://mhacks-2026.devpost.com/) and official linked resources. Availability and requirements can change; verify before submitting.

This page separates **listed hackathon awards**, **proposed product integrations**, and **future sponsors who might fund challenges**. A listed prize does not mean a sponsor has funded Scalpal or agreed to purchase recordings.

## Recommended Alignment

The primary product story is AI-guided mixed-reality learning with video-derived robot-hand replay and a sponsored contribution reward. **Actually Intelligent (AI)** is the recommended main track. **Beyond the Code (Hardware)** is an alternative if the final implementation emphasizes headset CV and robotics. Choose one main track; do not claim both under the current rules.

Use technologies because they support that flow. ElevenLabs, Gemini API, and Solana have strong potential roles. Tiger Data is optional if timestamped recording/replay storage becomes useful. Avoid adding several competing databases or unrelated communication products just for entry count.

| Sponsor/category | Confirmed listed prize | Proposed Scalpal integration | What would demonstrate meaningful use |
|---|---|---|---|
| ElevenLabs sponsor award | Three months of Scale per team member | Scalpal spoken conversation and app actions | Working voice selection, guidance, and an actual scene action |
| MLH Best Use of ElevenLabs | Wireless earbuds | Same voice integration | Demonstrate the implemented experience; do not confuse this with the subscription award |
| MLH Best Use of Gemini API | Swag kits | State-grounded coach reasoning and tool selection | Actual Gemini API calls grounded in supported exercise state and reviewed content |
| MLH Best Use of Solana | SenseCAP Card Tracker | Funded challenge, contribution acceptance receipt, unique Devnet payout | Show onchain state and a confirmed authorized reward transaction |
| MLH Best Use of Presage | Fitbit Inspire + Presage perks | Real volunteer breathing/pulse from a mounted phone: breathing-synced AR overlay, physiology-synced demonstration data, volunteer comfort guard ([proposal](presage/README.md), parked) | Live Presage readings above confidence on a real volunteer, overlay motion and logged samples on the session clock |
| MLH Best Use of Tiger Data | Stream Deck Mini | Store timestamped permitted recordings/robot commands and retrieve them for replay | Write a real session, retrieve it, and use it to reconstruct the replay |
| Neon | $1,000 / $500 / $100 in AI Gateway credits | Alternative persistent backend for sessions, rubric versions, and reward claims | A functional backend making substantial use of Neon tooling |
| Spacetime | $1,000 / $500 / $200 | Shared live state between headset, companion view, and robot view | Meaningful use as the core real-time backend |
| Meta | No dedicated Meta prize appeared in the refreshed Devpost list | Quest 3S camera, stereo rendering, and mixed-reality runtime | Essential hardware/platform integration; do not advertise an unlisted Meta award |

Tiger Data stores/retrieves motion data; it does not estimate hands, retarget robot joints, or train a policy. Its [documentation](https://www.tigerdata.com/docs) and [hypertable guide](https://www.tigerdata.com/docs/learn/hypertables/understand-hypertables) describe time-series storage. Data permission and meaningful integration matter even if no analytics dashboard is built.

Primary resources: [Current Devpost prize list](https://mhacks-2026.devpost.com/), [official Tracks & Prizes](https://safe-banon-80d.notion.site/Tracks-Prizes-3ed24ca0c81b80579aeff03edfa88af5), [MLH ElevenLabs resources](https://www.mlh.com/partners/elevenlabs), and [MLH Solana resources](https://www.mlh.com/partners/solana). Prize amounts labeled as credits are not cash. Multiple eligible entries do not guarantee multiple wins.

## Conditional Integrations That Add Scope

| Sponsor | Required connection to the sponsor product | Why the current demo is not automatically eligible |
|---|---|---|
| Fetch AI / ASI:One | Mandatory ACP interoperability, Agentverse registration, ASI:One discoverability, and completion of the primary workflow entirely inside ASI:One; public runnable repo and 3–5-minute video | A headset-first exercise with a generic voice agent does not satisfy the ASI:One workflow requirement |
| FinchNode | Working API integration with synthetic demo health records that helps patients, clinicians, or care teams | A surgical theme or generic anatomy model alone is insufficient |
| Relay | A working agent inside the Relay app using text, calls, or video | Scalpal running only in Quest is not a Relay integration |
| Photon | Spectrum integration that connects an agent to iMessage | A Quest voice interface is not an iMessage agent |
| FREE-WILi | Actual use of its hardware | The Quest alone is a different hardware product |
| Notability | Use Notability Pro during the hackathon, identify it in tools, explain usage, and include at least two screenshots | Merely mentioning the brand or producing Markdown notes does not establish eligibility |
| SpaceXAI / Make it Legendary | Build with Cursor and include Grok Imagine or Voice API; the detailed brief emphasizes real space data | This does not naturally fit the current surgical-learning workflow |
| Capital One / Nessie | Creative use of mock banking APIs | Solana payout alone is not Nessie integration |

Fetch lists $1,250 / $750 / $500 cash prizes; its [official MHacks hackpack](https://www.fetch.ai/events/hackathons/mhacks-2026/hackpack) is the direct source for requirements. Its [submission guide](https://docs.google.com/document/d/1UDW-X1C24hxZviFOQzjTeh0pXRNAoflMb8lhJqZP9Z0) describes team registration and the separate ASI submission. The team lead receives a Team ID, and all teammates must join before that submission is complete.

Other conditional award details are in the [official Tracks & Prizes page](https://safe-banon-80d.notion.site/Tracks-Prizes-3ed24ca0c81b80579aeff03edfa88af5). The Figma Best Design award is listed, but detailed qualification requirements were not established in the reading pass. These are optional opportunities, not implementation commitments.

## Challenge Sponsors Are a Separate Hypothesis

Scalpal's proposed economic loop is:

```text
Sponsor defines a useful task and acceptance requirements
  -> funds a challenge
  -> learners practice with AI guidance
  -> verifier accepts permitted, useful demonstrations
  -> contributors receive an authorized reward
  -> later robot-learning results inform future tasks
```

No AI lab, robotics lab, hospital, or educator has committed to funding this loop. For MHacks, label the sponsor as a demo entity unless an actual agreement exists. Do not present event sponsor logos as evidence of a commercial partnership or a buyer for captured data.

The sponsor must want the exact artifact we can supply. Novice anatomy answers, monocular video, reconstructed hand motion, applied robot commands, and quality-reviewed demonstrations are different deliverables. Replay shows motion transfer; it does not establish training value. Funding and payout do not make a contribution clinically correct.

See [research and data-use constraints](research/README.md) before proposing video export, dataset licensing, or third-party robot training. Keep camera footage and detailed movements offchain.

## Event Constraints for the Team

| Item | Current reading |
|---|---|
| Event | October 3–4, 2026, University of Michigan, Ann Arbor |
| Hacking window | October 3 noon through October 4 noon, Eastern |
| Team | One to four registered participants; add all teammates to Devpost |
| Main prizes | Grand prize $5,000 cash; Sustainability, Actually Intelligent, FinTech, and Beyond the Code each $2,500 |
| Entry selection | One main MHacks track plus multiple eligible sponsor entries |
| Project originality | Core must be new and built during the official hacking period; existing frameworks are allowed, an old project is not |
| Submission | Code access, description, table number, and requested materials; one project per participant |
| Pitch | Prepare a three-minute working demo and be present for judging |

The [Devpost rules](https://mhacks-2026.devpost.com/rules), [handbook](https://safe-banon-80d.notion.site/2026-Hacker-Handbook-3ca24ca0c81b80fb8adee2e26c8508af), and [live schedule](https://www.mhacks.org/live) say **noon October 4** for submission. The Devpost header/dates page displayed **12:15 PM EDT**. Work to the earlier noon deadline rather than relying on the discrepancy. Judging begins at 12:30 PM; the handbook and live schedule disagree on its end time.

The handbook names innovation, technical complexity, usability, and presentation quality; Devpost lists theme adherence as a criterion. No confirmed weighted rubric was found. Explain why the project fits the selected theme and demonstrate the complete working path.

## Submission Evidence to Collect

- A concise thesis separating the guided-learning demo, robot replay, and later robot-learning hypothesis.
- A complete working sequence for one supported exercise, with honest labels for unimplemented or offline steps.
- Evidence of each sponsor integration actually used, including the voice action, API-backed reasoning, recording retrieval, or Devnet transaction as applicable.
- A public repo with setup instructions and attribution, excluding secrets and participant footage.
- An explicit Devnet label and confirmed receipt; test SOL is not a real monetary payout.
- Required separate sponsor materials only for categories actually pursued.

These are preparation notes. No sponsor submission, benefit redemption, payment, or outreach has been performed as part of this research.
