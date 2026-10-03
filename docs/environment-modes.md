# Mixed Reality and Full VR Modes

Updated October 3, 2026 from the user's explicit direction: support a real reclining person with generic virtual organs and a separate entirely virtual surgery-themed demo. These share one exercise, tool behavior, voice coach and state flow. The two modes are a product direction; only static VR scene art and the earlier instrument sandbox are prepared on this branch.

## Shared Core

Use one registration/presentation boundary, with a common patient/anatomy root, organ IDs, tool IDs, authored exercise rules, Jarvis action validation and attempt state. A mode chooses the source of the patient transform and background; it does not create another voice agent or duplicate the exercise engine.

| Concern | Mixed reality | Full VR |
| --- | --- | --- |
| Background | Live headset passthrough | Authored operating-room geometry |
| Visible patient | Real reclining participant | Static virtual mannequin |
| Table | Accepted real surface estimate | Authored virtual table |
| Generic anatomy transform | Estimated, validated and explicitly confirmed body fit | Authored and validated virtual-patient fit |
| Perception dependency | Surface association and person/body registration | No CV needed to find known virtual scene objects |
| Exercise effects | Virtual organs/tools only | Same virtual organs/tools |
| Failure | Uncertain registration hides misleading anatomy and blocks effects/assessment | Invalid scene bindings or XR tracking block effects/assessment |

Surface video cannot reveal someone's actual organ geometry. Generic internal anatomy must be fitted as a teaching model; a detected table or person rectangle does not establish that fit.

## Real Table and Real Person

1. Initialize the native headset rig, passthrough and required permissions. Use the previously tested camera path as infrastructure evidence, not proof that torso registration works.
2. Propose a horizontal table surface using Meta MRUK scene anchors when room setup exists, or live environment raycasting when supported. Show the proposed extent and ask the operator to accept the actual table. A depth hit is geometry; it does not automatically identify a table or person. [Meta MRUK](https://developers.meta.com/vr/documentation/unity/unity-mr-utility-kit-overview/), [environment raycasting](https://developers.meta.com/vr/documentation/unity/unity-mr-utility-kit-environment-raycast/).
3. Find visible body landmarks/segmentation in passthrough with a pretrained pose model as a candidate, then test it on the actual reclining viewpoint. MediaPipe supplies image landmarks and estimated body-relative 3D coordinates; these must not be treated as Quest world-space anchors. [Pose Landmarker](https://developers.google.com/edge/mediapipe/solutions/vision/pose_landmarker), [coordinate output](https://developers.google.com/edge/mediapipe/solutions/vision/pose_landmarker/python#handle_and_display_results).
4. Associate image observations with correctly calibrated rays, defensible surface/depth observations and the acquisition-time headset pose. A table point underneath a person is not their shoulder or torso surface. If valid depth association is absent, keep the fit uncertain; do not manufacture metric body position from a 2D box.
5. Fit the selected generic anatomy frame to visible landmarks, inspect external alignment in both eyes, then confirm it. For the first controlled demo, an explicit operator-assisted fit is a proposed fallback if automatic pose detection fails; it must be labeled and measured. A fixed world anchor does not follow a moving participant.
6. Monitor pose age, confidence, occlusion, head tracking and participant movement. Invalidity pauses assessment and virtual effects. Recovery requires checking/confirming the fit; smoothing must not hide a stale observation.

A translucent body outline and focused anatomy window can show correspondence without covering the whole real person. Ordinary depth occlusion could hide the intentionally visible internal-organ overlay, so that presentation needs an explicit design rather than blindly applying occlusion to every object. No automatic table/body CV or this registration pipeline is implemented by the static environment import.

## Full VR

The prepared CC0 room and mannequin are available in `Assets/Scalpal/Environment/Samples/OperatingRoomPreview.unity`. The source model is approximate, static scene art. `PatientRoot` and `AnatomyRoot_Unbound` make the missing organ binding visible; no organs or surgery logic are secretly attached.

Next integrate the real XR rig, a selected anatomy assembly and the existing tool prefabs. Validate that the anatomy source frame fits the mannequin/table; Matthew's Z-Anatomy layers and separate HRA detail models are not automatically interchangeable. Existing authored seam cutting remains a practice-patch effect rather than arbitrary organ slicing.

At a mode transition, release held tools, invalidate the previous patient fit and rebind the same exercise to the new patient root before accepting another action. Record the presentation mode in session context for the companion and Jarvis; field names and migration are proposed until agreed with Nathan and Matthew.

## Recording and Robotics

Raw passthrough contains the physical room, hands and participant, not virtual organs or tools. Full-VR rendered footage contains the virtual room but is not raw camera footage. Keep source labels, clock/pose association and the actual applied scene state explicit. The user's robotics input remains passthrough video; choosing a VR presentation mode does not make rendered frames an equivalent observed input or prove a video-to-robot result. The fallback VR demo can show the learning interaction independently until a permitted recording/replay path is verified.

## Next Bounded Checks

First make the existing tools usable in the native XR rig inside the virtual room. Separately validate an accepted real table surface and visible body-fit markers on the actual reclining participant. Only connect the two-mode selector after both use the same registration/action validity gate. Then rehearse the same authored exercise with Jarvis in each mode. Headset frame time, body alignment error, stereo rendering and registration recovery remain unmeasured for this new scene.
