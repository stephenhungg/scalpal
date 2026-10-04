using UnityEngine;

namespace Scalpal.EncounterOffice
{
    [RequireComponent(typeof(TextMesh))]
    public sealed class EncounterOfficeText : MonoBehaviour
    {
        public float maximumWidth = 1.04f, maximumHeight = .12f;
        public float preferredCharacterSize = .005f;
        string previousText;
        TextMesh text;
        void OnEnable() { text = GetComponent<TextMesh>(); Font.textureRebuilt += RefreshAtlas; Fit(); }
        void OnDisable() => Font.textureRebuilt -= RefreshAtlas;
        void LateUpdate() { if (text && previousText != text.text) Fit(); }
        void RefreshAtlas(Font font)
        {
            if (!text || text.font != font) return;
            var material = GetComponent<Renderer>().sharedMaterial;
            if (material && material.shader.name == "Scalpal/Encounter Office/World Text") material.mainTexture = font.material.mainTexture;
        }
        public void Fit()
        {
            if (!text) text = GetComponent<TextMesh>();
            if (!text || !text.font) return;
            text.font.RequestCharactersInTexture(text.text, text.fontSize, text.fontStyle);
            RefreshAtlas(text.font);
            text.characterSize = preferredCharacterSize;
            previousText = text.text;
            // TextMesh glyph dimensions depend on fontSize as well as characterSize. Fit actual generated mesh bounds in meters.
            var size = GetComponent<Renderer>().localBounds.size;
            var scale = transform.lossyScale;
            float width = Mathf.Abs(size.x * scale.x), height = Mathf.Abs(size.y * scale.y);
            float fit = Mathf.Min(1, maximumWidth / Mathf.Max(.0001f, width), maximumHeight / Mathf.Max(.0001f, height));
            text.characterSize *= fit;
        }
        public Vector2 MeasuredSize()
        {
            var size = GetComponent<Renderer>().localBounds.size; var scale = transform.lossyScale;
            return new Vector2(Mathf.Abs(size.x * scale.x), Mathf.Abs(size.y * scale.y));
        }
    }
}
