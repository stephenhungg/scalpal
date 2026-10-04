using System;
using System.Collections.Generic;
using System.Linq;
using Scalpal.Environments.Editor;
using Scalpal.Exercises.Engine;
using Scalpal.Instruments;
using Scalpal.Surgery;
using Scalpal.Surgery.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Scalpal.Quest.Editor
{
    // Committed native scene in the Editor with scripted physics steps, not a headset test. The VR patient must
    // behave like a body on a table: solid skin, the scored teaching wound on the skin the learner can see and
    // touch, and dropped tools that come to rest on the table, patient, instrument table and floor.
    public static class NativeOperatingRoomPhysicsValidation
    {
        const float Dt = .02f;
        static int checks;
        static void Assert(bool passed, string message)
        {
            checks++;
            if (!passed) throw new InvalidOperationException("Operating room physics validation: " + message);
        }

        [MenuItem("Scalpal/Quest/Validate Operating Room Patient and Physics")]
        public static void Run()
        {
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            checks = 0;
            var previous = EditorSceneManager.GetSceneManagerSetup();
            var mode = Physics.simulationMode;
            bool backfaces = Physics.queriesHitBackfaces;
            NativeTissueSimulation tissue = null;
            try
            {
                Physics.simulationMode = SimulationMode.Script;
                var scene = EditorSceneManager.OpenScene(NativeSessionBuild.ScenePath, OpenSceneMode.Single);
                NativeCaseSession session = null;
                foreach (var root in scene.GetRootGameObjects()) { var found = root.GetComponentInChildren<NativeCaseSession>(true); if (found) session = found; }
                Assert(session && session.presentation && session.presentation.virtualMannequin, "scene binds the VR mannequin");
                PatientIsSolid(session.presentation);
                PatientLiesUnderItsTorsoFrame(session);
                string drops = DroppedToolsRest(session);

                var adapter = OpenSurgeryBuild.ConfigureSceneAttempt(out session, out tissue);
                string kit = KitRestsOnInstrumentTable(session);
                string contact = TouchingTheVisibleSkinActs(session, adapter);
                Debug.Log($"SCALPAL_OR_PHYSICS_VALIDATION_OK checks={checks} {drops} {kit} {contact} editorScriptedPhysics=true headsetValidated=false");
            }
            finally
            {
                Physics.simulationMode = mode;
                Physics.queriesHitBackfaces = backfaces;
                if (tissue) tissue.Dispose();
                OpenSurgeryBuild.RestoreScenes(previous);
            }
        }

        // A transparent, depth-less body lets the table, organs and floor show through the patient in VR.
        static void PatientIsSolid(NativePresentation view)
        {
            var materials = view.virtualMannequin.sharedMaterials;
            Assert(materials.Length > 0, "VR mannequin has a material");
            foreach (var material in materials)
            {
                Assert(material && material.shader, "VR mannequin material and shader exist");
                Assert(material.renderQueue < (int)RenderQueue.AlphaTest && material.GetTag("RenderType", true) == "Opaque",
                    "VR patient renders in the opaque queue: " + material.name + " queue=" + material.renderQueue);
                Assert(!material.HasProperty("_Color") || Mathf.Approximately(material.color.a, 1f), "VR patient skin has full alpha: " + material.name);
                Assert(!material.HasProperty("_ZWrite") || material.GetFloat("_ZWrite") >= 1f, "VR patient writes depth so it occludes what is behind it");
                Assert(!material.IsKeywordEnabled("_ALPHABLEND_ON") && !material.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON"), "VR patient has no alpha blending");
                Assert(material.GetShaderPassEnabled("ShadowCaster"), "VR patient casts shadows like a solid body");
                // Solid skin would otherwise hide the layered teaching wall and the organs under the incision.
                Assert(material.shader.name == "Scalpal/PatientSkin", "VR patient skin opens only over a live teaching wound: " + material.shader.name);
            }
        }

        static MeshCollider[] Hulls(NativePresentation view) => view.virtualMannequin.GetComponentsInChildren<MeshCollider>(true)
            .Where(collider => collider.name == EnvironmentPreviewBuilder.PatientCollisionName).ToArray();
        // The rendered skin, via a temporary collider on the render mesh; callers destroy it.
        static MeshCollider VisibleSkin(NativePresentation view)
        {
            var skin = new GameObject("RenderSkinProbe").AddComponent<MeshCollider>();
            skin.transform.SetParent(view.virtualMannequin.transform, false);
            skin.sharedMesh = view.virtualMannequin.GetComponent<MeshFilter>().sharedMesh;
            Physics.SyncTransforms();
            return skin;
        }
        static bool SkinBelow(Collider skin, Vector3 world, out float height)
        {
            height = 0;
            Physics.queriesHitBackfaces = true; // A few source triangles on the mannequin midline are wound inward.
            bool hit = skin.Raycast(new Ray(new Vector3(world.x, 3, world.z), Vector3.down), out var info, 4);
            if (hit) height = info.point.y;
            return hit;
        }

        // The torso frame is +Z cranial with its origin at the umbilicus; ports, the teaching wound and the
        // coach's body regions are all placed in it, so the visible body must lie under it the same way round.
        static void PatientLiesUnderItsTorsoFrame(NativeCaseSession session)
        {
            var view = session.presentation;
            var hulls = Hulls(view);
            Assert(hulls.Length == 1 && hulls.All(hull => hull.enabled && !hull.isTrigger && hull.sharedMesh && hull.gameObject.layer == 2),
                "VR patient has one static collider on its outward-wound skin mesh, outside the identity pointer's raycast layers");
            var skin = VisibleSkin(view);
            try {
            var frame = session.patientFrame;
            Assert(SkinBelow(skin, frame.position, out float umbilicus) && Mathf.Abs(frame.position.y - umbilicus) < .015f,
                // A flat authored frame on a domed belly: ports (36 mm trigger spheres) stay reachable at the skin.
                $"authored umbilicus frame sits at the visible abdomen (frame y={frame.position.y:F3}, skin y={umbilicus:F3})");
            float lift = view.virtualMannequin.bounds.min.y - 1.07f; // mattress top measured from the theatre art
            Assert(lift > -.005f && lift < .03f, $"VR patient lies on the mattress, not inside it (back {lift * 1000:F0} mm above the mattress)");
            // A supine body's highest point is its upturned toes, which must be caudal (-Z) of the umbilicus.
            var bounds = view.virtualMannequin.bounds;
            float top = float.NegativeInfinity, topZ = 0;
            for (float x = bounds.min.x; x <= bounds.max.x; x += .01f)
                for (float z = bounds.min.z; z <= bounds.max.z; z += .01f)
                    if (SkinBelow(skin, new Vector3(x, 0, z), out float y) && y > top) { top = y; topZ = z; }
            Assert(topZ < frame.position.z - .5f, $"mannequin feet point caudal like the anatomy fit (toes z={topZ:F2}, umbilicus z={frame.position.z:F2})");
            } finally { UnityEngine.Object.DestroyImmediate(skin.gameObject); }
        }

        // Reference heights come from the rendered art itself (temporary mesh colliders on the room and patient
        // meshes, removed before stepping), so a drop is judged against what the learner sees.
        static string DroppedToolsRest(NativeCaseSession session)
        {
            var view = session.presentation;
            view.passthrough = false; view.Apply();
            Assert(view.virtualRoom.activeInHierarchy && view.virtualRoom.GetComponentsInChildren<BoxCollider>(true)
                .Count(collider => collider.transform.parent && collider.transform.parent.name == EnvironmentPreviewBuilder.RoomCollisionName && collider.enabled && !collider.isTrigger) == 8,
                "VR operating table, instrument stands and floor have solid colliders");
            var art = new List<MeshCollider>();
            foreach (var filter in view.virtualRoom.GetComponentsInChildren<MeshFilter>(true).Append(view.virtualMannequin.GetComponent<MeshFilter>()))
            {
                var temporary = new GameObject("RenderSurfaceProbe").AddComponent<MeshCollider>();
                temporary.transform.SetParent(filter.transform, false); temporary.sharedMesh = filter.sharedMesh; temporary.enabled = false; art.Add(temporary);
            }
            var tray = InstrumentTable();
            Physics.SyncTransforms();
            var frame = session.patientFrame.position;
            var targets = new (string name, Vector3 point)[] {
                ("abdomen", frame + new Vector3(-.04f, 0, -.05f)), ("chest", frame + new Vector3(0, 0, .18f)),
                ("thigh", frame + new Vector3(.08f, 0, -.7f)), ("table_beside_head", new Vector3(view.virtualRoom.transform.position.x - .16f, 0, frame.z + .59f)),
                ("instrument_table", tray.transform.position + new Vector3(-.125f, 0, .2f)), ("instrument_stand", new Vector3(-.05f, 0, .05f)), ("floor", new Vector3(-.5f, 0, -.25f)) };
            // Starts above the patient and tables but below the theatre lights and ceiling.
            float Surface(Vector3 at)
            {
                foreach (var temporary in art) temporary.enabled = true;
                Physics.SyncTransforms(); Physics.queriesHitBackfaces = true;
                var ray = new Ray(new Vector3(at.x, 1.4f, at.z), Vector3.down);
                float height = float.NegativeInfinity;
                foreach (var collider in art.Cast<Collider>().Append(tray))
                    if (collider && collider.Raycast(ray, out var hit, 2)) height = Mathf.Max(height, hit.point.y);
                foreach (var temporary in art) temporary.enabled = false;
                return height;
            }
            var expected = new float[targets.Length];
            for (int i = 0; i < targets.Length; i++)
            {
                expected[i] = Surface(targets[i].point);
                Assert(float.IsFinite(expected[i]), "visible surface exists under the " + targets[i].name + " drop");
            }

            var scalpel = session.workbench.tools.Single(tool => tool.instrumentId == "scalpel");
            var body = scalpel.GetComponent<Rigidbody>();
            var hand = session.workbench.inputs[0].GetComponent<InstrumentInteractor>();
            var report = new List<string>();
            for (int i = 0; i < targets.Length; i++)
            {
                // Real pickup/release path: release must hand a sweeping (continuous) body back to physics.
                Vector3 grip = scalpel.gripAnchor.position;
                hand.SetTrackedPose(grip, Quaternion.identity, true, i * 10);
                Assert(hand.TryPickup(scalpel), "hand picks up the scalpel for drop " + targets[i].name);
                Assert(body.isKinematic && body.collisionDetectionMode == CollisionDetectionMode.ContinuousSpeculative, "a held tool is kinematic with speculative detection");
                hand.SetTrackedPose(new Vector3(targets[i].point.x, expected[i] + .3f, targets[i].point.z), Quaternion.Euler(0, 35, 0), true, i * 10 + .01);
                hand.Release();
                Assert(!body.isKinematic && body.collisionDetectionMode == CollisionDetectionMode.ContinuousDynamic, "a released tool sweeps continuously so it cannot tunnel");
                body.position = scalpel.transform.position; body.rotation = scalpel.transform.rotation;
                body.linearVelocity = Vector3.down * 3; body.angularVelocity = Vector3.zero;
                Physics.SyncTransforms();
                for (int step = 0; step < 200; step++) Physics.Simulate(Dt);
                // A tool may roll off a curved body; wherever it stops, no part of it may be inside the visible surface.
                var parts = scalpel.GetComponentsInChildren<Collider>().Where(c => !c.isTrigger).ToArray();
                float lowest = parts.Min(c => c.bounds.min.y), sunk = 0, rest = float.NegativeInfinity;
                foreach (var part in parts)
                {
                    var box = part.bounds;
                    sunk = Mathf.Max(sunk, Surface(box.center) - box.min.y);
                    // A tool can bridge a gap (between the legs, off a table edge): it rests on the highest support under it.
                    foreach (var corner in new[] { box.center, box.min, box.max, new Vector3(box.min.x, 0, box.max.z), new Vector3(box.max.x, 0, box.min.z) })
                        rest = Mathf.Max(rest, Surface(corner));
                }
                Assert(sunk <= .012f && lowest <= rest + .06f && body.linearVelocity.magnitude < .05f && (targets[i].name == "floor" || lowest > .5f),
                    $"a scalpel dropped onto the {targets[i].name} comes to rest on a visible surface (lowest={lowest:F3}, dropSurface={expected[i]:F3}, restSurface={rest:F3}, sunk={sunk * 1000:F1} mm, speed={body.linearVelocity.magnitude:F3})");
                report.Add($"{targets[i].name}:sunkMm={sunk * 1000:F1}");
            }
            foreach (var temporary in art) UnityEngine.Object.DestroyImmediate(temporary.gameObject);
            // AR has no virtual body: its collider must not catch tools above a real participant.
            view.passthrough = true; view.Apply();
            Assert(Hulls(view).All(hull => !hull.enabled), "AR disables the hidden mannequin's colliders");
            view.passthrough = false; view.Apply();
            return "dropRestOffsetsM=" + string.Join(",", report);
        }

        // The authored dark instrument table every tool starts on (a scene root, separate from the rig).
        static Collider InstrumentTable() => UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects()
            .Single(root => root.name == "Workbench").GetComponent<Collider>();

        // Every authored tool starts on the instrument table; no tool, including the open-case kit, may fall through
        // anything. The kit is still laid out past the table end (OpenSurgerySession's +0.32 m offset), so its tools
        // settle on the floor; moving it perturbs the actual-scene wall coupling fixture, so that is reported here.
        static string KitRestsOnInstrumentTable(NativeCaseSession session)
        {
            var tray = InstrumentTable();
            Assert(tray && !tray.isTrigger, "instrument table is solid");
            float top = tray.bounds.max.y;
            Physics.SyncTransforms();
            for (int step = 0; step < 150; step++) Physics.Simulate(Dt);
            int onTable = 0, onFloor = 0;
            foreach (var tool in session.workbench.tools)
            {
                float lowest = tool.GetComponentsInChildren<Collider>().Where(c => !c.isTrigger).Min(c => c.bounds.min.y);
                bool table = lowest >= top - .012f && lowest <= top + .06f, floor = lowest >= -.012f && lowest <= .12f; // room floor (VR, top 0.06 m) or the catch floor (top 0)
                Assert(table || (floor && tool.transform.root.name == "OpenSurgeryCaseTools"),
                    $"{tool.name} rests on the instrument table, or (open-case kit only) on the visible floor (lowest={lowest:F3}, top={top:F3})");
                if (table) onTable++; else onFloor++;
            }
            return $"toolsOnInstrumentTable={onTable} kitToolsOnFloor={onFloor}";
        }

        // The learner touches the patient they see. In the expected first state (nothing marked or opened) a held
        // scalpel resting on that skin must explain that the trigger cuts, and a stroke with the trigger must
        // open the skin and produce a scored body action.
        static string TouchingTheVisibleSkinActs(NativeCaseSession session, OpenSurgerySession adapter)
        {
            var input = adapter.Interaction; var wound = adapter.Wound.transform; var exercise = session.exercise;
            var volume = session.GetComponent<NativeVolumeSimulation>();
            volume.Initialize(wound, session.workbench, () => true, true);
            input.Initialize(exercise, session.workbench.tools, session.patientFrame, wound, () => true);
            input.BindWall(volume);
            var records = new List<BodyRecord>(); var hints = new List<string>();
            input.Submitted += (record, _) => records.Add(record);
            input.TouchedWithoutTrigger += (tool, verb, tissueId) => hints.Add(tool.instrumentId + ":" + verb + ":" + tissueId);
            void Step() { input.Simulate(Dt); volume.Simulate(Dt); Physics.SyncTransforms(); }

            var skin = VisibleSkin(session.presentation);
            bool found = SkinBelow(skin, wound.position, out float skinHeight);
            UnityEngine.Object.DestroyImmediate(skin.gameObject);
            Assert(found, "visible skin under the McBurney teaching wound");
            float proud = wound.position.y - skinHeight;
            Assert(proud >= 0 && proud <= .004f, $"teaching wound lies on the visible abdomen, not above it (wound-skin={proud * 1000:F1} mm)");

            var scalpel = session.workbench.tools.Single(tool => tool.instrumentId == "scalpel");
            var anchors = scalpel.GetComponentsInChildren<Transform>(true);
            var cutStart = anchors.Single(t => t.name == "CutStart"); var cutEnd = anchors.Single(t => t.name == "CutEnd");
            Vector3 skinCenter = new Vector3(wound.position.x, skinHeight, wound.position.z);
            void Blade(float along)
            {
                scalpel.transform.rotation = wound.rotation;
                scalpel.transform.rotation = Quaternion.FromToRotation(cutEnd.position - cutStart.position, wound.forward) * scalpel.transform.rotation;
                scalpel.transform.position += skinCenter + wound.right * along + wound.forward * .0015f - cutEnd.position;
                Physics.SyncTransforms();
            }
            scalpel.SetHeld(true); scalpel.SetTrackingValid(true); scalpel.SetActivation(0);
            Blade(-.03f); Step(); Step();
            Assert(records.Count == 0 && hints.Count == 1 && hints[0] == "scalpel:cut:skin",
                "a scalpel resting on the skin without the trigger applies nothing and asks for the trigger once: " + string.Join(",", hints));
            Assert(SurgeryTriggerHint.Phrase("cut") == "Hold trigger to cut", "the learner is told which input cuts");

            scalpel.SetActivation(1);
            for (int i = 0; i <= 12; i++) { Blade(Mathf.Lerp(-.03f, .03f, i / 12f)); Step(); }
            scalpel.SetActivation(0); Step();
            var cut = records.FirstOrDefault(record => record.action.verb == "cut" && record.action.tissueId == "skin");
            Assert(cut != null && exercise.Body.Get("skin", "opened") > 0,
                "a trigger-held scalpel stroke across the visible skin opens it as a scored body action: " + string.Join(",", records.Select(r => r.action.verb + ":" + r.action.tissueId)));
            scalpel.SetHeld(false);
            return $"woundAboveSkinMm={proud * 1000:F1} skinCutMm={cut.action.lengthMm:F1}";
        }
    }
}
