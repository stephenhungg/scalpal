using UnityEngine;

namespace Scalpal.Briefing
{
    /// <summary>Resources handle for the briefing atlas: the merged mesh (Models/briefing_atlas.fbx, null until it lands) and the shader material.</summary>
    public sealed class BriefingAtlasSource : ScriptableObject
    {
        public Mesh mesh;
        public Material material;
    }
}
