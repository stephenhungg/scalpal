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
                interactor.SetTrackedPose(Vector3.zero, Quaternion.identity, true);
                Physics.SyncTransforms();
                interactor.SetGrip(1);
                Require(interactor.HeldInstrument == tool && tool.Held, "grip picks up nearest tool");
                Require((tool.gripAnchor.position - hand.transform.position).magnitude < 0.001f, "grip anchor aligns to hand");
                Require(Vector3.Dot((tool.actionPoint.position - tool.gripAnchor.position).normalized, hand.transform.forward) > 0.999f, "held tool points along controller +Z");
                Require(toolObject.GetComponent<Rigidbody>().isKinematic, "held body is kinematic");
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
                interactor.SetTrackedPose(hand.transform.position, hand.transform.rotation, false);
                Require(!tool.Held && !tool.TrackingValid && interactor.HeldInstrument == null, "lost tracking releases held tool and disables effects");
                Require(!toolObject.GetComponent<Rigidbody>().isKinematic && toolObject.GetComponent<Rigidbody>().useGravity, "release restores physics");
                interactor.SetTrackedPose(tool.gripAnchor.position, Quaternion.identity, true);
                interactor.SetGrip(1);
                Require(interactor.HeldInstrument == null, "tracking recovery requires physical grip release before pickup");
                interactor.SetGrip(0);
                Physics.SyncTransforms();
                interactor.SetGrip(1);
                Require(interactor.HeldInstrument == tool, "explicit release and new grip reenable pickup after tracking recovery");
                interactor.Release();
                patch.ResetTeachingTarget();
                Require(!patch.IsCut && patch.ClipCount == 0 && patch.FluidRemaining == 1, "target reset restores authored state");

                VerifyDiscreteAction("clip_applier", patch, hand, interactor, scope.transform);
                VerifyDiscreteAction("endo_stapler", patch, hand, interactor, scope.transform);
                VerifyGrasp(patch, hand, interactor, scope.transform);
                VerifyContinuous("hook_cautery", patch, hand, interactor, scope.transform);
                VerifyContinuous("suction_irrigator", patch, hand, interactor, scope.transform);
                VerifyProjection(scope.transform);
                VerifyAnatomyContact(scope.transform);
                Require(AssetDatabase.LoadAssetAtPath<SceneAsset>("Assets/Scalpal/Instruments/Samples/InstrumentSandbox.unity") != null, "sandbox scene exists");
                Debug.Log("SCALPAL_INSTRUMENT_VALIDATION_OK: " + checks + " checks. Editor physics/contact tests; physical Quest input not tested.");
            }
            finally { UnityEngine.Object.DestroyImmediate(scope); }
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
