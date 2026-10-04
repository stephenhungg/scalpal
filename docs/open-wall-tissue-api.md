# Open Wall Tissue Interfaces

The open appendectomy uses one case-independent teaching wall in both AR and VR. `Surgery/OpenSurgerySession` supplies the McBurney wound frame; `NativeVolumeSimulation.Initialize(woundFrame, workbench, gate, true)` builds the volume at that frame's anterior origin. Do not reproject this wall from appendix mesh bounds. The existing right-ASIS/umbilicus defaults are authored proxies, not measured MediaPipe anatomical landmarks.

## Layer identity and geometry

`OpenWallLayers` is the shared descriptor source. Coordinates are wound-local rest metres, +Z inward. It exposes exact IDs and authored depth intervals: skin 0–2 mm, fat 2–14 mm, fascia 14–19 mm, muscle 19–27 mm, peritoneum 27–28 mm. Factory cell materials use those intervals. Fascia and muscle have an authored +X fiber axis; split separation is measured across +Y. `TryFiberAngle` returns an unsigned in-plane angle 0–90°; absent/invalid directions return false/NaN. It measures geometry, not tissue anisotropy or case success. Wrong-layer blade injury remains physically possible.

`TryMcBurney(rightAsisMeters, umbilicusMeters, out point)` interpolates one-third from explicit right-ASIS to umbilicus inputs. It does not infer or acquire those landmarks. The caller chooses the source frame and preserves its provenance.

## Generic interaction properties and verbs

`TissueInteractionProperties` is an immutable, case-independent value view: exact tissue ID, layer/order, cuttable/splittable/tentable capabilities, optional local 3D fiber axis, perfused/hollow/critical flags and provenance. It is not a second organ registry, JSON wire contract, injury state or bleeding clock. The existing `TissueDefinition` and body state remain the semantic authority. `OpenWallLayers.Layer.properties` exposes known wall geometry/capabilities; its consequence flags are nullable and **unknown until explicitly bound**, rather than claiming an unbound tissue cannot bleed, leak or cause harm.

After initializing the open wall, the Surgery owner can project the five existing `TissueDefinition` values into these immutable profiles using the corresponding authored `OpenWallLayers` fibers, then call `TryBindLayerProperties(profiles, out reason)`. All five identities, layers/orders, mechanical capabilities and fiber axes must match the actual generated geometry. Complete perfused/hollow/critical flags and provenance are required. Binding copies the values atomically; missing, duplicate or conflicting profiles refuse without replacing an earlier binding. Retry retains metadata; reinitialization clears it for the next body. `TryGetLayerProperties(id, out properties)` can be called before practice, and `HasConsequenceProperties` distinguishes bound semantics from geometry-only metadata. Metadata access is not proof of exposure, registration or valid practice.

`TryCutLayer(id, worldA, worldB, worldC, worldStrokeDirection, out fracture)` and `TrySplitLayer(id, worldA, worldB, worldC, out fracture)` are material-specific low-level mechanical fracture verbs. They change actual topology and emit the existing `LayerFractured` fact only when new faces break. Duplicate/unknown/degenerate/invalid-gate requests refuse. Cuts use a physical0.5mm tolerance; a wrong-layer muscle cut remains possible and is not converted into a split. The finite split plane follows the layer's splittable capability and fibers, within the wall separator's authored geometric tolerance. `TryMeasureLayerSplit(first, second, out millimeters)` measures two accepted material attachments across their common layer's fibers. Existing muscle-named APIs are compatibility wrappers over these generic methods.

These fracture inputs are caller-derived swept surfaces, not proof that a tool acquired the material or that the body has exposed it. The Surgery consumer must use current material contacts and tool lifecycles when deriving those surfaces, preserve the single scored event route, and apply the existing body consequences. Mechanical `verb="split"` is not a new scored wire verb: the existing body schema uses paired `retract` evidence. Cut alignment measures stroke versus the fiber axis; paired retraction alignment measures spread versus the across-fiber axis. Do not interchange them.

Generic properties accept arbitrary tissue IDs and local3D fibers, but this wall facade supports only its five actual materials. It does not fabricate bowel/vessel geometry or implement wall clamp/tie/seal/suction behavior. Those mechanisms remain with the existing actual vessel models and Surgery body/organ adapter. Unknown fiber angles remain NaN in mechanical output and signed lift/spread remains signed: `BodyAction` refuses nonfinite and negative measurements, so the adapter must preserve measurement validity and intentionally form compatible scored evidence, never serialize an unknown angle as zero or treat a request as achieved motion.

## Physical wall adapter

Public methods on `NativeVolumeSimulation`:

| API | Input/output | Meaning |
| --- | --- | --- |
| `TryContactLayer(id, worldPoint, worldRadius, out worldContact)` | Unity world metres | Closest current exposed boundary/cut face of that material; intact internal interfaces are not contact surfaces. Radius is bounded to 10 mm. |
| `TryBeginLayerHandle(id, worldPoint, worldRadius, out token)` | Layer ID, world contact, opaque integer token | Attaches a compliant finite material patch at a barycentric face point. Up to four simultaneous material grips; tokens are not reused by that component. |
| `TrySetLayerHandleTarget(token, worldPoint)` | Requested world position | Updates a request, not successful tissue motion. |
| `TryMeasureLayerHandle(token, out measurement)` | Actual accepted material geometry | Reports layer ID, world position/displacement, signed outward lift in physical millimetres, accepted-step status and topology revision. `hasAcceptedStep` means at least one accepted step since acquisition; `lastStepAccepted` reports the latest trial. |
| `ReleaseLayerHandle(token)` | Opaque token | Releases only that attachment. |
| `TryMeasureMuscleSplit(first, second, out increaseMillimeters)` | Two distinct muscle tokens | Measures the signed increase of actual material-point separation across fibers from the acquisition baseline. Repeated tokens, other materials and unstepped attachments refuse. |
| `TrySplitMuscle(worldA, worldB, worldC, out fracture)` | Finite requested split triangle | Separates only muscle interior faces along a fiber-compatible plane. Emits `verb="split"`, not a blade-cut event. The surgery adapter must derive the finite surface from real paired retractor contact/pull; a requested split triangle alone is not a scored action. |
| `LayerFractured` | Mechanical fact event | Per-material newly broken face counts, topology revision, cut/split verb and fiber angle. Automatic blade sweep angles use movement direction rather than blade-edge orientation. |

Uniform scale and rigid rotation are handled explicitly: measurement displacement is transformed into world metres before converting to millimetres. Nonuniform, sheared, reflected or nonfinite frames refuse these APIs. Gate loss, disable, frame change, retry and reinitialization release old grips; restoring a gate does not resurrect them. A cut remaps each material face through its owning cell/corner identities, retaining its own crack side without welding opposite lips. Acquisition and cut-time patch rebuilding allocate temporary buffers; actual Quest allocation/frame-time impact remains unmeasured. The legacy single-handle coupon remains supported separately; automatic whole-wall grasp is disabled for the open-wall composition so it cannot fight the surgery-owned handles.

The solver preserves its existing compliant finite patch, per-step travel limit, 20 mm material-target excursion cap, retry/backtracking and positive-Jacobian checks. Two grips are real simultaneous constraints, not two controller-distance counters. No measured surgical-force or tissue-viscosity claim is made. Fracture follows the coarse tetrahedral faces; broken-face count is not incision length, exposed area, or a percentage-open milestone. A fractured interface contributes to both adjoining material IDs; contact and verb evidence must remain distinct from an injury or complete division claim. A muscle split changes topology; the low-level historical `CutFaceCount` counts all broken faces, including splits.

## Surgery integration work order

The surgery owner should bind these APIs in the existing `OpenBodyInteraction` path, keeping the current `CaseRunner`/coach as sole scoring authority:

1. Bind caller-owned generic properties as described above, then replace duplicated depth arrays with `OpenWallLayers`; the old fat contact plane starts at 4 mm while the actual fat starts at 2 mm.
2. Acquire wall contact and a material handle from current geometry, keyed to each tool's lifecycle. Update requested positions, then read accepted measurements after the native solver update. Do not award tenting or split width from raw controller travel.
3. Use two distinct muscle attachments for paired retraction. Derive a finite fiber-aligned split surface from actual contact. Preserve forceps-held peritoneum while nicking and use its signed accepted lift in the existing `BodyAction` measurement.
4. Map layer fracture facts into the existing authored body outcomes without creating a second reducer or advancing a step directly. Body exposure, injury rules and finish/grade remain with the surgery engine.
5. Keep authored thresholds explicit. These APIs do not replace organ mobilization. Merged `952cef8` adds a separate scene-authored rigid cecum-group mobilization with a 200 mm tether; `02a7791` validates delivery against actual scene geometry. That extends the 18.1 mm local cage rather than awarding delivery from raw tool motion. It remains an uncalibrated teaching constraint, not reviewed mesenteric mechanics.

Current Surgery still uses static wall contact planes, controller-derived wall lift/spread, and a semantic wound view. Shipping these tissue interfaces does not establish that all those consumers are coupled yet. Its preexisting local-distance-versus-AR-scale assessment mismatch also remains until the owner consumes physical world-unit measurements consistently.

## One bleeding ledger

`VesselBleeding.CaptureSnapshot()` returns immutable values copied from the actual finite-source model: model validity/fault, injury/occlusion/active flow, injury area in square metres, cumulative loss/pool/removed/remaining source in explicitly named millilitres, available flow in millilitres per second, and model elapsed seconds. A captured snapshot stays unchanged after subsequent simulation, suction, occlusion or reset. `IsValid` describes the model state, not headset registration or anatomical validity.

`OpenBodyBleeding` already uses one `VesselBleeding` per scored perfused tissue and emits the existing shared `fluid` action. Consume snapshots from those same instances. Do not re-enable the disabled legacy `NativeVesselSimulation` beside it or step a source twice; that would duplicate clocks, pools and ledgers. Injury location/source association and the injury-area assumption remain the surgery adapter's explicit responsibility. The authored 0.5 mm radius opening is not a measured cut area. Suction reduces pool volume while cumulative blood loss remains unchanged; occlusion stops available discharge.

## Verification boundary

`NativeOpenWallValidation.Run` exercises the actual public descriptors, factory, solver, world facade and fluid model with synthetic inputs. The standard native Unity gate includes it. Actual headset feel, Quest CPU budget, registered reclining-person fit, complete physical open appendectomy, provider voice, participant footage and robot replay remain separate checks. See the [integration map](system-integration.md) for source commits and measured results.


Checkpoint source `5473aa9`, synchronized at `e5abdee`. The full native gate passed10,627 new wall assertions plus all existing mechanics/session fixtures. The open-surgery boundary gate passed318 adapter/fluid/delivery checks and current scene bindings. The scaled automatic-blade near-miss regression failed before its tolerance fix and passed afterward. Synthetic accepted lift/spread ranges and remaining consumer work are recorded in the integration map; these values do not establish surgical thresholds.

Final publication synchronization at `1871c93` also passed the full native gate with10,627 wall assertions. The Play Mode and open-surgery boundary evidence above was run at `e5abdee`; neither result establishes the pending Surgery material-grip hookup or a physical operation.
