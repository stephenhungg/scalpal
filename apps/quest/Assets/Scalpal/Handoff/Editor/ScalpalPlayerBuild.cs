using System;
using System.IO;
using System.Linq;
using Meta.XR;
using Scalpal.EncounterOffice.Editor;
using Scalpal.Quest.Editor;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features.CompositionLayers;
using UnityEngine.XR.OpenXR.Features.Meta;

namespace Scalpal.Handoff.Editor
{
    /// <summary>One Android player; the standalone component builders remain available.</summary>
    public static class ScalpalPlayerBuild
    {
        public const string ReservedShellScene = "Assets/Scalpal/Shell/Scenes/ScalpalShell.unity";
        const string ShellRoot = "Assets/Scalpal/Shell";

        public static EditorBuildSettingsScene[] PlayerScenes()
        {
            string shell = ResolveShellScene();
            return new[] {
                new EditorBuildSettingsScene(shell ?? ReservedShellScene, shell != null),
                new EditorBuildSettingsScene(EncounterOfficeBuild.ScenePath, true),
                new EditorBuildSettingsScene(NativeSessionBuild.ScenePath, true),
                new EditorBuildSettingsScene("Assets/Scalpal/Recap/Scenes/RunEnding.unity", true)
            };
        }

        static string ResolveShellScene()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ReservedShellScene)) return ReservedShellScene;
            if (!AssetDatabase.IsValidFolder(ShellRoot)) return null;
            var scenes = AssetDatabase.FindAssets("t:Scene", new[] { ShellRoot })
                .Select(AssetDatabase.GUIDToAssetPath).OrderBy(path => path, StringComparer.Ordinal).ToArray();
            if (scenes.Length == 0) return null;
            if (scenes.Length == 1) return scenes[0];
            // Preserve the shell owner's explicitly selected startup scene when there are previews too.
            string configured = EditorBuildSettings.scenes.FirstOrDefault(scene => scene.enabled
                && scenes.Contains(scene.path))?.path;
            if (!string.IsNullOrEmpty(configured)) return configured;
            throw new InvalidOperationException("Several Shell scenes exist. Select the launch scene first in Build Settings.");
        }

        [MenuItem("Scalpal/Player/Prepare Unified Android Player")]
        public static void Prepare()
        {
            HandoffCardPreview.PrepareResources();
            string version = PlayerSettings.bundleVersion;
            int versionCode = PlayerSettings.Android.bundleVersionCode;
            NativeQuestBuild.Configure();
            // NativeSession uses this same package; keep its private development configuration on upgrade.
            PlayerSettings.productName = "Scalpal";
            PlayerSettings.bundleVersion = version;
            PlayerSettings.Android.bundleVersionCode = versionCode;
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.insecureHttpOption = InsecureHttpOption.DevelopmentOnly;
            var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
            settings.GetFeature<ARSessionFeature>().enabled = true;
            settings.GetFeature<ARCameraFeature>().enabled = true;
            settings.GetFeature<OpenXRCompositionLayersFeature>().enabled = true;
            settings.GetFeature<MetaXRFeature>().enabled = true;
            EditorUtility.SetDirty(settings);
            var meta = OVRProjectConfig.CachedProjectConfig;
            meta.insightPassthroughSupport = OVRProjectConfig.FeatureSupport.Supported;
            meta.isPassthroughCameraAccessEnabled = true;
            meta.sceneSupport = OVRProjectConfig.FeatureSupport.Supported;
            OVRProjectConfig.CommitProjectConfig(meta);
            EditorBuildSettings.scenes = PlayerScenes();
            if (!EditorBuildSettings.scenes[0].enabled)
                Debug.LogWarning("SCALPAL_SHELL_PENDING: reserved disabled first slot; this player launches DiagnosisOffice until the Shell scene lands.");
            AssetDatabase.SaveAssets();
            ValidateSceneOrder();
            Debug.Log("SCALPAL_PLAYER_PREPARED package=" + NativeQuestBuild.PackageId);
        }

        // The product order, written independently of PlayerScenes() so a wrong generator cannot validate itself.
        static readonly string[] RequiredAfterShell = {
            "Assets/Scalpal/EncounterOffice/Scenes/DiagnosisOffice.unity",
            "Assets/Scalpal/Quest/Scenes/NativeSession.unity",
            "Assets/Scalpal/Recap/Scenes/RunEnding.unity"
        };

        [MenuItem("Scalpal/Player/Validate Unified Scene Order")]
        public static void ValidateSceneOrder() => ValidateSceneOrder(EditorBuildSettings.scenes);

        public static void ValidateSceneOrder(EditorBuildSettingsScene[] actual)
        {
            const string order = "Unified scene order must be Shell (reserved if absent), DiagnosisOffice, NativeSession, RunEnding.";
            if (actual == null || actual.Length != RequiredAfterShell.Length + 1 || !actual[0].path.StartsWith(ShellRoot + "/", StringComparison.Ordinal)
                || !actual.Skip(1).Select(scene => scene.path).SequenceEqual(RequiredAfterShell) || actual.Skip(1).Any(scene => !scene.enabled))
                throw new InvalidOperationException(order);
            bool shellExists = AssetDatabase.IsValidFolder(ShellRoot) && AssetDatabase.FindAssets("t:Scene", new[] { ShellRoot }).Length > 0;
            if (shellExists && (!actual[0].enabled || !AssetDatabase.LoadAssetAtPath<SceneAsset>(actual[0].path)))
                throw new InvalidOperationException("The Shell launch scene exists, so it must be the enabled first scene. " + order);
            foreach (var scene in actual.Where(scene => scene.enabled))
                if (!AssetDatabase.LoadAssetAtPath<SceneAsset>(scene.path))
                    throw new InvalidOperationException("Unified player scene missing: " + scene.path);
            Debug.Log("SCALPAL_PLAYER_SCENES_OK scenes=" + string.Join(",", actual.Where(scene => scene.enabled).Select(scene => scene.path)));
        }

        [MenuItem("Scalpal/Player/Build Unified Android Player")]
        public static void Build()
        {
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
                throw new InvalidOperationException("Launch Unity with -buildTarget Android before invoking the unified Android build.");
            string output = Environment.GetEnvironmentVariable("SCALPAL_QUEST_APK");
            if (string.IsNullOrWhiteSpace(output) || !Path.IsPathRooted(output))
                throw new InvalidOperationException("SCALPAL_QUEST_APK must be an absolute output path.");
            Prepare();
            EncounterOfficeBuild.Verify();
            Scalpal.Shell.Editor.ShellValidation.Run();
            NativeSessionBuild.Verify();
            HandoffValidation.Verify();
            ValidateSceneOrder();
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            EditorUserBuildSettings.buildAppBundle = false;
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
                scenes = EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray(),
                locationPathName = output, target = BuildTarget.Android, options = BuildOptions.Development
            });
            if (report.summary.result != BuildResult.Succeeded)
                throw new InvalidOperationException("Unified Android build failed: " + report.summary.result);
            Debug.Log("SCALPAL_PLAYER_BUILD_OK bytes=" + report.summary.totalSize + " package=" + NativeQuestBuild.PackageId);
        }
    }
}
