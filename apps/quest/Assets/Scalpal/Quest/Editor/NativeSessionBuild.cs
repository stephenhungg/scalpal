using System;
using System.IO;
using System.Linq;
using Scalpal.Anatomy;
using Scalpal.Anatomy.EditorTools;
using Scalpal.Exercises.Coach;
using Scalpal.Exercises.Data;
using Scalpal.Realtime;
using Scalpal.Voice;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features.Meta;
using UnityEngine.XR.OpenXR.Features.CompositionLayers;
using Meta.XR;

namespace Scalpal.Quest.Editor
{
    // One native authored VR rehearsal using the existing rig and shared instruments.
    // The source-to-mannequin transform is a teaching fit, not measured participant registration.
    public static class NativeSessionBuild
    {
        public const string ScenePath = "Assets/Scalpal/Quest/Scenes/NativeSession.unity";
        const string CaseId = "case_patient-demo-multi-source_lap_appendectomy";
        const string MaterialFolder = "Assets/Scalpal/Quest/Materials";
        const float AnatomyScale = 1.75f / 1.743f;
        static readonly string[] AnatomyIds = {
            "appendicular_artery", "appendix", "cecum", "greater_omentum", "mesoappendix",
            "right_ureter", "small_bowel", "terminal_ileum", "urinary_bladder"
        };

        [MenuItem("Scalpal/Quest/Prepare Native Session")]
        public static void Prepare()
        {
            NativeQuestBuild.Prepare();
            var scene = EditorSceneManager.OpenScene(NativeQuestBuild.ScenePath, OpenSceneMode.Single);
            var workbench = UnityEngine.Object.FindFirstObjectByType<NativeWorkbench>();
            if (!workbench) throw new InvalidOperationException("Native workbench rig is missing.");
            var patient = scene.GetRootGameObjects().Single(root => root.name == "PatientRoot");
            var room = scene.GetRootGameObjects().Single(root => root.name == "VirtualOperatingRoom");
            // Move room/table and patient together. The tool table and its reset poses stay intact.
            var delta = new Vector3(0.5f, 0f, -0.35f) - patient.transform.position;
            patient.transform.position += delta;
            room.transform.position += delta;
            var mannequin = patient.GetComponentsInChildren<Renderer>(true).Single();
            var patientBounds = mannequin.bounds;
            // The VR patient reads as solid skin; it is hidden in AR, where only the anatomy overlay is drawn.
            mannequin.sharedMaterials = mannequin.sharedMaterials.Select(_ => PatientSkin()).ToArray();
            PrefabUtility.RecordPrefabInstancePropertyModifications(mannequin);
            var unbound = patient.transform.Find("AnatomyRoot_Unbound");
            if (unbound) UnityEngine.Object.DestroyImmediate(unbound.gameObject);

            var prefab = AnatomyAtlasBuilder.BuildExercise();
            var fit = new GameObject("AuthoredAnatomyToPatient").transform;
            fit.SetParent(patient.transform, false);
            // The exported atlas retains a foot-origin source frame. X90 maps upright +Y
            // toward the reclining patient's +Z and assumed source anterior -Z to +Y.
            // Align with the mannequin's actual foot extent, rather than assuming centered art.
            fit.SetPositionAndRotation(new Vector3(patientBounds.center.x, patientBounds.center.y, patientBounds.min.z),
                Quaternion.Euler(90f, 0f, 0f));
            fit.localScale = Vector3.one * AnatomyScale;
            var practiceObject = (GameObject)PrefabUtility.InstantiatePrefab(prefab, fit);
            practiceObject.name = "PracticeAnatomy";
            var anatomy = practiceObject.GetComponent<AnatomyController>();
            anatomy.SetPreviewMode(false);
            anatomy.SetPreviewRotation(false);
            anatomy.SetRegistrationValid(false);

            var frame = new GameObject("AuthoredPatientTorsoFrame").transform;
            frame.SetParent(patient.transform, false);
            // Authored approximate skin umbilicus; +X patient left, +Y anterior, +Z cranial.
            frame.SetPositionAndRotation(new Vector3(patientBounds.center.x, patientBounds.max.y + 0.015f,
                patientBounds.min.z + 1.08f * AnatomyScale), Quaternion.identity);
            var bundle = LoadBundle();
            var selected = bundle.cases.Single(item => item.caseId == CaseId);
            foreach (var port in selected.procedure.ports)
            {
                var site = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                site.name = "port_" + port.id;
                site.transform.SetParent(frame, false);
                site.transform.localPosition = new Vector3(port.position.x, port.position.y, port.position.z) * selected.bodyScale;
                site.transform.localScale = Vector3.one * 0.036f;
                site.GetComponent<SphereCollider>().isTrigger = true;
                site.GetComponent<Renderer>().sharedMaterial = PortMaterial();
                site.AddComponent<NativePortMarker>().portId = port.id;
            }

            var previewRoot = new GameObject("SelectionPreviewPedestal").transform;
            previewRoot.position = new Vector3(-0.45f, 1.35f, 0.25f);
            var overview = AnatomyAtlasBuilder.BuildOrganOverview();
            var previewObject = (GameObject)PrefabUtility.InstantiatePrefab(overview, previewRoot);
            previewObject.name = "SelectionAnatomy";
            var sourceBounds = MeshBounds(previewObject);
            previewObject.transform.localScale = Vector3.one * 0.7f;
            previewObject.transform.localPosition = -sourceBounds.center * 0.7f;
            var preview = previewObject.GetComponent<AnatomyController>();
            foreach (var collider in previewObject.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
            preview.RebuildIndex();
            preview.SetPreviewMode(true);
            preview.SetPreviewRotation(true);
            var previewSettings = new SerializedObject(preview);
            previewSettings.FindProperty("previewRotationRoot").objectReferenceValue = previewRoot;
            previewSettings.ApplyModifiedPropertiesWithoutUndo();

            var sessionObject = new GameObject("NativeCaseSession");
            var coach = sessionObject.AddComponent<CoachRelay>();
            var exercise = sessionObject.AddComponent<AnatomyExerciseBinding>();
            exercise.anatomy = anatomy;
            exercise.coach = coach;
            exercise.presentationMode = "mixed_reality";
            exercise.requireCoachSynchronization = true;
            workbench.externalSessionControls = true;
            var session = sessionObject.AddComponent<NativeCaseSession>();
            sessionObject.AddComponent<NativeScenePointer>();
            sessionObject.AddComponent<NativeProcedureChecklist>();
            sessionObject.AddComponent<NativePatientMonitor>().session = session;
            session.workbench = workbench;
            session.exercise = exercise;
            session.anatomy = anatomy;
            session.preview = preview;
            session.coach = coach;
            session.realtime = sessionObject.AddComponent<QuestSessionBridge>();
            session.voice = sessionObject.AddComponent<QuestJarvisVoice>();
            session.patientFrame = frame;
            var presentation = sessionObject.AddComponent<NativePresentation>();
            presentation.headCamera = workbench.headCamera;
            presentation.cameraManager = workbench.headCamera.gameObject.AddComponent<ARCameraManager>();
            presentation.virtualRoom = room;
            presentation.virtualMannequin = mannequin;
            presentation.anatomyFit = fit;
            presentation.patientFrame = frame;
            presentation.session = session;
            session.presentation = presentation;
            workbench.presentation = presentation;
            var body = sessionObject.AddComponent<NativeBodyRegistration>();
            body.workbench = workbench; body.presentation = presentation;
            body.cameraAccess = sessionObject.AddComponent<PassthroughCameraAccess>();
            body.cameraAccess.enabled = false;
            body.cameraAccess.RequestedResolution = new Vector2Int(640, 480);
            body.surfaceAccess = sessionObject.AddComponent<EnvironmentRaycastManager>();
            body.surfaceAccess.enabled = false;
            body.surfaceAccess.CustomTrackingSpace = workbench.trackingOrigin;
            body.anatomyFit = fit; body.patientFrame = frame;
            var bodyObject = (GameObject)PrefabUtility.InstantiatePrefab(overview);
            bodyObject.name = "BodyFitOrganOverview";
            body.bodyOverview = bodyObject.GetComponent<AnatomyController>();
            body.bodyOverview.SetPreviewMode(true); body.bodyOverview.SetPreviewRotation(false);
            bodyObject.SetActive(false);
            session.bodyRegistration = body;
            sessionObject.AddComponent<ARSession>();
            presentation.Apply();
            session.status = workbench.status;
            workbench.status.transform.position = new Vector3(-0.15f, 1.65f, 0.3f);
            workbench.status.characterSize = 0.012f;
            workbench.status.text = "Scalpal | Appendectomy rehearsal\nReview the case and confirm selection\nGrip: pick up   Trigger: use   B: review / confirm\nX: identify   Y: voice   A: reset tools   Left menu: retry";

            // AnatomyPart captures these enabled flags as authored defaults at runtime Awake.
            // The practice controller then hides geometry until its explicit validity gate opens.
            foreach (var renderer in practiceObject.GetComponentsInChildren<Renderer>(true)) renderer.enabled = true;
            foreach (var collider in practiceObject.GetComponentsInChildren<Collider>(true)) collider.enabled = true;
            PrefabUtility.RecordPrefabInstancePropertyModifications(anatomy);
            PrefabUtility.RecordPrefabInstancePropertyModifications(preview);
            foreach (var collider in previewObject.GetComponentsInChildren<Collider>(true))
                PrefabUtility.RecordPrefabInstancePropertyModifications(collider);
            ApplySessionSettings();
            Directory.CreateDirectory("Assets/Scalpal/Quest/Scenes");
            if (!EditorSceneManager.SaveScene(scene, ScenePath)) throw new InvalidOperationException("Could not save native session scene.");
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
            Validate();
            Debug.Log("SCALPAL_NATIVE_SESSION_PREPARED view=mixed_reality registration=required_live_body_fit physicalPlaythroughUnverified=true");
        }

        static void ApplySessionSettings()
        {
            PlayerSettings.productName = "Scalpal Surgical Session";
            PlayerSettings.bundleVersion = "0.5.1-volume";
            PlayerSettings.Android.bundleVersionCode = 10;
            // Meta's OpenXR camera-pose plugin requires linear lighting; retain built-in rendering.
            PlayerSettings.colorSpace = ColorSpace.Linear;
            var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
            settings.GetFeature<ARSessionFeature>().enabled = true;
            settings.GetFeature<ARCameraFeature>().enabled = true;
            settings.GetFeature<OpenXRCompositionLayersFeature>().enabled = true;
            settings.GetFeature<MetaXRFeature>().enabled = true;
            var meta = OVRProjectConfig.CachedProjectConfig;
            meta.insightPassthroughSupport = OVRProjectConfig.FeatureSupport.Supported;
            meta.isPassthroughCameraAccessEnabled = true;
            meta.sceneSupport = OVRProjectConfig.FeatureSupport.Supported;
            OVRProjectConfig.CommitProjectConfig(meta);
            EditorUtility.SetDirty(settings);
            // The integration harness uses LAN HTTP only in this development player.
            PlayerSettings.insecureHttpOption = InsecureHttpOption.DevelopmentOnly;
        }

        static ScalpalBundle LoadBundle()
        {
            var text = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/Scalpal/Exercises/Resources/scalpal_bundle.json");
            if (!text) throw new InvalidOperationException("Packaged case bundle is missing.");
            var bundle = JsonUtility.FromJson<ScalpalBundle>(text.text);
            if (bundle?.cases == null) throw new InvalidOperationException("Packaged cases are missing.");
            return bundle;
        }

        static Material PatientSkin() => Material("AuthoredPatientSkin", new Color(0.65f, 0.5f, 0.4f, 1f), false);
        static Material PortMaterial() => Material("AuthoredPortSite", new Color(0.2f, 0.7f, 0.85f), false);

        static Material Material(string name, Color color, bool transparent)
        {
            if (!AssetDatabase.IsValidFolder(MaterialFolder)) AssetDatabase.CreateFolder("Assets/Scalpal/Quest", "Materials");
            var path = MaterialFolder + "/" + name + ".mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            var shader = Shader.Find("Standard");
            if (!shader) throw new InvalidOperationException("Standard shader is missing.");
            bool create = !material;
            if (create) material = new Material(shader);
            material.shader = shader;
            material.name = name;
            material.color = color;
            material.SetFloat("_Mode", transparent ? 2f : 0f);
            material.SetFloat("_Glossiness", 0.15f);
            material.SetFloat("_SrcBlend", (float)(transparent ? UnityEngine.Rendering.BlendMode.SrcAlpha : UnityEngine.Rendering.BlendMode.One));
            material.SetFloat("_DstBlend", (float)(transparent ? UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha : UnityEngine.Rendering.BlendMode.Zero));
            material.SetFloat("_ZWrite", transparent ? 0f : 1f);
            material.DisableKeyword("_ALPHATEST_ON");
            material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            if (transparent) material.EnableKeyword("_ALPHABLEND_ON"); else material.DisableKeyword("_ALPHABLEND_ON");
            material.SetOverrideTag("RenderType", transparent ? "Transparent" : "Opaque");
            material.SetShaderPassEnabled("ShadowCaster", !transparent);
            material.renderQueue = transparent ? 3000 : -1;
            if (create) AssetDatabase.CreateAsset(material, path); else EditorUtility.SetDirty(material);
            return material;
        }

        // Mesh bounds in this root's frame, independent of renderer visibility and source pivot.
        static Bounds MeshBounds(GameObject root)
        {
            bool started = false;
            var result = new Bounds();
            foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (!filter.sharedMesh) throw new InvalidOperationException("Anatomy mesh is missing.");
                var bounds = filter.sharedMesh.bounds;
                var matrix = root.transform.worldToLocalMatrix * filter.transform.localToWorldMatrix;
                for (int corner = 0; corner < 8; corner++)
                {
                    var point = bounds.center + Vector3.Scale(bounds.extents,
                        new Vector3((corner & 1) == 0 ? -1f : 1f, (corner & 2) == 0 ? -1f : 1f, (corner & 4) == 0 ? -1f : 1f));
                    point = matrix.MultiplyPoint3x4(point);
                    if (!started) { result = new Bounds(point, Vector3.zero); started = true; }
                    else result.Encapsulate(point);
                }
            }
            if (!started) throw new InvalidOperationException("No anatomy geometry.");
            return result;
        }

        [MenuItem("Scalpal/Quest/Validate Native Session")]
        public static void Validate()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var roots = scene.GetRootGameObjects();
            var sessions = roots.SelectMany(root => root.GetComponentsInChildren<NativeCaseSession>(true)).ToArray();
            if (sessions.Length != 1) throw new InvalidOperationException("Exactly one native case session is required.");
            var session = sessions[0];
            if (!session.GetComponent<NativeScenePointer>())
                throw new InvalidOperationException("Native session is missing the read-only scene identity pointer.");
            if (!session.GetComponent<NativeProcedureChecklist>())
                throw new InvalidOperationException("Native session is missing the read-only procedure checklist.");
            var monitor = session.GetComponent<NativePatientMonitor>();
            if (!monitor || monitor.session != session)
                throw new InvalidOperationException("Native session is missing the matched simulated patient monitor.");
            if (!session.workbench || !session.exercise || !session.anatomy || !session.preview || !session.coach
                || !session.realtime || !session.voice || !session.patientFrame || !session.status)
                throw new InvalidOperationException("Native session has a missing required binding.");
            if (session.anatomy == session.preview || session.exercise.anatomy != session.anatomy || session.exercise.coach != session.coach
                || session.status != session.workbench.status || session.exercise.presentationMode != "mixed_reality" || !session.exercise.requireCoachSynchronization
                || !session.workbench.externalSessionControls)
                throw new InvalidOperationException("Native case/coach/preview bindings are inconsistent.");
            if (roots.SelectMany(root => root.GetComponentsInChildren<AnatomyExerciseBinding>(true)).Count() != 1
                || roots.SelectMany(root => root.GetComponentsInChildren<AnatomyController>(true)).Count() != 3
                || roots.SelectMany(root => root.GetComponentsInChildren<CoachRelay>(true)).Count() != 1)
                throw new InvalidOperationException("Native scene must have one case runner/relay and separate preview/practice anatomy.");
            var rig = session.workbench;
            if (rig.tools == null || rig.tools.Length != 15 || rig.tools.Any(tool => !tool)
                || rig.tools.Select(tool => tool.instrumentId).Distinct().Count() != 15 || rig.inputs == null || rig.inputs.Length != 2
                || !rig.trackingOrigin || !rig.headCamera || rig.headCamera.transform.parent != rig.trackingOrigin
                || rig.inputs.Any(input => !input || input.trackingOrigin != rig.trackingOrigin || input.enabled))
                throw new InvalidOperationException("Native XR/tool bindings are missing or initially ungated.");
            ValidateAnatomy(session.anatomy, false);
            ValidateOverview(session.preview);
            ValidateOverview(session.bodyRegistration.bodyOverview);
            ValidatePresentation(session);
            var sourceFit = session.anatomy.transform.parent;
            if (!sourceFit || (sourceFit.localScale - Vector3.one * AnatomyScale).sqrMagnitude > 0.000001f
                || Quaternion.Angle(sourceFit.rotation, Quaternion.Euler(90f, 0f, 0f)) > 0.001f)
                throw new InvalidOperationException("Authored atlas-to-patient axes/metric scale changed.");
            var ports = session.patientFrame.GetComponentsInChildren<NativePortMarker>(true);
            var selected = LoadBundle().cases.Single(item => item.caseId == CaseId);
            if (ports.Length != selected.procedure.ports.Length || ports.Select(port => port.portId).Distinct().Count() != ports.Length)
                throw new InvalidOperationException("Authored port IDs are missing or duplicated.");
            foreach (var port in selected.procedure.ports)
            {
                var marker = ports.SingleOrDefault(item => item.portId == port.id);
                var expected = new Vector3(port.position.x, port.position.y, port.position.z) * selected.bodyScale;
                if (!marker || !marker.GetComponent<Collider>() || !marker.GetComponent<Collider>().isTrigger
                    || (marker.transform.localPosition - expected).sqrMagnitude > 0.000001f)
                    throw new InvalidOperationException("Port site does not match the authored torso-frame position: " + port.id);
            }
            foreach (var root in roots)
            {
                if (root.GetComponentsInChildren<Component>(true).Any(component => component == null))
                    throw new InvalidOperationException("Missing script in native session.");
                foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                    if (renderer.sharedMaterials.Length == 0 || renderer.sharedMaterials.Any(material => !material || !material.shader))
                        throw new InvalidOperationException("Missing material/shader in native session.");
                if (root.GetComponentsInChildren<MeshFilter>(true).Any(filter => !filter.sharedMesh))
                    throw new InvalidOperationException("Missing mesh in native session.");
            }
            var atlasDependencies = AssetDatabase.GetDependencies(ScenePath, true)
                .Where(path => path.StartsWith("Assets/Scalpal/Anatomy/Models/", StringComparison.Ordinal) && path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileNameWithoutExtension).OrderBy(name => name).ToArray();
            if (!atlasDependencies.SequenceEqual(new[] { "cardiovascular", "exercise-targets", "lymphatic", "muscular", "skeletal", "surface", "visceral" }))
                throw new InvalidOperationException("Unexpected whole-body/detail atlas dependency in the native session.");
            Debug.Log("SCALPAL_NATIVE_SESSION_SCENE_VALIDATED tools=15 controllers=2 practiceParts=9 practiceTriangles=93399 overviewParts=81 overviewTriangles=120125 ports=3 initialValidity=false "
                + "sourceBounds=" + MeshBounds(session.anatomy.gameObject));
        }

        static void ValidatePresentation(NativeCaseSession session)
        {
            var presentation = session.presentation;
            var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
            if (!presentation || presentation != session.workbench.presentation || !presentation.passthrough
                || presentation.headCamera != session.workbench.headCamera || !presentation.cameraManager
                || presentation.cameraManager.gameObject != presentation.headCamera.gameObject
                || !presentation.cameraManager.enabled || presentation.virtualRoom.activeSelf
                || presentation.headCamera.backgroundColor.a != 0f
                || session.exercise.presentationMode != "mixed_reality"
                || !session.GetComponent<ARSession>() || !settings.GetFeature<ARSessionFeature>().enabled
                || !settings.GetFeature<ARCameraFeature>().enabled
                || !settings.GetFeature<OpenXRCompositionLayersFeature>().enabled
                || !settings.GetFeature<MetaXRFeature>().enabled
                || !presentation.virtualMannequin || presentation.virtualMannequin.enabled
                || !session.bodyRegistration || session.bodyRegistration.Accepted || session.bodyRegistration.EnabledByOperator
                || !session.bodyRegistration.cameraAccess || session.bodyRegistration.cameraAccess.enabled
                || !session.bodyRegistration.surfaceAccess || session.bodyRegistration.surfaceAccess.enabled
                || session.bodyRegistration.surfaceAccess.CustomTrackingSpace != session.workbench.trackingOrigin
                || session.bodyRegistration.anatomyFit != session.anatomy.transform.parent
                || session.bodyRegistration.patientFrame != session.patientFrame
                || session.bodyRegistration.bodyOverview.gameObject.activeSelf)
                throw new InvalidOperationException("MR default, required body-fit bindings or transparent camera are incorrect.");
        }

        static void ValidateOverview(AnatomyController controller)
        {
            var parts = controller.GetComponentsInChildren<AnatomyPart>(true);
            int triangles = controller.GetComponentsInChildren<MeshFilter>(true).Sum(filter => filter.sharedMesh.triangles.Length / 3);
            if (parts.Length != AnatomyAtlasBuilder.OrganOverviewPartCount
                || !parts.Select(part => part.stableId).OrderBy(id => id).SequenceEqual(AnatomyAtlasBuilder.OrganOverviewIds.OrderBy(id => id))
                || triangles != AnatomyAtlasBuilder.OrganOverviewExpectedTriangles
                || triangles > AnatomyAtlasBuilder.OrganOverviewTriangleBudget
                || !controller.PreviewMode || controller.RegistrationValid
                || controller.GetComponentsInChildren<Collider>(true).Length != 0)
                throw new InvalidOperationException("Render-only organ overview IDs, geometry budget or preview gates are incorrect.");
        }

        static void ValidateAnatomy(AnatomyController controller, bool preview)
        {
            var parts = controller.GetComponentsInChildren<AnatomyPart>(true);
            var filters = controller.GetComponentsInChildren<MeshFilter>(true);
            if (parts.Length != AnatomyIds.Length || filters.Length != AnatomyIds.Length
                || !parts.Select(part => part.stableId).OrderBy(id => id).SequenceEqual(AnatomyIds.OrderBy(id => id))
                || filters.Sum(filter => filter.sharedMesh ? filter.sharedMesh.triangles.Length / 3 : 0) != 93399)
                throw new InvalidOperationException("Exercise anatomy must contain exactly its nine authored parts/meshes and 93,399 triangles.");
            if (controller.RegistrationValid || controller.PreviewMode != preview)
                throw new InvalidOperationException("Anatomy preview/practice validity is incorrectly authored.");
            if (parts.Any(part => !part.GetComponent<MeshCollider>() || part.GetComponent<MeshCollider>().sharedMesh != part.GetComponent<MeshFilter>().sharedMesh))
                throw new InvalidOperationException("Anatomy targets need colliders using their own imported meshes.");
            if (preview && controller.GetComponentsInChildren<Collider>(true).Any(collider => collider.enabled))
                throw new InvalidOperationException("Selection preview colliders must remain disabled.");
        }

        public static void Build()
        {
            var output = Environment.GetEnvironmentVariable("SCALPAL_QUEST_APK");
            if (string.IsNullOrWhiteSpace(output) || !Path.IsPathRooted(output))
                throw new InvalidOperationException("SCALPAL_QUEST_APK must be an absolute output path.");
            NativeQuestBuild.Configure();
            ApplySessionSettings();
            Verify();
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            EditorUserBuildSettings.buildAppBundle = false;
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
                scenes = new[] { ScenePath }, locationPathName = output, target = BuildTarget.Android, options = BuildOptions.Development
            });
            if (report.summary.result != BuildResult.Succeeded) throw new InvalidOperationException("Native session build failed: " + report.summary.result);
            Debug.Log("SCALPAL_NATIVE_SESSION_BUILD_OK bytes=" + report.summary.totalSize);
        }

        public static void Verify()
        {
            Validate();
            NativeCaseModelValidation.Run();
            Scalpal.Surgery.Editor.OpenBodyValidation.Run();
            NativeBodyAtlasValidation.Run();
            NativeBodyRegistrationValidation.Run();
            NativeOperatingRoomModeValidation.Run();
            NativeOperatingRoomPhysicsValidation.Run();
            NativeLocomotionValidation.Run();
            Scalpal.Instruments.Editor.InstrumentRuntimeValidation.Run();
            NativeProcedureInputValidation.Run();
            NativeInteriorContactValidation.Run();
            NativeTissueValidation.Run();
            NativeVolumeValidation.Run();
            NativeViscoelasticValidation.Run();
            NativeCouponValidation.Run();
            NativeSkinCalibrationBenchmark.Run();
            NativeTissueInterfaceValidation.Run();
            NativeVolumeAccelerationValidation.Run();
            NativeVolumeRuntimeValidation.Run();
            NativeOpenWallValidation.Run();
            NativeWoundResolutionValidation.Run();
            NativeBleedingValidation.Run();
            NativeVesselRuntimeValidation.Run();
            NativeTissueContactValidation.Run();
            NativeContactMotionValidation.Run();
            NativeScenePointerValidation.Run();
            NativeProcedureChecklistValidation.Run();
            NativePatientMonitorValidation.Run();
            NativeAppendectomyValidation.Run();
            NativeSessionBoundaryValidation.Run();
            NativeCoachRelayValidation.Run();
            Scalpal.Capture.Editor.CaptureValidation.Run();
            Scalpal.Shell.Editor.DialogueBoxValidation.Run();
            // Fixtures must not leave temporary poses, offline gates or substituted bindings in the player.
            Validate();
            Debug.Log("SCALPAL_NATIVE_SESSION_VERIFY_OK");
        }
    }
}
