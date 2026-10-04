using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Scalpal.EncounterOffice.Editor
{
    /// <summary>Fails the office gate when the committed bake is missing, stale or would cost realtime lighting on Quest.</summary>
    public static class EncounterOfficeLightingValidation
    {
        static int checks;
        public static void Run()
        {
            checks = 0;
            EditorSceneManager.OpenScene(EncounterOfficeBuild.ScenePath, OpenSceneMode.Single);
            Check(Lightmapping.lightingDataAsset, "office scene references committed baked lighting data");
            var settings = Lightmapping.lightingSettings;
            Check(settings && settings.bakedGI && !settings.realtimeGI && settings.ao && settings.lightmapCompression != LightmapCompression.None,
                "office uses compressed baked GI with ambient occlusion and no realtime GI");

            var maps = LightmapSettings.lightmaps;
            Check(maps.Length >= 1 && maps.Length <= 2, "office bakes one or two lightmap atlases (found " + maps.Length + ")");
            Check(maps.All(map => map.lightmapColor && map.lightmapColor.width <= EncounterOfficeLighting.MaxAtlasSize && map.lightmapColor.height <= EncounterOfficeLighting.MaxAtlasSize),
                "every lightmap atlas exists and is at most " + EncounterOfficeLighting.MaxAtlasSize + "px");

            var probes = LightmapSettings.lightProbes;
            Check(probes && probes.count >= 40 && UnityEngine.Object.FindFirstObjectByType<LightProbeGroup>(), "baked light probes exist for dynamic patients (found " + (probes ? probes.count : 0) + ")");
            var reflection = UnityEngine.Object.FindObjectsByType<ReflectionProbe>(FindObjectsSortMode.None);
            Check(reflection.Length == 1 && reflection[0].mode == ReflectionProbeMode.Baked && reflection[0].bakedTexture, "exactly one baked reflection probe has a cubemap");

            var lights = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            Check(lights.Length >= 3 && lights.All(light => light.lightmapBakeType == LightmapBakeType.Baked),
                "office lights are all fully baked so Quest renders no realtime lights or shadow maps");

            var statics = UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(renderer => GameObjectUtility.GetStaticEditorFlags(renderer.gameObject).HasFlag(StaticEditorFlags.ContributeGI)).ToArray();
            Check(statics.Length >= 15, "office art is marked static for the bake (found " + statics.Length + ")");
            foreach (var renderer in statics)
            {
                var filter = renderer.GetComponent<MeshFilter>(); var mesh = filter ? filter.sharedMesh : null;
                Check(mesh && mesh.HasVertexAttribute(VertexAttribute.TexCoord1), "static renderer has lightmap UVs: " + renderer.name);
                if (renderer.receiveGI == ReceiveGI.Lightmaps)
                    Check(CollapsedLightmapArea(mesh) < .002f, "lightmap UV charts are not collapsed into black slivers: " + renderer.name);
                if (renderer.receiveGI == ReceiveGI.Lightmaps)
                    Check(renderer.lightmapIndex >= 0 && renderer.lightmapIndex < maps.Length && renderer.lightmapScaleOffset.x > 0,
                        "lightmapped static renderer is in a baked atlas: " + renderer.name);
            }
            foreach (string name in new[] { "Body_Office_Floor", "Body_Office_WallWarm", "Body_Office_Upholstery", "BakedCeiling" })
                Check(statics.Any(renderer => renderer.name == name && renderer.receiveGI == ReceiveGI.Lightmaps), "large room surface is lightmapped: " + name);

            var patients = UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(t => t.name == "GenericAdultFemaleTemplate" || t.name == "GenericAdultMaleTemplate").ToArray();
            Check(patients.Length == 2, "both patient templates are present for probe lighting");
            foreach (var renderer in patients.SelectMany(p => p.GetComponentsInChildren<Renderer>(true)))
                Check(GameObjectUtility.GetStaticEditorFlags(renderer.gameObject) == 0 && renderer.lightProbeUsage == LightProbeUsage.BlendProbes,
                    "dynamic patient renderer is lit by baked probes: " + renderer.name);

            Debug.Log("SCALPAL_ENCOUNTER_LIGHTING_VERIFY_OK checks=" + checks + " atlases=" + string.Join(",", maps.Select(map => map.lightmapColor.width + "x" + map.lightmapColor.height + ":" + map.lightmapColor.format))
                + " probes=" + probes.count + " lights=" + lights.Length + " realtimeLights=0");
        }
        /// <summary>Fraction of surface whose lightmap texel density is under 5% of the mesh mean (Unity's unwrapper did this to the window frame).</summary>
        static float CollapsedLightmapArea(Mesh mesh)
        {
            var vertices = mesh.vertices; var uv2 = mesh.uv2; var triangles = mesh.triangles;
            int count = triangles.Length / 3; var world = new float[count]; var density = new float[count];
            float totalWorld = 0, totalUv = 0;
            for (int i = 0; i < count; i++)
            {
                int a = triangles[i * 3], b = triangles[i * 3 + 1], c = triangles[i * 3 + 2];
                world[i] = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]).magnitude;
                Vector2 u = uv2[b] - uv2[a], v = uv2[c] - uv2[a]; float uvArea = Mathf.Abs(u.x * v.y - u.y * v.x);
                density[i] = world[i] > 0 ? uvArea / world[i] : 0; totalWorld += world[i]; totalUv += uvArea;
            }
            float mean = totalUv / Mathf.Max(totalWorld, 1e-12f), collapsed = 0;
            for (int i = 0; i < count; i++) if (density[i] < mean * .05f) collapsed += world[i];
            return collapsed / Mathf.Max(totalWorld, 1e-12f);
        }
        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Office lighting validation failed: " + message);
            checks++;
        }
    }
}
