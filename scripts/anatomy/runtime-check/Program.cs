using System;
using System.Reflection;
using Scalpal.Anatomy;
using Scalpal.Exercises.Coach;
using UnityEngine;

static class Program
{
    static int checks;
    static void Check(bool condition, string message)
    {
        checks++;
        if (!condition) throw new Exception(message);
    }
    static void Call(object target, string method) => target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, null);
    static AnatomyPart AddPart(GameObject root, string id, string system, bool rendererEnabled = true)
    {
        var go = root.Child();
        var part = go.AddComponent<AnatomyPart>();
        part.stableId = id;
        part.system = system;
        var renderer = go.AddComponent<Renderer>();
        renderer.enabled = rendererEnabled;
        var material = new Material { emission = true };
        material.properties.Add(Shader.PropertyToID("_EmissionColor"));
        material.properties.Add(Shader.PropertyToID("_BaseColor"));
        renderer.sharedMaterials = new[] { material };
        go.AddComponent<Collider>();
        return part;
    }
    static Renderer RendererOf(AnatomyPart part) => part.GetComponentsInChildren<Renderer>(true)[0];
    static Collider ColliderOf(AnatomyPart part) => part.GetComponentsInChildren<Collider>(true)[0];
    static Color Read(Renderer renderer, int property, int index)
    {
        var block = new MaterialPropertyBlock();
        renderer.GetPropertyBlock(block, index);
        return block.GetColor(property);
    }
    static void Main()
    {
        var root = new GameObject();
        var controller = root.AddComponent<AnatomyController>();
        var liver = AddPart(root, "liver", "organs");
        var heart = AddPart(root, "heart", "organs");
        var artery = AddPart(root, "artery", "cardiovascular");
        var hidden = AddPart(root, "hidden", "organs", false);
        var skin = AddPart(root, "skin", "surface");
        controller.RebuildIndex();
        Check(!RendererOf(liver).enabled && !ColliderOf(liver).enabled, "unregistered practice must hide renderers and colliders");
        Check(!controller.Highlight("liver") && !controller.Isolate("liver"), "unregistered practice must reject visible commands");
        controller.SetPreviewMode(true);
        Check(RendererOf(liver).enabled && ColliderOf(liver).enabled, "preview allowed before registration");
        Check(!RendererOf(hidden).enabled, "authored disabled renderer must stay disabled");
        Check(!RendererOf(skin).enabled, "serialized surface default remains hidden in preview");
        controller.SetSystemVisible("surface", true);
        controller.RebuildIndex();
        Check(RendererOf(skin).enabled, "reindex preserves user filter changes");
        Call(controller, "Update");
        Check(root.transform.rotationDegrees > 0, "selection rotates");
        var rotation = root.transform.rotationDegrees;
        controller.SetPreviewMode(false);
        controller.SetRegistrationValid(true);
        Call(controller, "Update");
        Check(root.transform.rotationDegrees == rotation, "practice must not rotate anatomy");
        Check(controller.SetSystemVisible("ORGANS", false), "case insensitive system toggle");
        Check(!RendererOf(liver).enabled && RendererOf(artery).enabled, "system filter only hides matching system");
        Check(controller.Isolate("liver") && RendererOf(liver).enabled && !RendererOf(artery).enabled, "isolation overrides prior filters");
        controller.RestoreVisibility();
        Check(!RendererOf(liver).enabled && RendererOf(artery).enabled, "restore preserves pre-isolation filters");
        Check(!controller.Isolate("missing") && !controller.SetSystemVisible("missing", false), "unknown commands fail without changes");
        controller.ShowAllSystems();
        Check(RendererOf(heart).enabled && RendererOf(artery).enabled, "show all resets filters");

        var renderer = RendererOf(liver);
        var emission = Shader.PropertyToID("_EmissionColor");
        var other = Shader.PropertyToID("_Other");
        var original = new Color(0.1f, 0.2f, 0.3f, 1);
        var unrelated = new Color(0.7f, 0.4f, 0.2f, 1);
        var block = new MaterialPropertyBlock();
        block.SetColor(emission, original);
        block.SetColor(other, unrelated);
        renderer.SetPropertyBlock(block, 0);
        Check(controller.Highlight("liver"), "known visible part highlights");
        Check(!Read(renderer, emission, 0).Equals(original), "highlight changes emission");
        Check(Read(renderer, other, 0).Equals(unrelated), "highlight preserves unrelated indexed properties");
        controller.ClearHighlight();
        Check(Read(renderer, emission, 0).Equals(original), "clear restores original emission");
        renderer.SetPropertyBlock(null, 0);
        renderer.SetPropertyBlock(block);
        controller.Highlight("liver");
        Check(Read(renderer, other, 0).Equals(unrelated), "highlight inherits renderer-wide block when indexed block absent");
        controller.ClearHighlight();
        var restored = new MaterialPropertyBlock();
        renderer.GetPropertyBlock(restored, 0);
        Check(restored.isEmpty && Read(renderer, emission, -1).Equals(original), "clear removes created indexed override and preserves renderer-wide block");
        renderer.sharedMaterials[0].emission = false;
        Check(controller.Highlight("liver"), "non-emissive material uses base-color fallback");
        Check(!Read(renderer, Shader.PropertyToID("_BaseColor"), 0).Equals(default), "fallback changes base color");
        controller.Highlight("heart");
        Check(!liver.IsHighlighted && heart.IsHighlighted, "only selected part remains highlighted");
        controller.SetRegistrationValid(false);
        Check(!RendererOf(heart).enabled && !heart.IsHighlighted && !ColliderOf(heart).enabled, "tracking loss clears highlight, renderer, collider");
        controller.SetRegistrationValid(true);
        Check(RendererOf(heart).enabled && !heart.IsHighlighted, "tracking recovery does not revive stale highlight");
        controller.SetPreviewMode(true);
        controller.enabled = false;
        Call(controller, "OnDisable");
        Check(!RendererOf(heart).enabled && !controller.Highlight("heart"), "disabled controller cannot expose anatomy");
        controller.enabled = true;
        Call(controller, "OnEnable");
        Check(RendererOf(heart).enabled, "reenable reapplies current visibility gates");
        heart.gameObject.activeSelf = false;
        Check(!controller.Highlight("heart"), "inactive geometry cannot report a visible highlight");
        heart.gameObject.activeSelf = true;
        var originalHeartMaterials = RendererOf(heart).sharedMaterials;
        var ghost = new Material();
        ghost.properties.Add(Shader.PropertyToID("_BaseColor"));
        heart.ghostMaterial = ghost;
        Check(!controller.SetSystemGhosted("organs", true) && !heart.IsGhosted,
            "unsupported system ghosting is rejected atomically");
        liver.ghostMaterial = ghost;
        hidden.ghostMaterial = ghost;
        controller.Highlight("heart");
        Check(controller.SetSystemGhosted("ORGANS", true), "ghost system supports case-insensitive system name");
        Check(heart.IsGhosted && !heart.IsHighlighted && ReferenceEquals(RendererOf(heart).sharedMaterials[0], ghost),
            "ghost clears highlight and uses supplied shared material");
        controller.SetSystemVisible("organs", false);
        controller.SetSystemVisible("organs", true);
        Check(heart.IsGhosted && ReferenceEquals(RendererOf(heart).sharedMaterials[0], ghost),
            "ghost preference survives visibility changes");
        controller.SetPreviewMode(false);
        controller.SetRegistrationValid(false);
        Check(heart.IsGhosted && !RendererOf(heart).enabled && !ColliderOf(heart).enabled,
            "ghost cannot bypass invalid registration");
        controller.SetRegistrationValid(true);
        Check(heart.IsGhosted && RendererOf(heart).enabled, "ghost returns after valid registration");
        Check(controller.Highlight("heart"), "ghost material can receive a temporary highlight");
        Check(controller.SetSystemGhosted("organs", false) && !heart.IsGhosted && !heart.IsHighlighted,
            "unghost clears temporary highlight");
        Check(ReferenceEquals(RendererOf(heart).sharedMaterials[0], originalHeartMaterials[0]),
            "unghost restores original shared material slots");
        Check(!controller.SetSystemGhosted("missing", true), "unknown ghost system fails");
        var extra = new Material();
        var slots = new[] { originalHeartMaterials[0], extra };
        RendererOf(heart).sharedMaterials = slots;
        heart.SetGhosted(true);
        Check(RendererOf(heart).sharedMaterials.Length == 2 && ReferenceEquals(RendererOf(heart).sharedMaterials[1], ghost),
            "ghost replaces every material slot");
        heart.SetGhosted(false);
        Check(ReferenceEquals(RendererOf(heart).sharedMaterials[0], slots[0]) && ReferenceEquals(RendererOf(heart).sharedMaterials[1], slots[1]),
            "unghost preserves slot ordering and original material identity");
        AddPart(root, "liver", "organs");
        controller.RebuildIndex();
        Check(!controller.TryGetPart("liver", out _) && !controller.Highlight("liver"), "duplicate ids rejected instead of highlighting arbitrary mesh");
        var nestedRoot = root.Child();
        var nested = nestedRoot.AddComponent<AnatomyController>();
        AddPart(nestedRoot, "nested", "organs");
        nested.RebuildIndex();
        controller.RebuildIndex();
        Check(!controller.TryGetPart("nested", out _) && nested.TryGetPart("nested", out _), "nested controller owns its parts exclusively");
        var relay = root.AddComponent<CoachRelay>();
        var binding = root.AddComponent<AnatomyCoachBinding>();
        binding.anatomy = controller;
        binding.relay = relay;
        binding.Rebind();
        Check(relay.tracking.Count == 1 && relay.tracking[0], "binding publishes initial registration state");
        relay.Emit("highlight", "heart");
        Check(relay.acks.Count == 1 && relay.acks[0].applied && heart.IsHighlighted, "relay acks only applied highlight");
        relay.Emit("highlight", "liver");
        Check(!relay.acks[1].applied, "ambiguous highlight ack rejected");
        relay.Emit("clear_highlight", null);
        Check(relay.acks[2].applied && !heart.IsHighlighted, "clear command restores highlight state");
        controller.SetPreviewMode(false);
        controller.SetRegistrationValid(false);
        Check(relay.tracking.Count == 2 && !relay.tracking[1], "tracking loss forwarded");
        relay.Emit("highlight", "heart");
        Check(!relay.acks[3].applied, "binding rejects highlight with invalid practice registration");
        relay.Emit("delete", "heart");
        Check(!relay.acks[4].applied, "unsupported action rejected");
        binding.Rebind();
        relay.Emit("clear_highlight", null);
        Check(relay.acks.Count == 6, "rebind does not duplicate event handlers");
        controller.SetPreviewMode(true);
        Check(controller.SetExerciseParts(new[] { "heart" }), "known exercise filter accepted");
        Check(!controller.Isolate("artery"), "isolation cannot bypass exercise context");
        Check(!controller.SetExerciseParts(new[] { "missing-target" }), "invalid case filter rejected atomically");
        Check(heart.IsVisible && !artery.IsVisible, "invalid filter preserves prior visibility");
        Check(controller.SetExerciseParts(null), "case context can be cleared");
        Call(binding, "OnDisable");
        relay.Emit("highlight", "heart");
        Check(relay.acks.Count == 6, "disabled binding unsubscribes");
#if UNITY_EDITOR || (!UNITY_ANDROID && !UNITY_IOS)
        var searchRoot = new GameObject();
        var searchController = searchRoot.AddComponent<AnatomyController>();
        for (var i = 0; i < 25; i++)
        {
            var searchPart = AddPart(searchRoot, "source_" + i, "organs");
            searchPart.displayName = "Organ " + i;
        }
        searchController.RebuildIndex();
        var panel = searchRoot.AddComponent<AnatomyPreviewPanel>();
        panel.anatomy = searchController;
        Check(panel.FindMatches("").Count == 20, "desktop search limits displayed matches to twenty");
        Check(panel.FindMatches("SOURCE_24").Count == 1, "desktop search matches stable ids case-insensitively");
        Check(panel.FindMatches(" Organ 24 ").Count == 1, "desktop search matches source labels and trims whitespace");
        Check(panel.FindMatches("not-present").Count == 0, "desktop search reports no results for unknown labels");
        Call(panel, "OnGUI");
        panel.anatomy = null;
        Call(panel, "OnGUI");
        Check(panel.FindMatches("").Count == 0, "unassigned desktop panel safely handles search");
#endif
        EditorChecks.Run();
        Console.WriteLine($"Anatomy runtime: {checks} behavior checks passed. UnityEngine doubles only; no headset/render verification.");
    }
}
