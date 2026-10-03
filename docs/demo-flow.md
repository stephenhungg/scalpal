# End-to-End Demo Flow

This is the proposed experience from startup through completion. Most components are not implemented yet; see [hardware baseline](hardware-baseline.md). The chosen exercise, recording route, robot model, and acceptance rule remain unresolved.

## Before the Learner Arrives

The operator launches the native app on Quest 3S and opens its laptop mirror. One permitted anatomy asset and authored exercise are loaded. The headset camera and microphone permissions are configured, the participant understands the setup and recording, and the service endpoints are reachable. The demo sponsor and Devnet funding status are visibly identified.

The physical participant reclines comfortably on the table. The virtual activity does not involve actual cutting or other procedures on them. Camera footage may capture people and surroundings; its intended storage, processing, and sharing must be established before collection.

## 1. Open Scalpal

The learner sees the real room through passthrough and an entry panel with Jarvis and the available challenge. The laptop mirror allows spectators to follow. Starting the app does not automatically start recording, enroll a contribution, or trigger a payment.

## 2. Describe a Learning Goal

The learner says, for example, "I want to practice something involving the abdomen." Jarvis asks an appropriate clarification, then suggests an exercise from the supported catalog. For the first demo, the catalog contains one complete module. Unsupported surgery requests receive an honest explanation and the supported alternative.

## 3. Explore the Selection Preview

A rotating 3D anatomy model appears in the selection space. The learner can ask where a structure is, what they will practice, or which surrounding structures matter. Jarvis can rotate or stop the model, isolate a named structure, and restore anatomical context.

The preview is specifically a selection aid. It explains the learning objective, the required actions, the contribution acceptance criteria, and the demo reward. It is not already fitted to the person.

## 4. Accept the Exercise and Challenge

"Let's do this one" confirms the supported exercise. The participant and learner confirm the recording arrangement. A wallet is paired through the laptop companion flow if a reward will be claimed. The challenge identity and version are fixed for this attempt.

The learning attempt, recording acceptance, and payout are separate outcomes. The interface should not suggest that a reward certifies medical skill.

## 5. Fit Anatomy to the Participant

The app estimates visible body landmarks and fits the generic anatomy overlay to the reclining participant. The learner or operator confirms the fit. If the selected markerless approach cannot produce a reliable result, the app stays in setup rather than showing fabricated precision.

The selected approach is pretrained visible landmarks plus custom torso registration. A participant-relative marker is a contingency requiring an explicit product decision; a table marker alone would not follow the person.

## 6. Practice With Guidance

Jarvis introduces each authored step. The learner uses simulated tools or makes the supported anatomical/procedural decisions. Allowed hints and feedback refer to known scene structures and actual exercise events.

Recording starts explicitly for the defined segment. The proposed robotics input is raw passthrough-camera video, with timestamps and the calibration/pose metadata needed for reconstruction where available. Relevant virtual tool and object state is recorded separately: those objects do not appear in raw camera footage.

When registration becomes uncertain, hide the misleading anatomy and pause scoring. When voice fails, preserve the attempt and provide a local pause or supported text/controller fallback. A pause is not an automatic success or a payout trigger.

## 7. Finish and Review

The learner sees completed steps, mistakes, hints, and an explanation of what to improve. A retry begins a new attempt. The app can show that a practice attempt completed while its recording is still processing or unsuitable for robot replay.

Any surgical content remains an illustrative prototype until appropriately reviewed. The final rubric must match the selected exercise rather than use generic "surgery skill" scoring.

## 8. Watch the Robot Replay

The first proposed processing path runs offline on the Mac. It decodes the clip, estimates hand landmarks, handles low-confidence segments, retargets the valid movement to the selected robot hand, and produces a replay artifact. The interface shows honest processing state or a useful failure.

The learner then watches the robot hand repeat the supported movement sequence in simulation, with a comparison to the source recording where appropriate. The exact display location is open: a headset panel and a laptop robot view are both candidates. The replay must be derived from the submitted clip rather than an unrelated canned animation.

Repeating a motion is not autonomous robot learning. If virtual object interactions are shown, reset the scene appropriately and check contacts separately; joint playback alone does not guarantee successful manipulation.

## 9. Accept the Contribution and Issue a Reward

The verifier evaluates the challenge's defined technical and task checks. Rejected or incomplete contributions receive an explanation and an opportunity to retry. A qualifying, nonduplicate contribution receives an accepted receipt and one authorized Devnet payout.

The learner sees separate statuses for practice, contribution, and payment. Pending or failed payment does not erase the session. A confirmed payout includes a transaction link. Video and detailed motion stay offchain; a digest or receipt is an identifier, not proof of data quality.

## 10. End the Session

The recap offers learning feedback, replay status, and the reward receipt. The learner can retry, choose another supported module when available, or exit. Recording stops, resources are released, and the participant is no longer part of an active capture session. Retention/deletion controls follow the chosen data arrangement.

## Judge-Facing Proof

Show one uninterrupted journey on hardware, with an actual recorded movement and its resulting robot replay. Show a real Devnet transaction and label it as test tokens. If processing is asynchronous, show it as such. Clearly distinguish the implemented demo from the future goal of training an autonomous policy.
