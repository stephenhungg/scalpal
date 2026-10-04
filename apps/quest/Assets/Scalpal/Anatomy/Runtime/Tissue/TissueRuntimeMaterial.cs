using System;
using UnityEngine;

namespace Scalpal.Anatomy.Tissue
{
    // A committed Resources material carries the opaque variant into player builds.
    // Instances are owned/disposed by the wall or vessel; the template is never edited.
    public static class TissueRuntimeMaterial
    {
        public static Material Create(string name, Color color)
        {
            var template = Resources.Load<Material>("TissueOpaque");
            if (!template || !template.shader) throw new InvalidOperationException("Missing authored TissueOpaque material/shader");
            return new Material(template) { name = name, color = color };
        }
    }
}
