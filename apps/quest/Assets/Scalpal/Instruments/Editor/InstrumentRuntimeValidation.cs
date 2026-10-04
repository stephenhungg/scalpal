using System;
using UnityEditor;
using UnityEngine;

namespace Scalpal.Instruments.Editor
{
    // Batch entry point: -executeMethod Scalpal.Instruments.Editor.InstrumentRuntimeValidation.Run
    public static class InstrumentRuntimeValidation
    {
        const string Root = "Assets/Scalpal/Instruments/Prefabs/";
        static int checks;

        static void Require(bool condition, string message)
        {
            checks++;
            if (!condition) throw new InvalidOperationException("Scalpal validation failed: " + message);
        }

        public static void Run()
        {
            checks = 0;
            foreach (string id in InstrumentAssetBuilder.ToolIds)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Root + "inst_" + id + ".prefab");
                Require(prefab != null, id + " prefab exists");
                var instrument = prefab.GetComponent<InstrumentBehaviour>();
                Require(instrument != null && instrument.instrumentId == id, id + " stable ID");
                Require(instrument.actionPoint != null && instrument.gripAnchor != null, id + " named anchors");
                Require(prefab.GetComponent<Rigidbody>() != null && prefab.GetComponents<Collider>().Length >= 2, id + " rigidbody and compound colliders");
                var tipContact = prefab.GetComponentInChildren<InstrumentTipContact>(true);
                Require(tipContact != null && tipContact.name == "Tip" && tipContact.GetComponent<SphereCollider>().isTrigger, id + " named distal Tip trigger");
                Require(prefab.GetComponent<InstrumentProjection>() != null, id + " virtual projection helper");
                if (id == "laparoscope_30") Require(prefab.GetComponent<LaparoscopeView>() != null && prefab.GetComponent<LaparoscopeView>().opticDirection != null, "scope has virtual render camera configuration");
                int triangles = 0;
                foreach (var filter in prefab.GetComponentsInChildren<MeshFilter>(true)) if (filter.sharedMesh != null) triangles += filter.sharedMesh.triangles.Length / 3;
                Require(triangles > 0 && triangles <= 2000, id + " runtime triangle budget");
                var localTip = prefab.transform.InverseTransformPoint(instrument.actionPoint.position);
                Require(localTip.z > 0.03f && localTip.z < 1 && Mathf.Abs(localTip.x) < 0.002f && Mathf.Abs(localTip.y) < 0.002f, id + " metric +Z tip");
            }
            var scope = new GameObject("RuntimeValidationScope");
            try
            {
                var hand = new GameObject("ValidationHand");
                hand.transform.SetParent(scope.transform);
                var interactor = hand.AddComponent<InstrumentInteractor>();
                var toolObject = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(Root + "inst_scalpel.prefab"), scope.transform);
                var tool = toolObject.GetComponent<InstrumentBehaviour>();
                var toolRestPosition = tool.transform.position;
                var toolRestRotation = tool.transform.rotation;
                interactor.SetTrackedPose(Vector3.zero, Quaternion.identity, true);
                Physics.SyncTransforms();
                interactor.SetGrip(1);
                Require(interactor.HeldInstrument == tool && tool.Held, "grip picks up nearest tool");
                Require((tool.gripAnchor.position - hand.transform.position).magnitude < 0.001f, "grip anchor aligns to hand");
                Require(Vector3.Dot((tool.actionPoint.position - tool.gripAnchor.position).normalized, hand.transform.forward) > 0.999f, "held tool points along controller +Z");
                Require(toolObject.GetComponent<Rigidbody>().isKinematic, "held body is kinematic");
                Require(toolObject.GetComponent<Rigidbody>().interpolation == RigidbodyInterpolation.None, "held tracked body disables delayed physics interpolation");
                var shifted = new Vector3(0.21f, 0.37f, -0.15f);
                var turned = Quaternion.Euler(25, 70, -15);
                interactor.SetTrackedPose(shifted, turned, true);
                Require((tool.gripAnchor.position - shifted).sqrMagnitude < 0.000001f && Quaternion.Angle(tool.gripAnchor.rotation, turned) < 0.001f, "held grip follows a changed tracked pose immediately");
                interactor.SetTrackedPose(Vector3.zero, Quaternion.identity, true);
                var patchObject = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(Root + "TrainingPatch.prefab"), scope.transform);
                patchObject.transform.position = new Vector3(0, 0, 1);
                var patch = patchObject.GetComponent<TrainingTarget>();
                Require(!patch.RegistrationValid, "reusable target defaults to invalid registration");
                Require(patch.Apply(InstrumentAction.Clip, patch.transform.position, patch.transform.position, 0.02f) == null && patch.ClipCount == 0, "invalid registration blocks effects");
                patch.SetRegistrationValid(true);
                int cuts = 0;
                tool.ActionApplied += e => { if (e.outcome == "seam_separated") cuts++; };
                interactor.SetActivation(1);
                for (int i = 0; i <= 18; i++)
                {
                    Vector3 desiredTip = patch.transform.TransformPoint(new Vector3(-0.045f + i * 0.005f, 0, 0));
                    interactor.SetTrackedPose(hand.transform.position + desiredTip - tool.actionPoint.position, hand.transform.rotation, true);
                    Physics.SyncTransforms();
                    tool.SimulateStep(0.02f);
                }
                Require(patch.IsCut && cuts == 1, "active tracked scalpel separates authored seam and publishes once");
                Require((patch.leftHalf.localPosition - patch.rightHalf.localPosition).magnitude > 0.06f, "cut visibly separates halves");
                var frozenPosition = tool.transform.position;
                var frozenRotation = tool.transform.rotation;
                var recoveryPosition = hand.transform.position;
                var recoveryRotation = hand.transform.rotation;
                double clock = Time.unscaledTimeAsDouble;
                interactor.SetTrackedPose(Vector3.one * 100, Quaternion.identity, false, clock);
                interactor.SetGrip(0); // Missing XR button data must not masquerade as a grip release.
                interactor.SetActivation(1);
                interactor.AdvanceTrackingLoss(clock + 0.1);
                Require(tool.Held && interactor.HeldInstrument == tool && !tool.TrackingValid && tool.Activation == 0,
                    "brief tracking loss keeps held equipment but blocks every action");
                Require(toolObject.GetComponent<Rigidbody>().isKinematic && !toolObject.GetComponent<Rigidbody>().useGravity,
                    "tracking grace freezes a kinematic tool rather than dropping it");
                hand.transform.position += Vector3.one; // Simulate tracking-origin movement while paused.
                Require((tool.transform.position - frozenPosition).sqrMagnitude < 0.000001f && Quaternion.Angle(tool.transform.rotation, frozenRotation) < 0.001f,
                    "world-frozen equipment ignores invalid pose and parent movement");
                interactor.SetTrackedPose(recoveryPosition, recoveryRotation, true, clock + 0.1);
                interactor.SetGrip(1);
                interactor.SetActivation(1);
                Require(tool.Held && tool.TrackingValid && tool.Activation == 0 &&
                    (tool.transform.position - frozenPosition).sqrMagnitude < 0.000001f,
                    "brief recovery retains calibrated pose and requires deliberate trigger rearm");
                interactor.SetActivation(0);
                interactor.SetActivation(1);
                Require(tool.Activation == 1, "observed trigger release enables subsequent actions");
                interactor.SetTrackedPose(recoveryPosition, recoveryRotation, false, clock + 0.2);
                interactor.AdvanceTrackingLoss(clock + 0.39);
                Require(interactor.HeldInstrument == tool, "tracking grace does not expire early");
                interactor.AdvanceTrackingLoss(clock + 0.401);
                Require(!tool.Held && !tool.TrackingValid && interactor.HeldInstrument == null, "sustained loss returns held equipment and disables effects");
                Require((tool.transform.position - toolRestPosition).sqrMagnitude < 0.000001f && Quaternion.Angle(tool.transform.rotation, toolRestRotation) < 0.001f,
                    "sustained loss restores the tool's captured rest pose");
                Require(!toolObject.GetComponent<Rigidbody>().isKinematic && toolObject.GetComponent<Rigidbody>().useGravity, "release restores physics");
                Require(toolObject.GetComponent<Rigidbody>().interpolation == RigidbodyInterpolation.Interpolate, "release restores the original interpolation policy");
                interactor.SetTrackedPose(tool.gripAnchor.position, Quaternion.identity, true);
                interactor.SetGrip(1);
                Require(interactor.HeldInstrument == null, "tracking recovery requires physical grip release before pickup");
                interactor.SetGrip(0);
                Physics.SyncTransforms();
                interactor.SetGrip(1);
                Require(interactor.HeldInstrument == tool, "explicit release and new grip reenable pickup after tracking recovery");
                interactor.SetActivation(0);
                interactor.SetTrackedPose(hand.transform.position, hand.transform.rotation, false, clock + 1);
                interactor.SetTrackedPose(hand.transform.position + Vector3.right, hand.transform.rotation, true, clock + 1.1);
                Require(interactor.HeldInstrument == null && (tool.transform.position - toolRestPosition).sqrMagnitude < 0.000001f,
                    "discontinuous tracking recovery cannot teleport a held blade across the patient");
                interactor.SetGrip(0);
                interactor.SetActivation(0);
                interactor.Release();
                patch.ResetTeachingTarget();
                Require(!patch.IsCut && patch.ClipCount == 0 && patch.FluidRemaining == 1, "target reset restores authored state");

                VerifyDiscreteAction("clip_applier", patch, hand, interactor, scope.transform);
                VerifyDiscreteAction("endo_stapler", patch, hand, interactor, scope.transform);
                VerifyGrasp(patch, hand, interactor, scope.transform);
                VerifyContinuous("hook_cautery", patch, hand, interactor, scope.transform);
                VerifyContinuous("suction_irrigator", patch, hand, interactor, scope.transform);
                VerifyOpenTools(scope.transform);
                VerifyProjection(scope.transform);
                VerifyAnatomyContact(scope.transform);
                Require(AssetDatabase.LoadAssetAtPath<SceneAsset>("Assets/Scalpal/Instruments/Samples/InstrumentSandbox.unity") != null, "sandbox scene exists");
                Debug.Log("SCALPAL_INSTRUMENT_VALIDATION_OK: " + checks + " checks. Editor physics/contact tests; physical Quest input not tested.");
            }
            finally { UnityEngine.Object.DestroyImmediate(scope); }
        }

        static void VerifyOpenTools(Transform parent)
        {
            var kit = Resources.Load<GameObject>("OpenSurgeryInstruments");
            Require(kit != null, "open surgery runtime Resources kit exists");
            var kitTools = kit.GetComponentsInChildren<InstrumentBehaviour>(true);
            int retractors = 0, hemostats = 0;
            var ids = new System.Collections.Generic.HashSet<string>();
            foreach (var item in kitTools)
            {
                ids.Add(item.instrumentId);
                if (item.instrumentId == "retractor") retractors++;
                if (item.instrumentId == "hemostat") hemostats++;
            }
            Require(kitTools.Length == 10 && retractors == 2 && hemostats == 2, "open tray has two independent retractors and two independent hemostats");
            Require(ids.SetEquals(OpenInstrumentModels.Ids), "open tray contains every open instrument ID");
            var hand = new GameObject("OpenToolValidationHand");
            hand.transform.SetParent(parent);
            var interactor = hand.AddComponent<InstrumentInteractor>();
            foreach (string id in OpenInstrumentModels.Ids)
            {
                var model = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(Root + "inst_" + id + ".prefab"), parent);
                try
                {
                    var tool = model.GetComponent<InstrumentBehaviour>();
                    Require(model.GetComponentsInChildren<InstrumentTipContact>(true).Length == 1,
                        id + " uses one distal contact adapter");
                    foreach (var filter in model.GetComponentsInChildren<MeshFilter>(true))
                    {
                        var mesh = filter.sharedMesh;
                        Require(mesh != null && AssetDatabase.Contains(mesh), id + " mesh persists as an asset");
                        bool valid = true;
                        var vertices = mesh.vertices;
                        var triangles = mesh.triangles;
                        foreach (var point in vertices)
                            valid &= !float.IsNaN(point.x) && !float.IsNaN(point.y) && !float.IsNaN(point.z)
                                && !float.IsInfinity(point.x) && !float.IsInfinity(point.y) && !float.IsInfinity(point.z);
                        for (int triangle = 0; triangle < triangles.Length; triangle += 3)
                            valid &= Vector3.Cross(vertices[triangles[triangle + 1]] - vertices[triangles[triangle]],
                                vertices[triangles[triangle + 2]] - vertices[triangles[triangle]]).sqrMagnitude > 1e-20f;
                        Require(valid, id + " mesh has finite vertices and no degenerate triangles");
                    }
                    interactor.SetTrackedPose(Vector3.zero, Quaternion.identity, true);
                    Require(interactor.TryPickup(tool), id + " tracked pickup");
                    interactor.SetTrackedPose(new Vector3(.31f, .52f, -.4f), Quaternion.Euler(20, 30, 40), true);
                    Require((tool.gripAnchor.position - hand.transform.position).magnitude < .001f, id + " grip follows tracked hand");
                    tool.SetActivation(0);
                    Quaternion open = tool.upperJaw ? tool.upperJaw.localRotation : Quaternion.identity;
                    tool.SetActivation(1);
                    if (tool.upperJaw) Require(Quaternion.Angle(open, tool.upperJaw.localRotation) > 5, id + " trigger articulates jaws");
                    if (id == "metzenbaum_scissors")
                    {
                        Transform start = null, end = null;
                        foreach (var anchor in model.GetComponentsInChildren<Transform>())
                        { if (anchor.name == "CutStart") start = anchor; if (anchor.name == "CutEnd") end = anchor; }
                        Require(start && end && Vector3.Distance(start.position, end.position) >= .002f && Vector3.Distance(start.position, end.position) <= .06f,
                            "Metzenbaum supplies one metric blade segment to the tissue driver");
                    }
                    interactor.SetTrackedPose(hand.transform.position, hand.transform.rotation, false);
                    Require(!tool.Held && tool.Activation == 0, id + " tracking loss releases and deactivates");
                    Require(!tool.GetComponent<Rigidbody>().isKinematic && tool.GetComponent<Rigidbody>().useGravity,
                        id + " release restores pickup physics");
                }
                finally { interactor.Release(); UnityEngine.Object.DestroyImmediate(model); }
            }
        }

        static void VerifyProjection(Transform parent)
        {
            var camera = new GameObject("ProjectionValidationCamera").AddComponent<Camera>();
            camera.transform.SetParent(parent);
            camera.transform.position = new Vector3(0, 0, -1);
            camera.transform.rotation = Quaternion.identity;
            camera.pixelRect = new Rect(0, 0, 800, 600);
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.transform.SetParent(parent);
            cube.transform.position = Vector3.zero;
            cube.transform.localScale = Vector3.one * 0.1f;
            var projection = cube.AddComponent<InstrumentProjection>();
            Require(projection.TryGetNormalizedTopLeftBounds(camera, out Rect bounds) && bounds.xMin >= 0 && bounds.yMin >= 0 && bounds.xMax <= 1 && bounds.yMax <= 1, "virtual renderer projection produces clipped normalized bounds");
            cube.transform.position = new Vector3(0, 0, -2);
            Require(!projection.TryGetPixelBounds(camera, out _), "projection rejects geometry behind camera");
        }

        static void VerifyAnatomyContact(Transform parent)
        {
            var model = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(Root + "inst_lap_scissors.prefab"), parent);
            var tool = model.GetComponent<InstrumentBehaviour>();
            var tip = model.GetComponentInChildren<InstrumentTipContact>();
            var anatomy = new GameObject("anat_demo_only");
            anatomy.transform.SetParent(parent);
            var collider = anatomy.AddComponent<BoxCollider>();
            int touches = 0;
            tip.TouchApplied += (structure, instrument) => { if (structure == "demo_only" && instrument == "lap_scissors") touches++; };
            tool.SetHeld(true);
            tool.SetTrackingValid(true);
            tool.SetActivation(1);
            Require(!tip.TryReportContact(collider), "anatomy touch defaults to blocked without registration provider");
            tip.RegistrationIsValid = () => true;
            Require(tip.TryReportContact(collider) && touches == 1, "registered deliberate distal contact publishes real IDs");
            Require(!tip.TryReportContact(collider) && touches == 1, "continuous overlap does not duplicate anatomy touch");
            var secondCollider = anatomy.AddComponent<SphereCollider>();
            Require(!tip.TryReportContact(secondCollider) && touches == 1, "two colliders on one anatomy structure publish one touch per activation");
            tip.gameObject.SendMessage("OnTriggerExit", collider, SendMessageOptions.DontRequireReceiver);
            Require(!tip.TryReportContact(collider) && touches == 1, "exit and reentry while holding activation do not rearm anatomy touch");
            tool.SetActivation(0);
            tool.SetActivation(1);
            Require(tip.TryReportContact(secondCollider) && touches == 2, "deliberate trigger release and reactivation rearm anatomy touch");
            tool.SetTrackingValid(false);
            Require(!tip.TryReportContact(collider) && touches == 2, "lost tracking blocks anatomy touch");
            tool.SetHeld(false);
        }

        static InstrumentBehaviour PickupAtTarget(string id, TrainingTarget target, GameObject hand, InstrumentInteractor interactor, Transform parent)
        {
            interactor.Release();
            var instance = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(Root + "inst_" + id + ".prefab"), parent);
            var tool = instance.GetComponent<InstrumentBehaviour>();
            interactor.SetTrackedPose(instance.transform.position, Quaternion.identity, true);
            Require(interactor.TryPickup(tool), id + " can be picked up");
            interactor.SetTrackedPose(hand.transform.position + target.transform.position - tool.actionPoint.position, Quaternion.identity, true);
            Physics.SyncTransforms();
            tool.SimulateStep(0.02f);
            return tool;
        }

        static void VerifyDiscreteAction(string id, TrainingTarget patch, GameObject hand, InstrumentInteractor interactor, Transform parent)
        {
            patch.ResetTeachingTarget();
            var tool = PickupAtTarget(id, patch, hand, interactor, parent);
            interactor.SetActivation(1);
            for (int i = 0; i < 10; i++) tool.SimulateStep(0.02f);
            bool isClip = id == "clip_applier";
            Require((isClip ? patch.ClipCount : patch.StapleCount) == 1, id + " holds activation without repeated firing");
            interactor.SetActivation(0);
            interactor.SetActivation(1);
            tool.SimulateStep(0.02f);
            Require((isClip ? patch.ClipCount : patch.StapleCount) == 2, id + " rearms after trigger release");
            interactor.Release();
        }

        static void VerifyGrasp(TrainingTarget patch, GameObject hand, InstrumentInteractor interactor, Transform parent)
        {
            patch.ResetTeachingTarget();
            Transform previousParent = patch.transform.parent;
            var tool = PickupAtTarget("atraumatic_grasper", patch, hand, interactor, parent);
            interactor.SetActivation(1);
            tool.SimulateStep(0.02f);
            Require(patch.transform.parent == tool.actionPoint, "grasper attaches the virtual target");
            interactor.SetActivation(0);
            Require(patch.transform.parent == previousParent, "grasper releases and restores target parent");
            interactor.Release();
        }

        static void VerifyContinuous(string id, TrainingTarget patch, GameObject hand, InstrumentInteractor interactor, Transform parent)
        {
            patch.ResetTeachingTarget();
            var tool = PickupAtTarget(id, patch, hand, interactor, parent);
            interactor.SetActivation(1);
            for (int i = 0; i < 100; i++) tool.SimulateStep(0.02f);
            Require(id == "hook_cautery" ? patch.IsSealed : patch.FluidRemaining == 0, id + " authored continuous effect completes");
            interactor.Release();
        }
    }
}
