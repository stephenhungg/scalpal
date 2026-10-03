// Editor check for imported anatomy. Select the anatomy root (the object that sits at the torso
// root: umbilicus origin, +Z toward the head, +Y out of the abdomen) and run
// Scalpal > Validate Selected Anatomy Rig. It reports missing or misnamed meshes, missing colliders,
// flipped axes, and a triangle count over the Quest budget.
using System.Collections.Generic;
using System.Linq;
using Scalpal.Exercises.Generated;
using UnityEditor;
using UnityEngine;

namespace Scalpal.Exercises.EditorTools
{
    public static class ScalpalAssetValidator
    {
        const int TriangleBudget = 150000;

        // Landmarks whose side of the torso is unambiguous; a wrong sign means an axis is flipped.
        // x < 0 is the participant's right, z > 0 is toward the head.
        static readonly (string name, int xSign, int zSign)[] Landmarks =
        {
            (AnatomyUnityNames.Liver, -1, 1),
            (AnatomyUnityNames.Gallbladder, -1, 1),
            (AnatomyUnityNames.Cecum, -1, -1),
            (AnatomyUnityNames.Appendix, -1, -1),
            (AnatomyUnityNames.SigmoidColon, 1, -1),
            (AnatomyUnityNames.UrinaryBladder, 0, -1),
            (AnatomyUnityNames.Heart, 0, 1),
        };

        [MenuItem("Scalpal/Validate Selected Anatomy Rig")]
        public static void ValidateSelection()
        {
            var root = Selection.activeGameObject;
            if (root == null)
            {
                Debug.LogError("[Scalpal] Select the anatomy root first.");
                return;
            }
            var problems = Validate(root.transform);
            if (problems.Count == 0) Debug.Log($"[Scalpal] {root.name}: anatomy rig ok ({AnatomyUnityNames.All.Length} structures).");
            else Debug.LogError($"[Scalpal] {root.name}: {problems.Count} problem(s)\n- " + string.Join("\n- ", problems));
        }

        public static List<string> Validate(Transform root)
        {
            var problems = new List<string>();
            var byName = new Dictionary<string, Transform>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (!t.name.StartsWith("anat_")) continue;
                if (byName.ContainsKey(t.name)) problems.Add($"duplicate object {t.name}");
                else byName[t.name] = t;
            }

            foreach (var name in AnatomyUnityNames.All)
            {
                if (!byName.TryGetValue(name, out var t)) problems.Add($"missing {name}");
                else if (t.GetComponent<Collider>() == null) problems.Add($"{name} has no Collider, so instrument touches cannot register");
            }
            var known = new HashSet<string>(AnatomyUnityNames.All);
            foreach (var extra in byName.Keys.Where(n => !known.Contains(n))) problems.Add($"{extra} is not in the catalog (typo, or add it to services/preop/src/catalog/anatomy.ts)");

            foreach (var (name, xSign, zSign) in Landmarks)
            {
                if (!byName.TryGetValue(name, out var t)) continue;
                var renderer = t.GetComponent<Renderer>();
                var local = root.InverseTransformPoint(renderer != null ? renderer.bounds.center : t.position);
                if (xSign != 0 && Mathf.Sign(local.x) != xSign) problems.Add($"{name} is on the wrong side (x = {local.x:F2}); the rig is mirrored or the X axis is flipped");
                if (zSign != 0 && Mathf.Sign(local.z) != zSign) problems.Add($"{name} is on the wrong end (z = {local.z:F2}); +Z must point toward the head");
            }

            var triangles = root.GetComponentsInChildren<MeshFilter>(true)
                .Where(f => f.sharedMesh != null)
                .Sum(f => f.sharedMesh.triangles.Length / 3);
            if (triangles > TriangleBudget) problems.Add($"{triangles} triangles exceeds the {TriangleBudget} Quest budget for anatomy; decimate in Blender");
            return problems;
        }
    }
}
