// Editor check for imported anatomy. Select the anatomy root (the object that sits at the torso
// root: umbilicus origin, +Z toward the head, +Y out of the abdomen) and run
// Scalpal > Validate Selected Anatomy Rig. It reports missing or misnamed meshes, missing colliders,
// flipped axes, and a triangle count over the Quest budget.
using System.Collections.Generic;
using System.Linq;
using Scalpal.Anatomy;
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
            (AnatomyIds.Liver, -1, 1),
            (AnatomyIds.Gallbladder, -1, 1),
            (AnatomyIds.Cecum, -1, -1),
            (AnatomyIds.Appendix, -1, -1),
            (AnatomyIds.SigmoidColon, 1, -1),
            (AnatomyIds.UrinaryBladder, 0, -1),
            (AnatomyIds.Heart, 0, 1),
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
            if (problems.Count == 0) Debug.Log($"[Scalpal] {root.name}: anatomy rig ok ({AnatomyIds.All.Length} structures).");
            else Debug.LogError($"[Scalpal] {root.name}: {problems.Count} problem(s)\n- " + string.Join("\n- ", problems));
        }

        // Structures are found by AnatomyPart.stableId (the Codex atlas convention), falling back to
        // GameObjects named anat_<id> for hand-built placeholder rigs.
        public static List<string> Validate(Transform root)
        {
            var problems = new List<string>();
            var byId = new Dictionary<string, Transform>();
            foreach (var part in root.GetComponentsInChildren<AnatomyPart>(true))
            {
                if (string.IsNullOrEmpty(part.stableId)) continue;
                if (byId.ContainsKey(part.stableId)) problems.Add($"duplicate stableId {part.stableId}");
                else byId[part.stableId] = part.transform;
            }
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (!t.name.StartsWith("anat_")) continue;
                var id = t.name.Substring(5);
                if (!byId.ContainsKey(id)) byId[id] = t;
            }

            foreach (var id in AnatomyIds.All)
            {
                if (!byId.TryGetValue(id, out var t)) problems.Add($"missing {id} (no AnatomyPart with that stableId)");
                else if (t.GetComponentsInChildren<Collider>(true).Length == 0) problems.Add($"{id} has no Collider, so instrument touches cannot register");
            }

            foreach (var (name, xSign, zSign) in Landmarks)
            {
                if (!byId.TryGetValue(name, out var t)) continue;
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
