using Scalpal.Brand;
using TMPro;
using UnityEngine;

namespace Scalpal.Briefing
{
    /// <summary>
    /// Laser identification on the briefing atlas: the pointed part gets a green box that follows its animated
    /// offset/scale/spin, a name label and a highlight. The merged mesh has no per-part colliders, so the hit test is
    /// BriefingAtlas.Raycast (per-part rest box, then that part's triangles under the inverse part transform).
    /// </summary>
    public sealed class BriefingPicker : MonoBehaviour
    {
        public static readonly Color BoxGreen = new Color32(64, 214, 128, 255);
        public const float MaximumDistance = 4, LineWidth = .0016f;
        public BriefingAtlas atlas;
        public Transform viewer;
        public int Hovered { get; private set; } = -1;
        public string HoveredId => atlas && Hovered >= 0 ? atlas.Parts[Hovered].id : "";
        public LineRenderer Box { get; private set; }
        public TextMeshPro Label { get; private set; }
        readonly Vector3[] corners = new Vector3[8];
        readonly Vector3[] path = new Vector3[16];
        // A continuous path over all 12 box edges (three edges are walked twice).
        static readonly int[] Walk = { 0, 1, 3, 2, 0, 4, 5, 7, 6, 4, 5, 1, 3, 7, 6, 2 };

        public static BriefingPicker Create(BriefingAtlas atlas, Transform parent, Transform viewer)
        {
            var picker = new GameObject("BriefingPicker").AddComponent<BriefingPicker>();
            picker.transform.SetParent(parent, false);
            picker.atlas = atlas; picker.viewer = viewer;
            var brand = ScalpalBrand.Active;
            var line = new GameObject("BriefingPartBox").AddComponent<LineRenderer>();
            line.transform.SetParent(picker.transform, false);
            line.sharedMaterial = brand ? brand.ray : null; line.useWorldSpace = true; line.positionCount = 16; line.loop = false;
            line.startWidth = line.endWidth = LineWidth; line.numCornerVertices = 0; line.numCapVertices = 0;
            line.startColor = line.endColor = BoxGreen;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; line.receiveShadows = false;
            line.enabled = false;
            picker.Box = line;
            if (brand)
            {
                picker.Label = brand.Text(picker.transform, "BriefingPartLabel", "", ScalpalTextRole.Label, Vector3.zero, .022f, .5f, .06f, TextAnchor.LowerCenter);
                picker.Label.color = ScalpalBrand.Ink;
                picker.Label.gameObject.SetActive(false);
            }
            return picker;
        }

        /// <summary>Nearest visible part hit by a world ray, or -1.</summary>
        public int Pick(Ray ray, out Vector3 point)
        {
            point = default;
            return atlas && atlas.Raycast(ray, MaximumDistance, out int part, out point) ? part : -1;
        }

        /// <summary>Identifies the part under the first ray that hits one; clears when none does.</summary>
        public int Track(Ray? first, Ray? second, out Vector3 point)
        {
            point = default;
            int part = first.HasValue ? Pick(first.Value, out point) : -1;
            if (part < 0 && second.HasValue) part = Pick(second.Value, out point);
            Show(part);
            return part;
        }

        public void Show(int part)
        {
            if (!atlas) return;
            if (part != Hovered)
            {
                Hovered = part;
                atlas.SetHighlight(part);
                if (Label)
                {
                    Label.gameObject.SetActive(part >= 0);
                    if (part >= 0) { Label.text = atlas.Parts[part].name; Label.GetComponent<ScalpalTextFit>().Fit(); }
                }
            }
            Refresh();
        }

        /// <summary>Moves the box and label onto the hovered part's current animated transform.</summary>
        public void Refresh()
        {
            if (!atlas || Hovered < 0 || !atlas.IsVisible(Hovered))
            {
                if (Hovered >= 0 && !atlas.IsVisible(Hovered)) { Hovered = -1; if (atlas) atlas.SetHighlight(-1); }
                if (Box) Box.enabled = false;
                if (Label) Label.gameObject.SetActive(false);
                return;
            }
            atlas.Corners(Hovered, corners);
            for (int i = 0; i < path.Length; i++) path[i] = corners[Walk[i]];
            if (Box) { Box.SetPositions(path); Box.enabled = true; }
            if (!Label) return;
            var top = corners[0]; var center = Vector3.zero;
            foreach (var corner in corners) { center += corner / 8; if (corner.y > top.y) top = corner; }
            Label.gameObject.SetActive(true);
            Label.transform.position = new Vector3(center.x, top.y + .015f, center.z);
            var eye = viewer ? viewer.position : Label.transform.position - Vector3.forward;
            var facing = Vector3.ProjectOnPlane(Label.transform.position - eye, Vector3.up);
            if (facing.sqrMagnitude > 1e-6f) Label.transform.rotation = Quaternion.LookRotation(facing, Vector3.up);
        }

        void LateUpdate() => Refresh();
    }
}
