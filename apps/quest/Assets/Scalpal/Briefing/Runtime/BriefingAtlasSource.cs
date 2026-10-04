using UnityEngine;

namespace Scalpal.Briefing
{
    /// <summary>Resources handle for the briefing atlas: the merged mesh (Models/briefing_atlas.fbx, null until it lands), the
    /// shader material, and the ethereal backdrop's void and mote materials (BriefingStage).</summary>
    public sealed class BriefingAtlasSource : ScriptableObject
    {
        public Mesh mesh;
        public Material material;
        public Material voidMaterial, moteMaterial;
    }
}
