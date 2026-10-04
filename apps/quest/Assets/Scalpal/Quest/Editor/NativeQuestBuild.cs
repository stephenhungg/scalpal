using System;
using System.IO;
using System.Linq;
using Scalpal.Instruments;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEditor.XR.OpenXR.Features;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features.Interactions;
using UnityEngine.XR.OpenXR.Features.MetaQuestSupport;

namespace Scalpal.Quest.Editor
{
    public static class NativeQuestBuild
    {
        public const string ScenePath = "Assets/Scalpal/Quest/Scenes/NativeWorkbench.unity";
        const string Sandbox = "Assets/Scalpal/Instruments/Samples/InstrumentSandbox.unity";
        const string ArtPreview = "Assets/Scalpal/Environment/Samples/OperatingRoomPreview.unity";
        public const string PackageId = "com.scalpal.nativeworkbench";

        [MenuItem("Scalpal/Quest/Prepare Native Workbench")]
        public static void Prepare()
        {
            Configure();
            Directory.CreateDirectory("Assets/Scalpal/Quest/Scenes");
            AssetDatabase.Refresh();
            // Add art to a separate copy of the sandbox without touching the original samples.
            var scene = EditorSceneManager.OpenScene(Sandbox, OpenSceneMode.Single);
            var art = EditorSceneManager.OpenScene(ArtPreview, OpenSceneMode.Additive);
            var retained = art.GetRootGameObjects().Where(root => root.name == "VirtualOperatingRoom" || root.name == "OperatingTheatre" || root.name == "PatientRoot").ToArray();
            foreach (var root in retained)
            {
                if (root.name == "OperatingTheatre") root.name = "VirtualOperatingRoom";
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
                root.transform.position += new Vector3(0, 0, 2.2f);
            }
            EditorSceneManager.CloseScene(art, true);
            UnityEngine.SceneManagement.SceneManager.SetActiveScene(scene);
            var oldCamera = scene.GetRootGameObjects().First(root => root.name == "DesktopPreviewCamera");
            UnityEngine.Object.DestroyImmediate(oldCamera);
            var origin = scene.GetRootGameObjects().Single(root => root.name == "TrackingOrigin").transform;
            var camera = new GameObject("TrackedHeadCamera").AddComponent<Camera>();
            camera.transform.SetParent(origin, false);
            camera.gameObject.tag = "MainCamera";
            camera.stereoTargetEye = StereoTargetEyeMask.Both;
            camera.nearClipPlane = 0.025f; camera.farClipPlane = 30;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.12f, 0.15f, 0.19f);
            camera.gameObject.AddComponent<AudioListener>();
            var runtime = origin.gameObject.AddComponent<NativeWorkbench>();
            runtime.trackingOrigin = origin; runtime.headCamera = camera;
            runtime.inputs = origin.GetComponentsInChildren<XRInstrumentInput>();
            runtime.tools = UnityEngine.Object.FindObjectsByType<InstrumentBehaviour>(FindObjectsSortMode.None);
            runtime.targets = UnityEngine.Object.FindObjectsByType<TrainingTarget>(FindObjectsSortMode.None);
            foreach (var target in runtime.targets)
            {
                target.SetRegistrationValid(false);
                PrefabUtility.RecordPrefabInstancePropertyModifications(target);
            }
            foreach (var input in runtime.inputs)
            {
                input.enabled = false;
                NativeControllerHands.Install(input);
            }
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "ToolCatchFloor";
            // Top face at y = 0, the room floor and tracked floor height, so dropped tools rest on the visible floor.
            floor.transform.SetPositionAndRotation(new Vector3(0, -0.05f, 0), Quaternion.identity);
            floor.transform.localScale = new Vector3(10, 0.1f, 10);
            floor.GetComponent<Renderer>().enabled = false;
            var text = new GameObject("HardwareTestInstructions").AddComponent<TextMesh>();
            text.transform.position = new Vector3(0, 1.5f, 0.6f);
            text.anchor = TextAnchor.MiddleCenter; text.alignment = TextAlignment.Center;
            text.characterSize = 0.015f; text.fontSize = 48;
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.GetComponent<Renderer>().sharedMaterial = text.font.material;
            text.text = "Scalpal | Native tool test\nGrip: pick up / release   Trigger: use tool\nA: reset tools and practice patch";
            runtime.status = text;
            RenderSettings.ambientMode = AmbientMode.Flat; RenderSettings.ambientLight = new Color(0.6f, 0.65f, 0.7f);
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
            Validate();
            Debug.Log("SCALPAL_NATIVE_PREPARED");
        }

        public static void Configure()
        {
            Directory.CreateDirectory("Assets/XR/Settings"); AssetDatabase.Refresh();
            if (!EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.k_SettingsKey, out XRGeneralSettingsPerBuildTarget store))
            {
                store = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
                AssetDatabase.CreateAsset(store, "Assets/XR/Settings/XRGeneralSettings.asset");
                EditorBuildSettings.AddConfigObject(XRGeneralSettings.k_SettingsKey, store, true);
            }
            if (!store.HasSettingsForBuildTarget(BuildTargetGroup.Android)) store.CreateDefaultSettingsForBuildTarget(BuildTargetGroup.Android);
            var general = store.SettingsForBuildTarget(BuildTargetGroup.Android);
            if (general.Manager == null)
            {
                var manager = ScriptableObject.CreateInstance<XRManagerSettings>();
                manager.name = "Android XR Manager";
                AssetDatabase.AddObjectToAsset(manager, store); general.Manager = manager;
            }
            general.InitManagerOnStart = true;
            if (!XRPackageMetadataStore.AssignLoader(general.Manager, "UnityEngine.XR.OpenXR.OpenXRLoader", BuildTargetGroup.Android))
                throw new InvalidOperationException("Could not assign the Android OpenXR loader.");
            FeatureHelpers.RefreshFeatures(BuildTargetGroup.Android);
            var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
            settings.GetFeature<MetaQuestFeature>().enabled = true;
            settings.GetFeature<OculusTouchControllerProfile>().enabled = true;
            settings.renderMode = OpenXRSettings.RenderMode.SinglePassInstanced;
            EditorUtility.SetDirty(store); EditorUtility.SetDirty(general); EditorUtility.SetDirty(general.Manager); EditorUtility.SetDirty(settings);
            PlayerSettings.SetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.Android, PackageId);
            PlayerSettings.productName = "Scalpal Native Workbench";
            PlayerSettings.bundleVersion = "0.1.1-native";
            PlayerSettings.Android.bundleVersionCode = 2;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel32;
            PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevel34;
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.SetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan });
            PlayerSettings.insecureHttpOption = InsecureHttpOption.NotAllowed;
        }

        public static void Validate()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var runtime = UnityEngine.Object.FindFirstObjectByType<NativeWorkbench>();
            if (!runtime || !runtime.headCamera || !runtime.trackingOrigin || !runtime.status || runtime.tools.Length != 15 || runtime.targets.Length != 1)
                throw new InvalidOperationException("Native scene bindings missing.");
            if (runtime.headCamera.transform.parent != runtime.trackingOrigin || runtime.inputs.Length != 2 || runtime.inputs.Any(input => input.trackingOrigin != runtime.trackingOrigin || input.enabled))
                throw new InvalidOperationException("Input must use one shared origin and start gated.");
            if (runtime.targets.Any(target => target.RegistrationValid) || runtime.tools.Select(tool => tool.instrumentId).Distinct().Count() != 15)
                throw new InvalidOperationException("Invalid initial registration or duplicate tools.");
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.GetComponentsInChildren<Component>(true).Any(component => component == null)) throw new InvalidOperationException("Missing script in native scene.");
                if (root.GetComponentsInChildren<Renderer>(true).Any(renderer => renderer.sharedMaterials.Any(material => !material || !material.shader)))
                    throw new InvalidOperationException("Missing native scene material.");
            }
            var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
            if (!settings.GetFeature<MetaQuestFeature>().enabled || !settings.GetFeature<OculusTouchControllerProfile>().enabled)
                throw new InvalidOperationException("Quest / controller features missing.");
            // Regression: carrying a kinematic patch must not strand it after reset.
            var patch = runtime.targets[0];
            var rest = patch.transform.position;
            var rotation = patch.transform.rotation;
            var parent = patch.transform.parent;
            runtime.SendMessage("Awake");
            patch.transform.SetPositionAndRotation(rest + Vector3.one * 4, Quaternion.Euler(40, 60, 80));
            runtime.ResetWorkbench();
            if ((patch.transform.position - rest).sqrMagnitude > 0.000001f || Quaternion.Angle(patch.transform.rotation, rotation) > 0.001f || patch.transform.parent != parent)
                throw new InvalidOperationException("Reset must restore a moved patch's pose and parent.");
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            Debug.Log("SCALPAL_NATIVE_SCENE_VALIDATED tools=15 controllers=2 targets=1 initialValidity=false");
        }

        public static void Build()
        {
            var output = Environment.GetEnvironmentVariable("SCALPAL_QUEST_APK");
            if (string.IsNullOrWhiteSpace(output) || !Path.IsPathRooted(output)) throw new InvalidOperationException("SCALPAL_QUEST_APK must be an absolute output path.");
            Configure();
            Validate();
            Scalpal.Instruments.Editor.InstrumentRuntimeValidation.Run();
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            EditorUserBuildSettings.buildAppBundle = false;
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = new[] { ScenePath }, locationPathName = output, target = BuildTarget.Android, options = BuildOptions.Development });
            if (report.summary.result != BuildResult.Succeeded) throw new InvalidOperationException("Native build failed: " + report.summary.result);
            Debug.Log("SCALPAL_NATIVE_BUILD_OK bytes=" + report.summary.totalSize);
        }
    }
}
