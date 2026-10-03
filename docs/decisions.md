# Decisions, Changes, and Open Questions

Updated October 3, 2026. "Current direction" means a choice stated or accepted during product exploration. It does not mean implemented, clinically reviewed, or fully approved as an engineering specification.

**Latest instruction:** Solana and monetary completion rewards remain removed. Nathan is assigned the companion website + SpacetimeDB/routing lane; Matthew continues his Jarvis work. See [current direction](current-direction.md) and [Nathan's work order](nathan-plan.md), which supersede older proposals and the prior blanket wait for this lane.

## Current Direction

| Topic | Current direction | Status / implication |
| --- | --- | --- |
| Name | `scalpal` | User-requested repository name; preserve this spelling |
| Platform | Meta Quest 3S, native Unity client | Native camera/immersive sample baseline demonstrated; application not built |
| Thesis | Human learning plus useful robot demonstrations, with later robot learning | Preserve both goals rather than forcing an education-versus-robotics choice |
| Participant | A real reclining person | Supersedes fixed prop/mannequin premise; overlay is generic anatomy |
| Selection | Voice conversation with Jarvis | Supersedes Tinder-style surgery swiping |
| Selection visuals | Rotating 3D Blender anatomy, controlled by supported voice actions | Explicitly during selection, before participant fitting |
| Body registration | Pretrained visible body landmarks plus custom torso alignment | MediaPipe is a candidate, not verified on our reclining viewpoint |
| Tools | Simulated tools and authored exercise rules | No actual operation on the participant |
| Robotics input | Recorded passthrough video | Latest explicit correction; do not default back to SDK hand-joint recording |
| First robotics output | Estimated motion retargeted to a simulated robot hand and replayed | No custom neural model needed as initial approach; no policy learned yet |
| Hand type | An articulated robot hand is the requested concept | Wrist/pinch-to-gripper would be a scope simplification requiring agreement |
| Robotics processing | Proposed first experiment runs offline on the Mac | Not a commitment to live low-latency teleoperation |
| Payments / onchain | Removed from project scope | No Solana, wallets, payouts, or money for completing simulated surgeries |
| Data backend | SpacetimeDB for shared state; private file/object storage for video and replay | Part of [Nathan's assigned implementation lane](nathan-plan.md); not configured or deployed yet |
| First slice | Appendectomy first (`lap_appendectomy`), then gallbladder and sigmoid colectomy | Anatomy interaction targets and code wiring are implemented; physical validation, robot model, and shared manipulation remain open. See [anatomy integration](anatomy-integration.md) |

## Important Corrections to Earlier Ideas

The first exploration discussed a training prop, swipe selection, a minimal completion reward, and anatomy/procedural cognition without a robot component. The user subsequently required a real person, conversational selection with a 3D preview, a dual education-and-robotics thesis, more visible onchain sponsorship, and video-derived hand retargeting.

A prior surgery-training design was independently reviewed twice and remained DRAFT awaiting document approval. It predates the expanded robotics/challenge scope. Its review cannot be cited as approval of this current architecture. The accepted experience flow and the later directions are preserved here without representing the expanded specification as approved.

The original device setup logs also include earlier failures and pending installation states. Later hardware validation superseded those states. [Hardware baseline](hardware-baseline.md) records the final observed state without repeating obsolete blockers.

## Decisions Needed Before Broad Implementation

1. **Robot demonstration segment:** Appendectomy is the selected educational demo with authored interaction checks. Which supported movement from it supplies a feasible demonstration for the selected robot?
2. **Robot model and simulator:** Which articulated hand, optionally attached to an arm, and which simulation environment? What movement will we reproduce first? A render-only animated mesh and a physics simulation are different deliverables.
3. **Capture and intended use:** Which raw camera recording route, metadata, participant disclosures, storage, retention, and platform permissions support the exact in-app replay, export, sharing, or training use? The video correction changes the relevant SDK restriction but does not establish lab dataset rights.
4. **Registration feasibility:** Can the selected pretrained body model detect reliable landmarks on the actual reclining person, with valid depth at the intended viewpoint? If not, which explicit tracking fallback is acceptable?
5. **Content and assets:** Who authors and reviews the exercise? Which anatomy model is licensed for the intended use and performs well enough on Quest?
6. **Contribution acceptance:** What checks make a clip useful? What happens when learning completes but reconstruction fails? Who operates the demo verifier?
7. **Nathan's implementation choices:** The companion + SpacetimeDB lane is assigned. Choose module/client stack, tested composited-video transport, endpoint/storage arrangement, and worker/replay format with the respective owners. Removed payments are not an open implementation question.
8. **Team and logistics:** See the updated [team plan](team-plan.md). Internal milestone times, development machines/accounts/endpoints, and remaining build budget still need coordination; Nathan's GitHub identity is not assumed. The working event submission deadline is noon Eastern on October 4; see [event constraints](sponsors.md#event-constraints-for-the-team) for the published deadline discrepancy.

## Claims Requiring Evidence

- Body-relative anatomy alignment and recovery on a reclining person.
- Hand reconstruction from our actual first-person footage, especially occlusion and wrist translation.
- Task-feasible retargeting to a particular robot, not simply plausible hand animation.
- Educational improvement from the guided exercise.
- Autonomous policy improvement from accepted demonstrations.
- Sponsor demand and legitimate training/export rights.

Treat these as hypotheses until measured. Do not solve an unknown coordinate/depth issue by deciding that a custom model is necessary without a reproducible detection failure.
