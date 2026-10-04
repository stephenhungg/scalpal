using System;
using System.Reflection;
using System.Text.Json;
using Scalpal.Anatomy.EditorTools;

public static class EditorChecks
{
    public static void Run()
    {
        const string valid = "{\"schemaVersion\":1,\"systems\":[{\"id\":\"visceral\",\"group\":\"body\",\"assetPath\":\"Assets/Scalpal/Anatomy/Models/visceral.fbx\"}],\"parts\":[{\"stableId\":\"liver\",\"displayName\":\"Liver\",\"system\":\"visceral\",\"objectName\":\"Liver_mesh\",\"catalogId\":\"liver\"}]}";
        Check(valid, true);
        Check(valid.Replace("\"schemaVersion\":1", "\"schemaVersion\":2"), false);
        Check(valid.Replace("\"group\":\"body\"", "\"group\":\"unknown\""), false);
        Check(valid.Replace("Models/visceral.fbx", "Models/../visceral.fbx"), false);
        Check(valid.Replace("Models/visceral.fbx", "Elsewhere/visceral.fbx"), false);
        Check(valid.Replace("\"stableId\":\"liver\"", "\"stableId\":\"wrong\""), false);
        Check(valid.Replace("\"system\":\"visceral\"", "\"system\":\"unknown\""), false);
        Check(valid.Replace("\"displayName\":\"Liver\"", "\"displayName\":\"\""), false);
        Check(valid.Replace("\"objectName\":\"Liver_mesh\"", "\"objectName\":\"\""), false);
        var part = JsonDocument.Parse(valid).RootElement.GetProperty("parts")[0].GetRawText();
        Check(valid.Replace("[" + part + "]", "[" + part + "," + part + "]"), false);
        Check(valid.Replace("[" + part + "]", "[" + part + "," + part.Replace("\"stableId\":\"liver\"", "\"stableId\":\"other\"").Replace("\"catalogId\":\"liver\"", "\"catalogId\":\"\"") + "]"), false);
        Check("{\"schemaVersion\":1,\"systems\":[],\"parts\":[]}", false);
        bool missingManifestRejected = false;
        try { AnatomyAtlasBuilder.Build(); }
        catch (InvalidOperationException error) { missingManifestRejected = error.Message.Contains("Missing atlas manifest"); }
        if (!missingManifestRejected) throw new Exception("Builder did not reject missing manifest.");
        Console.WriteLine("13 editor manifest/failure checks passed (no Unity FBX import or prefab save executed).");
        CheckMaterial("cardiovascular", "Hepatic vein", "vessel_vein");
        CheckMaterial("cardiovascular", "Cystic artery", "vessel_artery");
        CheckMaterial("cardiovascular", "Pulmonary arteries", "vessel_artery");
        CheckMaterial("visceral", "Liver", "tissue_liver");
        CheckMaterial("visceral", "Common hepatic duct", "tissue_duct");
        CheckMaterial("visceral", "Lung.L", "tissue_lung");
        CheckMaterial("visceral", "Kidney.R", "tissue_kidney");
        CheckMaterial("muscular", "Unknown source label", "muscular");
        Console.WriteLine("8 shared material palette checks passed.");
        CheckMaterialConfiguration();
    }

    static void Check(string json, bool expectedValid)
    {
        var atlasType = typeof(AnatomyAtlasBuilder).GetNestedType("Atlas", BindingFlags.NonPublic);
        var atlas = JsonSerializer.Deserialize(json, atlasType, new JsonSerializerOptions { IncludeFields = true });
        var validate = typeof(AnatomyAtlasBuilder).GetMethod("ValidateManifest", BindingFlags.NonPublic | BindingFlags.Static);
        bool valid = true;
        try { validate.Invoke(null, new[] { atlas }); }
        catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException) { valid = false; }
        if (valid != expectedValid) throw new Exception("Unexpected manifest validation outcome: " + json);
    }

    static void CheckMaterial(string system, string name, string expected)
    {
        var method = typeof(AnatomyAtlasBuilder).GetMethod("MaterialKey", BindingFlags.NonPublic | BindingFlags.Static);
        var actual = (string)method.Invoke(null, new object[] { system, name });
        if (actual != expected) throw new Exception("Incorrect shared material palette for " + name);
    }

    static void CheckMaterialConfiguration()
    {
        var method = typeof(AnatomyAtlasBuilder).GetMethod("BuildMaterial", BindingFlags.NonPublic | BindingFlags.Static);
        var shader = UnityEngine.Shader.Find("Universal Render Pipeline/Lit");
        var opaque = (UnityEngine.Material)method.Invoke(null, new object[] { "vessel_vein", shader, false });
        var ghost = (UnityEngine.Material)method.Invoke(null, new object[] { "vessel_vein", shader, true });
        if (opaque.colors["_BaseColor"].a != 1 || ghost.colors["_BaseColor"].a != 0.15f)
            throw new Exception("Incorrect opacity for shared ghost/opaque materials.");
        if (ghost.floats["_Surface"] != 1 || ghost.floats["_ZWrite"] != 0 || ghost.floats["_SrcBlend"] != 5 || ghost.floats["_DstBlend"] != 10)
            throw new Exception("Ghost material must use standard alpha blending without depth writes.");
        if (ghost.renderQueue != 3000 || !ghost.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT") || ghost.passes["ShadowCaster"])
            throw new Exception("Ghost render queue, shader variant, or shadow settings are incorrect.");
        if (opaque.floats["_Surface"] != 0 || opaque.floats["_ZWrite"] != 1 || opaque.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT")
            || !opaque.IsKeywordEnabled("_EMISSION") || !ghost.IsKeywordEnabled("_EMISSION"))
            throw new Exception("Opaque material must remain opaque and both variants support highlighting.");
        if (opaque.shader.name != "Scalpal/Tissue")
            throw new Exception("Opaque anatomy must use the shared tissue shader in the current builder.");
        var standard = (UnityEngine.Material)method.Invoke(null,
            new object[] { "vessel_vein", UnityEngine.Shader.Find("Standard"), true });
        if (standard.shader.name != "Standard" || standard.colors["_Color"].a != 0.15f || standard.floats["_Mode"] != 2)
            throw new Exception("Built-in ghost must use Standard fade mode and its color property.");
        if (standard.floats["_ZWrite"] != 0 || standard.floats["_SrcBlend"] != 5 || standard.floats["_DstBlend"] != 10)
            throw new Exception("Built-in ghost must alpha blend without depth writes.");
        if (standard.renderQueue != 3000 || !standard.IsKeywordEnabled("_ALPHABLEND_ON")
            || standard.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT") || standard.passes["ShadowCaster"])
            throw new Exception("Built-in ghost must use its own transparent variant without shadows.");
        if (!standard.IsKeywordEnabled("_EMISSION") || standard.IsKeywordEnabled("_ALPHATEST_ON")
            || standard.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON"))
            throw new Exception("Built-in ghost must support highlighting without conflicting alpha keywords.");
        Console.WriteLine("9 tissue/URP/built-in material configuration checks passed (shader rendering not exercised).");
    }
}
