using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

// Builds the supplied scenes without changing camera, inference or XR behavior.
public static class QuestProbeBuild
{
    public static void Build()
    {
        var output = Environment.GetEnvironmentVariable("QUEST_PROBE_APK");
        if (string.IsNullOrWhiteSpace(output) || !Path.IsPathRooted(output))
            throw new InvalidOperationException("QUEST_PROBE_APK must name an absolute APK output path.");

        var scenes = EditorBuildSettings.scenes.Where(scene => scene.enabled)
            .Select(scene => scene.path).ToArray();
        if (scenes.Length == 0 || Path.GetFileNameWithoutExtension(scenes[0]) != "StartScene")
            throw new InvalidOperationException("StartScene must remain first for the sample permission flow.");

        Directory.CreateDirectory(Path.GetDirectoryName(output));
        EditorUserBuildSettings.buildAppBundle = false;
        // Match the installed API 34 platform and the connected Android 14 headset.
        PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevel34;
        // The development-only diagnostic talks to localhost through the USB reverse tunnel.
        PlayerSettings.insecureHttpOption = InsecureHttpOption.DevelopmentOnly;
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = output,
            target = BuildTarget.Android,
            options = BuildOptions.Development
        });
        if (report.summary.result != BuildResult.Succeeded)
            throw new InvalidOperationException($"Quest probe build failed: {report.summary.result}; {report.summary.totalErrors} errors.");

        Debug.Log($"QUEST_PROBE_BUILD_OK path={output} bytes={report.summary.totalSize}");
    }
}
