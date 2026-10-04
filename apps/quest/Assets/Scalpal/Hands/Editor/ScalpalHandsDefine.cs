// Turns the hand module on only in projects that have the packages it needs. Defines SCALPAL_HANDS for
// Android and Standalone when both com.unity.ai.inference and com.meta.xr.mrutilitykit are installed,
// and removes it when either is missing, so the instrument workbench without them still compiles.
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.PackageManager;

namespace Scalpal.Hands.EditorTools
{
    [InitializeOnLoad]
    static class ScalpalHandsDefine
    {
        const string Symbol = "SCALPAL_HANDS";
        static readonly string[] Required = { "com.unity.ai.inference", "com.meta.xr.mrutilitykit" };

        static ScalpalHandsDefine()
        {
            Events.registeredPackages += _ => Apply();
            EditorApplication.delayCall += Apply;
        }

        [MenuItem("Scalpal/Hands/Refresh SCALPAL_HANDS Define")]
        static void Apply()
        {
            var installed = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages().Select(p => p.name).ToArray();
            var enable = Required.All(installed.Contains);
            foreach (var target in new[] { NamedBuildTarget.Android, NamedBuildTarget.Standalone })
            {
                var symbols = PlayerSettings.GetScriptingDefineSymbols(target).Split(';').Where(s => s.Length > 0).ToList();
                var has = symbols.Contains(Symbol);
                if (enable == has) continue;
                if (enable) symbols.Add(Symbol);
                else symbols.Remove(Symbol);
                PlayerSettings.SetScriptingDefineSymbols(target, string.Join(";", symbols));
            }
        }
    }
}
