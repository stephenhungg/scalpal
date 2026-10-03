# Unity Instrument Runtime

This package is a functional virtual-tool prototype for Unity 6000.0.66f2. It is separate from Matthew's feature branch and does not change his exercise state machine or Jarvis service. All fifteen prefab IDs match Matthew's current catalog, including `scalpel`, added upstream at `a629fdf`.

## Build and Open

Open the committed [Unity project](../apps/quest/README.md) at `apps/quest` in Unity 6000.0.66f2, then open `Assets/Scalpal/Instruments/Samples/InstrumentSandbox.unity`. Its package manifest, lock, project settings and enabled sandbox build scene are committed. The project remains a standalone editor workbench: it does not configure a native XR loader, Android deployment, passthrough permission or headset rig. Alternatively, import the complete [Instruments](../apps/quest/Assets/Scalpal/Instruments/) folder into an existing native Quest project's `Assets/Scalpal/` directory, preserving `.meta` files.

In Unity, use **Scalpal → Instruments → Build Prefabs and Sandbox**. This reads fifteen `Models/inst_<id>.fbx` files, creates PBR materials and pickup prefabs under `Prefabs/`, and creates `Samples/InstrumentSandbox.unity`. Existing open scenes are restored after generation; the menu asks to save modified scenes first. This generator intentionally rebuilds its outputs, so authored customization should be made in separate prefab variants.

Each exported model must expose `GripAnchor`, `Tip`, optional `ActionPoint`, and optional `JawUpper`/`JawLower` objects. The generator rejects missing anchors or implausible grip-to-tip distances. It normalizes the imported grip-to-tip direction to local +Z and puts the grip at the prefab origin. All dimensions are in meters. Runtime jaw rotation is around each jaw's local +X axis, relative to its imported closed pose. Trigger parts articulate with activation; the retrieval bag switches between folded and deployed visual states, the circular stapler anvil closes axially, and the trocar obturator cap withdraws visually.

The generator adds a Rigidbody, compound box/capsule colliders, and a small distal trigger on the child named `Tip`, matching Matthew's latest asset manifest. If the project already defines an `Instrument` physics layer, it assigns that layer; it does not modify global project layers. These are simplified pickup/collision volumes, not a faithful surgical contact model. The Blender geometry remains the visual model. Render materials use the installed URP Lit shader, or Standard as a fallback. Imported diffuse colors are preserved; the original named material recipes supply metallic and smoothness values lost in FBX conversion. The membrane and optical glass are opaque stylized surfaces to keep transparency out of the Quest workbench.

## Controller Pickup

[XRInstrumentInput.cs](../apps/quest/Assets/Scalpal/Instruments/Runtime/XRInstrumentInput.cs) reads Unity's `InputDevices.GetDeviceAtXRNode`, `CommonUsages.devicePosition`, `deviceRotation`, `isTracked`, `grip`, and `trigger`. It does not depend on Meta-specific C# classes or XR Interaction Toolkit.

For each controller, attach `InstrumentInteractor` and `XRInstrumentInput` to a dedicated GameObject. Select `LeftHand` or `RightHand`, and assign the **actual tracking-origin transform** from the application's XR rig. The sandbox's `TrackingOrigin` is an integration placeholder; it does not start XR or track its desktop preview camera. Replace the sandbox camera with the configured headset rig when moving the workbench into the Quest application.

- Grip crossing 0.65 picks up the nearest unheld instrument within 14 cm of the controller's grip point.
- Grip below 0.25 releases it and restores its earlier physics settings.
- Trigger above 0.7 activates a held tool. Below 0.2 releases its target and rearms discrete actions.
- A held instrument follows the controller at its `GripAnchor`; its Rigidbody becomes kinematic while held.
- Losing valid controller tracking immediately releases the instrument, ends its action, and releases any grasped virtual target. Recovery requires a physical grip release before another pickup.

Hand tracking, two-hand grips, controller haptics, throw velocity, tool-change UI, and laparoscopic port constraints are not implemented. These are explicit extension points rather than claimed capabilities.

Existing rig adapters can instead call `SetTrackedPose`, `SetGrip`, and `SetActivation` on `InstrumentInteractor`. The same contact rules apply to either input route.

## What Each Tool Actually Does

| Tools | Implemented prototype effect |
| --- | --- |
| Scalpel, laparoscopic scissors | Trace the authored seam on a virtual training patch; sufficient coverage separates two existing visual halves |
| Atraumatic grasper, Maryland dissector | Attach an allowed virtual target at the tip while activated; restore its prior parent/physics on release |
| Monopolar hook, vessel sealer | Hold activation near an allowed target for 0.75 seconds to apply a visual simulated-seal state |
| Clip applier | Place one visible clip marker per trigger cycle, up to six |
| Endoscopic linear and circular staplers | Place a visible row of markers per trigger cycle, up to three rows |
| Suction irrigator | Decrease the demo fluid volume while activated; irrigation delivery is not implemented |
| Retrieval bag | Attach an explicitly retrieval-enabled virtual target; bag deformation and specimen containment are not simulated |
| 5 mm and 12 mm trocars | Place an explicit access marker on an allowed target; insertion mechanics are not simulated |
| Fascial closure device | Restore a previously separated seam's visual halves; suture threading/mechanics are not simulated |
| 30-degree laparoscope | While held/activated, a 30-degree virtual-scene camera supplies a 512² RenderTexture at 15 FPS to an assigned monitor; this is not a physical endoscope feed |

The reusable `TrainingPatch.prefab` starts with **invalid registration**. The application's registration owner must explicitly call `TrainingTarget.SetRegistrationValid(true)` before any effects are allowed. The sandbox enables this on its synthetic patch so the workbench can be exercised without a participant. Each target also has an authored `allowedActions` mask: touching unrelated anatomy does not automatically cut, seal, clip, or collect it.

The patch is an authored state demonstration. Cutting follows its narrow local-X seam, requires short contiguous contact movements, and opens two existing pieces; it is not arbitrary mesh slicing, soft-body mechanics, cautery physics, bleeding simulation, or a model of an actual procedure. No action changes the real person or raw passthrough image. The optional `LaparoscopeView` uses authored field-of-view/clip settings for a virtual scene preview; its rendering cost and visual output still require graphics/headset validation. The URP path uses the ordinary [camera-to-RenderTexture route documented by Unity](https://docs.unity3d.com/6000.0/Documentation/Manual/urp/rendering-to-a-render-texture.html).

## Integration Boundary

Subscribe to `InstrumentBehaviour.ActionApplied` for a structured `InstrumentActionRecord` containing:

- `instrumentId`, `targetId`, `action`, and the actual applied `outcome`;
- the Unity-world interaction point in meters;
- `unityMonotonicSeconds`, from `Time.realtimeSinceStartupAsDouble`.

These events report a simulated scene effect, not an automatically completed surgery step or score. The exercise owner must validate instrument/target/step and current attempt before feeding an accepted event to Matthew's runner, Jarvis context, or Nathan's shared session state. Session/attempt/exercise versions are deliberately not guessed in this standalone package.

The timestamp uses the Unity monotonic clock. It is not a camera acquisition timestamp or synchronized video time; capture integration must record the clock relationship and uncertainty separately.

`InstrumentTipContact` supplies a separate distal-touch callback with real instrument/anatomy IDs. Set its `RegistrationIsValid` function from the registration owner and subscribe `TouchApplied` only through the current attempt/step dispatcher. It defaults to blocked, requires held/tracked/activated input, and emits once per anatomy structure per deliberate activation cycle, across multiple colliders and exit/reentry. It does not call Matthew's state machine automatically. Matthew's latest feature branch adds an alternative `InstrumentTip` adapter with raw-contact focus and held/tracked/activated scoring. Use either that scoring adapter or an accepted `InstrumentTipContact.TouchApplied` dispatcher, never both for the same contact; otherwise actions can be counted twice. Neither path replaces the registration/current-attempt gate.

Virtual instruments are already named scene objects. For them, use their IDs and renderer geometry rather than requiring a CV model to rediscover them. **Raw passthrough video does not contain virtual instruments.** A rendered spectator/composited view can show them. `InstrumentProjection` can project renderer bounding-box corners through an explicitly supplied rendered-view camera/eye and return clipped screen pixels or normalized top-left rectangles. These are conservative projected geometry boxes, not pixel segmentation or camera-frame calibration. Geometry crossing the camera near plane is rejected. Use the actual rendered eye camera; do not apply its rectangles directly to raw passthrough images. Unity documents the [per-eye world-to-screen overload and bottom-left pixel origin](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Camera.WorldToScreenPoint.html); the helper explicitly converts that to a top-left normalized rectangle.

Detecting physical surgical tools in raw camera images is a separate data/model task and is not supplied by these FBX files or the existing COCO bottle detector.

## Verification

The batch/editor entry points are:

```text
Scalpal.Instruments.Editor.InstrumentAssetBuilder.BuildAll
Scalpal.Instruments.Editor.InstrumentRuntimeValidation.Run
```

The validator checks all fifteen prefab IDs, anchors, metric +Z orientation, a 2,000-triangle runtime budget, Rigidbody/colliders, controller grip pickup, grip alignment, authored seam separation and single event, registration gating, tracking-loss release, restored physics, tracking-recovery grip release, clip/staple trigger debounce, grasp release, simulated sealing, demo suction, reset, and sandbox existence. Root integration records actual results separately after running this against the generated models. Source availability alone does not establish a passed import or physical-headset behavior.

Required follow-up is a physical Quest rehearsal in the real XR rig: pick up/release each tool, articulate jaws, run the virtual-patch effects, and deliberately interrupt controller and torso tracking. Quest performance, per-eye projection, real-person registration, and educational validity are not established by editor validation.
