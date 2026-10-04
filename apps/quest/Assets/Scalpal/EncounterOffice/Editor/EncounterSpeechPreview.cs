using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Scalpal.Voice;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Scalpal.EncounterOffice.Editor
{
    // Deterministic app capture: authored PCM -> production playback callback -> production donor bone poses.
    // No microphone, network connection, or audible Editor playback is started by this capture.
    public static class EncounterSpeechPreview
    {
        const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

        public static void Capture()
        {
            string output = Environment.GetEnvironmentVariable("SCALPAL_SPEECH_PREVIEW");
            if (string.IsNullOrWhiteSpace(output) || !Path.IsPathRooted(output))
                throw new InvalidOperationException("SCALPAL_SPEECH_PREVIEW must be an absolute frame directory.");
            Directory.CreateDirectory(output);
            EditorSceneManager.OpenScene(EncounterOfficeBuild.ScenePath, OpenSceneMode.Single);
            var session = UnityEngine.Object.FindFirstObjectByType<NativeEncounterSession>();
            var patient = session.patient;
            patient.Select(new EncounterState { patientId = EncounterContract.FemalePatientId, patientName = "Priya Ramaswamy",
                speaker = "patient", patientSex = "female", patientAge = 40, speakerName = "Priya Ramaswamy", speakerSex = "female", speakerAge = 40 });
            patient.SetState("speaking");
            patient.stateLabel.gameObject.SetActive(false);
            var clip = Resources.Load<AudioClip>("EncounterSpeech/Priya/greeting");
            if (!clip || clip.channels != 1 || clip.frequency != 24000)
                throw new InvalidOperationException("Preview requires the authored mono 24 kHz greeting.");
            var pcm = new float[clip.samples];
            if (!clip.GetData(pcm, 0)) throw new InvalidOperationException("Cannot read preview PCM.");
            File.Copy(Path.GetFullPath(AssetDatabase.GetAssetPath(clip)), Path.Combine(output, "speech.wav"), true);
            var queue = (Queue<float>)typeof(QuestJarvisVoice).GetField("outputSamples", Private).GetValue(session.voice);
            var read = typeof(QuestJarvisVoice).GetMethod("ReadAudio", Private);
            var animate = typeof(EncounterPatientPresentation).GetMethod("ApplyAnimation", Private);
            typeof(QuestJarvisVoice).GetField("localSpeech", Private).SetValue(session.voice, true);
            var head = patient.female.GetComponentsInChildren<Transform>(true).Single(node => node.name == "HeadPivot");
            var camera = UnityEngine.Object.FindFirstObjectByType<EncounterOfficeRig>().head;
            camera.stereoTargetEye = StereoTargetEyeMask.None;
            camera.transform.position = head.position + new Vector3(0, .02f, .95f);
            camera.transform.LookAt(head.position + new Vector3(0, -.04f, 0));
            camera.fieldOfView = 38;
            var render = new RenderTexture(1280, 720, 24);
            var previous = RenderTexture.active;
            var texture = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            const int fps = 24, samplesPerFrame = 24000 / fps;
            int frames = Mathf.CeilToInt((clip.length + .5f) * fps);
            float highest = 0;
            var skins = patient.female.GetComponentsInChildren<SkinnedMeshRenderer>();
            var snapshots = new Mesh[skins.Length];
            var snapshotObjects = new GameObject[skins.Length];
            try
            {
                // Camera.Render in batch Edit mode does not run the player skinning update.
                // Render CPU snapshots of the same production skinned meshes for each captured pose.
                for (int i = 0; i < skins.Length; i++)
                {
                    snapshots[i] = new Mesh();
                    skins[i].BakeMesh(snapshots[i], true);
                    snapshotObjects[i] = new GameObject("EditorSkinSnapshot");
                    snapshotObjects[i].transform.SetParent(skins[i].transform, false);
                    snapshotObjects[i].AddComponent<MeshFilter>().sharedMesh = snapshots[i];
                    snapshotObjects[i].AddComponent<MeshRenderer>().sharedMaterials = skins[i].sharedMaterials;
                    skins[i].enabled = false;
                }
                camera.targetTexture = render;
                camera.Render(); // Warm mesh upload/culling before the first recorded frame.
                for (int frame = 0; frame < frames; frame++)
                {
                    queue.Clear();
                    for (int sample = frame * samplesPerFrame; sample < Math.Min((frame + 1) * samplesPerFrame, pcm.Length); sample++)
                        queue.Enqueue(pcm[sample]);
                    var block = new float[samplesPerFrame];
                    read.Invoke(session.voice, new object[] { block });
                    float level = session.voice.PlaybackLevel;
                    animate.Invoke(patient, new object[] { 1f / fps, frame / (float)fps, level, true });
                    for (int i = 0; i < skins.Length; i++) skins[i].BakeMesh(snapshots[i], true);
                    camera.Render();
                    RenderTexture.active = render;
                    texture.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0); texture.Apply();
                    var png = texture.EncodeToPNG();
                    File.WriteAllBytes(Path.Combine(output, "frame-" + frame.ToString("D4") + ".png"), png);
                    if (frame == 0) File.WriteAllBytes(Path.Combine(output, "rest.png"), png);
                    if (level > highest) { highest = level; File.WriteAllBytes(Path.Combine(output, "speaking.png"), png); }
                }
                Debug.Log("SCALPAL_SPEECH_PREVIEW_OK frames=" + frames + " fps=" + fps + " peakEnvelope=" + highest + " monoEditorOnly=true");
            }
            finally
            {
                session.voice.Disconnect();
                for (int i = 0; i < skins.Length; i++)
                {
                    skins[i].enabled = true;
                    if (snapshotObjects[i]) UnityEngine.Object.DestroyImmediate(snapshotObjects[i]);
                    if (snapshots[i]) UnityEngine.Object.DestroyImmediate(snapshots[i]);
                }
                camera.targetTexture = null; RenderTexture.active = previous;
                UnityEngine.Object.DestroyImmediate(texture); UnityEngine.Object.DestroyImmediate(render);
            }
        }
    }
}
