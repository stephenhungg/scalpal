using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Scalpal.Quest.Editor
{
    // Source-geometry evidence only. No participant observations, scene changes or runtime dependencies.
    public static class NativeBodyAtlasValidation
    {
        const string Models = "Assets/Scalpal/Anatomy/Models/";
        const float BoundsTolerance = 0.0015f;
        static int checks;

        // Original high-resolution humeral/femoral head sphere fits. These are authored
        // joint proxies, not stored source landmarks or validated MediaPipe correspondences.
        public static readonly Vector3 LeftShoulder = new Vector3(0.167503f, 1.382441f, 0.026002f);
        public static readonly Vector3 RightShoulder = new Vector3(-0.167503f, 1.382441f, 0.026002f);
        public static readonly Vector3 LeftHip = new Vector3(0.084307f, 0.860876f, 0.004882f);
        public static readonly Vector3 RightHip = new Vector3(-0.084307f, 0.860876f, 0.004882f);
        public static readonly Vector3 Umbilicus = new Vector3(0f, 1.017768f, -0.115287f);

        [MenuItem("Scalpal/Quest/Validate Native Body Atlas Axes")]
        public static void Run()
        {
            checks = 0;
            var skeletal = Index("skeletal");
            var surface = Index("surface");
            // Bounds were measured by reimporting the checked-in prepared FBXs into
            // Blender, then converting B=(X,Y,Z) to expected Unity U=(X,Z,Y).
            // Comparing against actual imported meshes catches reflection, axis/unit
            // changes and discarded source transforms, rather than trusting metadata.
            CheckBounds(skeletal, "atlas_skeletal__humerus_l", new Vector3(.143688f, 1.086809f, .001645f), new Vector3(.249789f, 1.405961f, .053940f));
            CheckBounds(skeletal, "atlas_skeletal__humerus_r", new Vector3(-.249789f, 1.086809f, .001645f), new Vector3(-.143688f, 1.405961f, .053940f));
            CheckBounds(skeletal, "atlas_skeletal__femur_l", new Vector3(.034072f, .430577f, -.019184f), new Vector3(.147251f, .883247f, .060048f));
            CheckBounds(skeletal, "atlas_skeletal__femur_r", new Vector3(-.147251f, .430577f, -.019184f), new Vector3(-.034067f, .883247f, .060048f));
            CheckBounds(surface, "atlas_surface__deltoid_region_l", new Vector3(.134622f, 1.272989f, -.032216f), new Vector3(.242427f, 1.436042f, .102037f));
            CheckBounds(surface, "atlas_surface__deltoid_region_r", new Vector3(-.241763f, 1.272942f, -.031059f), new Vector3(-.134648f, 1.435463f, .102037f));
            CheckBounds(surface, "atlas_surface__hip_region_l", new Vector3(.150567f, .794971f, -.037917f), new Vector3(.180395f, .903412f, .068687f));
            CheckBounds(surface, "atlas_surface__hip_region_r", new Vector3(-.180285f, .794922f, -.037999f), new Vector3(-.150477f, .903206f, .068669f));
            var leftNavel = CheckBounds(surface, "atlas_surface__umbilicus_l", new Vector3(-.000100f, 1.004080f, -.121027f), new Vector3(.016015f, 1.029506f, -.106985f));
            var rightNavel = CheckBounds(surface, "atlas_surface__umbilicus_r", new Vector3(-.016015f, 1.004114f, -.121027f), new Vector3(.000319f, 1.029506f, -.106646f));
            Contains(skeletal["atlas_skeletal__humerus_l"], LeftShoulder, "left shoulder proxy");
            Contains(skeletal["atlas_skeletal__humerus_r"], RightShoulder, "right shoulder proxy");
            Contains(skeletal["atlas_skeletal__femur_l"], LeftHip, "left hip proxy");
            Contains(skeletal["atlas_skeletal__femur_r"], RightHip, "right hip proxy");
            var navelBounds = leftNavel; navelBounds.Encapsulate(rightNavel);
            Assert(navelBounds.Contains(Umbilicus), "authored umbilicus lies in imported paired skin patches");
            foreach (var side in new[] { "l", "r" })
            {
                Assert(WorldBounds(surface["atlas_surface__presternal_region_" + side]).max.z < -.05f,
                    "presternal skin proves anterior is negative source Z");
                Assert(WorldBounds(surface["atlas_surface__vertebral_region_" + side]).min.z > .08f,
                    "vertebral skin proves posterior is positive source Z");
            }

            var hipMid = (LeftHip + RightHip) * .5f;
            var shoulderMid = (LeftShoulder + RightShoulder) * .5f;
            var cranial = (shoulderMid - hipMid).normalized;
            var left = (LeftShoulder - RightShoulder).normalized;
            var anterior = Vector3.Cross(cranial, left).normalized;
            Assert(Vector3.Dot(cranial, Vector3.up) > .999f && Vector3.Dot(left, Vector3.right) > .999f
                && Vector3.Dot(anterior, Vector3.back) > .999f, "joint frame agrees with measured source axes");
            float length = Vector3.Distance(hipMid, shoulderMid);
            Assert(length > .520f && length < .524f, "source torso reference has metric scale");
            var torso = Quaternion.LookRotation(cranial, anterior);
            var navelInTorso = Quaternion.Inverse(torso) * (Umbilicus - hipMid);
            Debug.Log("[Scalpal body atlas] PASS " + checks + " imported-geometry checks. Unity atlas-root meters: LS="
                + LeftShoulder.ToString("F6") + " RS=" + RightShoulder.ToString("F6")
                + " LH=" + LeftHip.ToString("F6") + " RH=" + RightHip.ToString("F6")
                + " umbilicus=" + Umbilicus.ToString("F6") + "; hipMid=" + hipMid.ToString("F6")
                + " cranial=" + cranial.ToString("F6") + " anterior=" + anterior.ToString("F6")
                + " torsoLength=" + length.ToString("F6") + " shoulderWidth=" + Vector3.Distance(LeftShoulder, RightShoulder).ToString("F6")
                + " hipWidth=" + Vector3.Distance(LeftHip, RightHip).ToString("F6") + " umbilicusInTorso=" + navelInTorso.ToString("F6")
                + ". Original joint sphere-fit radial RMS: shoulder 0.93 mm, hip 0.82 mm; cut sensitivity approximately 1 mm."
                + " Bounds containment does not independently verify exact joint centers. No participant alignment or MediaPipe accuracy established.");
        }

        static Dictionary<string, MeshFilter> Index(string system)
        {
            string path = Models + system + ".fbx";
            ConfigureReferenceImport(path);
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert(model, "imported shared-frame " + system + " asset exists");
            var filters = model.GetComponentsInChildren<MeshFilter>(true);
            Assert(filters.Length > 0 && filters.All(f => f.sharedMesh), "source contains static imported meshes");
            Assert(filters.Select(f => f.name).Distinct(StringComparer.Ordinal).Count() == filters.Length, "source mesh labels are unique");
            return filters.ToDictionary(f => f.name, StringComparer.Ordinal);
        }

        static void ConfigureReferenceImport(string path)
        {
            // Keep these identical to AnatomyAtlasBuilder.ConfigureModelImport. The
            // sparse exercise builder never configures unused skeleton/skin sources.
            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer == null) throw new InvalidOperationException("Missing FBX model importer: " + path);
            if (importer.bakeAxisConversion && importer.useFileScale && importer.globalScale == 1f
                && !importer.importAnimation && importer.animationType == ModelImporterAnimationType.None
                && importer.materialImportMode == ModelImporterMaterialImportMode.None) return;
            importer.bakeAxisConversion = true;
            importer.useFileScale = true;
            importer.globalScale = 1f;
            importer.importAnimation = false;
            importer.animationType = ModelImporterAnimationType.None;
            importer.materialImportMode = ModelImporterMaterialImportMode.None;
            importer.SaveAndReimport();
        }

        static Bounds CheckBounds(Dictionary<string, MeshFilter> index, string name, Vector3 expectedMin, Vector3 expectedMax)
        {
            Assert(index.TryGetValue(name, out var filter), "source label exists: " + name);
            var actual = WorldBounds(filter);
            Assert(MaxAbs(actual.min - expectedMin) < BoundsTolerance && MaxAbs(actual.max - expectedMax) < BoundsTolerance,
                name + " expected imported axes/units/bounds; actual min=" + actual.min.ToString("F6") + " max=" + actual.max.ToString("F6"));
            Debug.Log("[Scalpal body atlas] " + name + " min=" + actual.min.ToString("F6") + " max=" + actual.max.ToString("F6"));
            return actual;
        }

        static void Contains(MeshFilter mesh, Vector3 proxy, string label)
        {
            var bounds = WorldBounds(mesh); bounds.Expand(BoundsTolerance * 2f);
            Assert(bounds.Contains(proxy), label + " is inside the corresponding imported bone bounds");
        }

        static Bounds WorldBounds(MeshFilter filter)
        {
            var local = filter.sharedMesh.bounds;
            var matrix = filter.transform.localToWorldMatrix;
            var result = new Bounds(matrix.MultiplyPoint3x4(local.min), Vector3.zero);
            for (int corner = 0; corner < 8; corner++)
                result.Encapsulate(matrix.MultiplyPoint3x4(new Vector3(
                    (corner & 1) == 0 ? local.min.x : local.max.x,
                    (corner & 2) == 0 ? local.min.y : local.max.y,
                    (corner & 4) == 0 ? local.min.z : local.max.z)));
            return result;
        }

        static float MaxAbs(Vector3 vector) => Mathf.Max(Mathf.Abs(vector.x), Mathf.Abs(vector.y), Mathf.Abs(vector.z));
        static void Assert(bool condition, string message)
        {
            checks++;
            if (!condition) throw new InvalidOperationException("Body atlas validation failed: " + message);
        }
    }
}
