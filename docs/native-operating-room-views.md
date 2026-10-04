# Native Operating-Room Views

These read-only views implement the display slice of [operation flow](operation-flow.md) on the shared `NativeSession` for both AR and VR. They use the existing case, anatomy and coach routes. They do not create a second scorer, restrict tools, advance steps or end a run.

## Scene Identity Pointer

`NativeScenePointer` uses the tracked controller aim pose, transformed once through the XR origin. It displays the existing tool/organ identity and a box from current renderer bounds. Targets must belong to this session, have visible current geometry and pass registration gates. Foreign foreground geometry occludes the ray; ambiguous ray-buffer overflow fails closed. Held tools and tiny tip bubbles are ignored as foreground blockers. This is scene-graph pointing, not camera detection or identification of a volunteer's hidden organs.

The actual nine imported appendectomy meshes and a shared scalpel passed 48,591 component assertions covering rotated/scaled bounds, source vertices, deformation, identity ownership, occlusion, tracking/focus loss and readable labels. The count includes per-vertex assertions, not distinct clinical scenarios.

## Free-Form Checklist

`NativeProcedureChecklist` reads authored milestone order/titles and the shared exercise runner's accepted milestone history and current step. The coach service's latest checklist semantics exclude the current step from completed check marks, even if it was achieved earlier: a new bleed can reopen a hemostasis goal. The component passed 130 assertions using the actual open-body reducer, an accepted off-order closing action, rebleeding, retry and both presentation gates. History is preserved by the engine, while current guidance and grading continue to read body state. The view never fabricates completion from a requested action or enables/disables instruments. Tracking, fit, session and score readiness control visibility.

## Simulated Patient Monitor

`NativePatientMonitor` consumes `CoachRelay.AlertSnapshot.condition` from the existing poll. The additive `CoachPatientCondition` DTO mirrors Matthew's service serializer; it does not implement a competing physiology model. Display includes HR, BP, RR, known/unknown SpO2, raw blood loss, demo acceleration, hemorrhage class, baseline source and server outcome. Values are explicitly simulated, never represented as the volunteer's response to virtual surgery.

Every sample must match session, patient, procedure, case and presentation mode with a nonregressing version. Receipt freshness uses local monotonic time because the service supplies no generated-at timestamp: a newly published poll can refresh an unchanged version, but rereading the same object cannot. Three seconds without a fresh sample clears numbers. Tracking/focus, anatomy/AR fit and coach synchronization loss invalidate readings; resume needs a new poll. Early `ended` and explicit `died` outcomes are displayed separately. A flatline requires explicit validated server death, not hemorrhage class alone. The view never calls the Presage baseline/camera/provider route.

The monitor passed 59 Editor assertions against exact serialized synthetic service fixtures, stale/mismatched/malformed samples, actual input-gate methods and Brand text floors. This does not establish live receipt delivery of death: the current relay fails synchronization on an unrecognized `patient_died` event receipt, and the terminal failure-to-recap authority remains unresolved.

## Packaging and Remaining Routes

The scene and reconstructing `NativeSessionBuild.Prepare` bind all three views. Build validation requires them and runs component checks. Explore/office run independently and retain their owners. Controller-pose capture/robot replay, Unity coarse region injuries, the surgery flythrough, real provider voice, participant fit and physical performance are separate acceptance gaps. The old passthrough recorder still needs replacement by the replay owner; this display milestone does not claim that route is upgraded.
