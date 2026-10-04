using Scalpal.Anatomy.EditorTools;
using UnityEditor;

namespace Scalpal.Quest.Editor
{
    public static class NativeTissueBuild
    {
        // Refresh prefab content without regenerating unrelated XR/native scene object identities.
        [MenuItem("Scalpal/Quest/Prepare Tissue Milestone")]
        public static void Prepare()
        {
            AnatomyAtlasBuilder.BuildExercise();
            AnatomyAtlasBuilder.BuildOrganOverview();
            NativeSessionBuild.Verify();
        }
    }
}
