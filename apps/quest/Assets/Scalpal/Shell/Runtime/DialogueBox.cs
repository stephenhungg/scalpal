using System.Collections.Generic;
using System.Text;
using Scalpal.Brand;
using TMPro;
using UnityEngine;
using UnityEngine.XR;

namespace Scalpal.Shell
{
    public enum DialogueSpeaker { You, Patient, Parent, Attending, Coach }
    public enum DialogueIndicator { None, Listening, Thinking }

    // Video-game style transcript card in the lower middle of view, shared by the office and the OR.
    // Presentation only: it is fed by DialogueFeed and never talks to voice transport or clinical state.
    // Placement is lazy-follow: body-locked yaw with a dead zone and smoothing, horizon-relative pitch.
    public sealed class DialogueBox : MonoBehaviour
    {
        public static DialogueBox Active { get; private set; }
        public DialogueBoxStyle style;
        public Transform viewer;
        public float yawDeadZone = 18f, maxYawLag = 40f, followSeconds = .45f, positionDeadZone = .10f, maxPositionLag = .30f;
        public float speechCharactersPerSecond = 16f, transcriptCharactersPerSecond = 60f;
        public float minimumIdleSeconds = 6f, maximumIdleSeconds = 12f, fadeSeconds = .35f;
        // Lower-middle panels the card must not overlap (for example the office assessment console).
        public readonly List<Renderer> avoid = new List<Renderer>();
        // Optional face the card must stay below, e.g. the seated patient's head.
        public Transform protectedFace;
        public float faceMarginDegrees = 4f, maximumPitchDegrees = 45f;

        public DialogueSpeaker Speaker { get; private set; }
        public string SpeakerLabel { get; private set; } = "";
        public string FullText { get; private set; } = "";
        public string PreviousText { get; private set; } = "";
        public string VisibleText => body ? body.text : "";
        public bool Typing => revealed < revealTotal;
        public bool Visible => content && content.activeSelf;
        public float Alpha => alpha;
        public DialogueIndicator Indicator { get; private set; }
        public float CurrentPitch => pitch;
        public float CardHeight { get; private set; }
        public float CardWidth => style ? style.width : .8f;
        public float BodyGlyphHeight { get; private set; }
        public float TargetPitch { get; private set; }

        GameObject content;
        TextMeshPro body, previous, nameText, initial, indicatorText;
        Renderer card, border, chip;
        MaterialPropertyBlock block;
        readonly List<string> lines = new List<string>();
        readonly List<int> lineEnds = new List<int>();
        readonly StringBuilder builder = new StringBuilder(256);
        readonly List<XRInputSubsystem> inputs = new List<XRInputSubsystem>();
        readonly List<XRInputSubsystem> subscribed = new List<XRInputSubsystem>();
        float revealed, idle, alpha, previousAlpha, indicatorClock, rate;
        float yaw, yawVelocity, pitch, pitchVelocity, nextSubsystemScan;
        Vector3 anchor, anchorVelocity;
        bool following, moving, snap = true, summoned, warning, built;
        int shownCharacters = -1, indicatorPhase = -1;
        float metersPerPixel = 1, bodyLinePitch, textWidth, bodyEm;
        string indicatorName = "";
        Color roleColor;
        const int MaxCharacters = 600;
        static readonly string[] ListeningFrames = { "listening", "listening.", "listening..", "listening..." };
        static readonly string[] ThinkingFrames = { "thinking", "thinking.", "thinking..", "thinking..." };
        static Mesh blossom;
        static readonly int ColorId = Shader.PropertyToID("_Color");
        static readonly int RimId = Shader.PropertyToID("_RimColor"), RimBId = Shader.PropertyToID("_RimColorB");

        public static DialogueBox Create(DialogueBoxStyle style, Transform viewer)
        {
            if (!style) return null;
            var go = new GameObject("DialogueBox");
            var box = go.AddComponent<DialogueBox>();
            box.style = style; box.viewer = viewer; box.Build();
            return box;
        }

        public static string Label(DialogueSpeaker speaker, string name)
        {
            switch (speaker)
            {
                case DialogueSpeaker.You: return "You";
                case DialogueSpeaker.Attending: return "Jarvis · Attending";
                case DialogueSpeaker.Coach: return "Jarvis · Coach";
                case DialogueSpeaker.Parent: return string.IsNullOrWhiteSpace(name) ? "Parent" : name.Trim() + " · Parent";
                default: return string.IsNullOrWhiteSpace(name) ? "Patient" : name.Trim() + " · Patient";
            }
        }

        public Color RoleColor(DialogueSpeaker speaker)
        {
            switch (speaker)
            {
                case DialogueSpeaker.You: return style.you;
                case DialogueSpeaker.Parent: return style.parent;
                case DialogueSpeaker.Attending: return style.attending;
                case DialogueSpeaker.Coach: return style.coach;
                default: return style.patient;
            }
        }

        void OnEnable() { Active = this; }
        void OnDisable()
        {
            if (Active == this) Active = null;
            foreach (var system in subscribed) system.trackingOriginUpdated -= OriginUpdated;
            subscribed.Clear();
        }

        void Build()
        {
            if (built || !style) return;
            built = true; block = new MaterialPropertyBlock();
            TargetPitch = pitch = style.pitchDegrees;
            content = new GameObject("Card");
            content.transform.SetParent(transform, false);
            float width = style.width, pad = .028f;
            textWidth = width - 2 * pad;
            body = Text("Body", style.bodyFont, style.bodyText, TextAnchor.UpperLeft);
            BodyGlyphHeight = Calibrate(body, style.bodyLineHeight);
            bodyEm = body.fontSize * .1f;
            body.text = "Hg\nHg"; bodyLinePitch = Measure(body).y - BodyGlyphHeight;
            // Widths are measured in metres straight from the static SDF font's advances.
            metersPerPixel = 1;
            body.text = "";
            previous = Text("Previous", style.bodyFont, style.bodyText, TextAnchor.UpperLeft);
            float previousHeight = Calibrate(previous, style.bodyLineHeight * .82f);
            nameText = Text("Name", style.nameFont, style.nameText, TextAnchor.MiddleLeft);
            Calibrate(nameText, style.bodyLineHeight * .92f);
            indicatorText = Text("Indicator", style.bodyFont, style.bodyText, TextAnchor.MiddleRight);
            Calibrate(indicatorText, style.bodyLineHeight * .78f);
            initial = Text("Initial", style.nameFont, style.nameText, TextAnchor.MiddleCenter);
            Calibrate(initial, style.bodyLineHeight * .80f);

            const float chipSize = .044f, nameRow = .050f, gap = .010f;
            float bodyBlock = BodyGlyphHeight + bodyLinePitch;
            CardHeight = pad + nameRow + gap + previousHeight + gap + bodyBlock + pad;
            float top = CardHeight / 2, left = -width / 2 + pad;
            float nameY = top - pad - nameRow / 2;
            // Same glass material throughout; sorting order keeps rim < card < chip < text within its queue.
            card = Surface("Glass", new Vector2(width, CardHeight), style.glass, Vector3.zero, 1);
            border = Surface("Rim", new Vector2(width + .006f, CardHeight + .006f), style.glass, new Vector3(0, 0, .002f), 0);
            var chipObject = new GameObject("RoleChip", typeof(MeshFilter), typeof(MeshRenderer));
            chipObject.transform.SetParent(content.transform, false);
            chipObject.transform.localPosition = new Vector3(left + chipSize / 2, nameY, -.004f);
            chipObject.transform.localScale = Vector3.one * chipSize;
            chipObject.GetComponent<MeshFilter>().sharedMesh = Blossom();
            chip = chipObject.GetComponent<MeshRenderer>(); chip.sharedMaterial = style.chip ? style.chip : style.glass; chip.sortingOrder = 2;
            Plain(chip);
            initial.transform.localPosition = new Vector3(left + chipSize / 2, nameY, -.008f);
            nameText.transform.localPosition = new Vector3(left + chipSize + .014f, nameY, -.008f);
            indicatorText.transform.localPosition = new Vector3(width / 2 - pad, nameY, -.008f);
            previous.transform.localPosition = new Vector3(left, nameY - nameRow / 2 - gap, -.008f);
            body.transform.localPosition = new Vector3(left, nameY - nameRow / 2 - gap - previousHeight - gap, -.008f);
            content.SetActive(false); alpha = 0;
        }

        TextMeshPro Text(string name, TMP_FontAsset font, Material material, TextAnchor anchor)
        {
            var text = new GameObject(name).AddComponent<TextMeshPro>();
            text.transform.SetParent(content.transform, false);
            text.font = font; text.fontSharedMaterial = material; text.richText = false;
            text.textWrappingMode = TextWrappingModes.NoWrap; text.overflowMode = TextOverflowModes.Overflow;
            ScalpalBrand.Anchor(text, anchor);
            text.rectTransform.sizeDelta = new Vector2(textWidth, .1f);
            text.color = style.ink;
            var renderer = text.GetComponent<MeshRenderer>(); renderer.sortingOrder = 3; Plain(renderer);
            return text;
        }

        // Fix the line extent (ascender to descender) from the font's metrics instead of a font-size guess.
        static float Calibrate(TextMeshPro text, float height)
        {
            float em = height / ScalpalBrandLayout.ExtentPerEm(text.font) / Mathf.Max(1e-6f, Mathf.Abs(text.transform.lossyScale.y));
            text.fontSize = em * 10;
            return height;
        }

        static Vector2 Measure(TextMeshPro text)
        {
            var size = text.GetPreferredValues(Mathf.Infinity, Mathf.Infinity);
            var scale = text.transform.lossyScale;
            return new Vector2(Mathf.Abs(size.x * scale.x), Mathf.Abs(size.y * scale.y));
        }

        // Advance of one character in metres at an em size, from the static SDF font asset (with fallback).
        static float Advance(TMP_FontAsset font, char c, float em)
        {
            if (font.characterLookupTable.TryGetValue(c, out var character)) return character.glyph.metrics.horizontalAdvance / font.faceInfo.pointSize * font.faceInfo.scale * character.scale * em;
            if (font.fallbackFontAssetTable != null) foreach (var fallback in font.fallbackFontAssetTable) if (fallback && fallback.characterLookupTable.ContainsKey(c)) return Advance(fallback, c, em);
            return em * .6f;
        }

        Renderer Surface(string name, Vector2 size, Material material, Vector3 position, int order)
        {
            var panel = ShellView.Panel(content.transform, name, position, size);
            var renderer = panel.GetComponent<MeshRenderer>(); renderer.sharedMaterial = material; renderer.sortingOrder = order;
            return renderer;
        }

        static void Plain(Renderer renderer) { renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; renderer.receiveShadows = false; }

        // Five-petal floral chip, generated once; original geometry, no texture.
        static Mesh Blossom()
        {
            if (blossom) return blossom;
            const int petals = 5, steps = 18;
            var vertices = new List<Vector3> { Vector3.zero };
            var triangles = new List<int>();
            for (int p = 0; p < petals; p++)
            {
                float angle = p * Mathf.PI * 2 / petals + Mathf.PI / 2;
                var axis = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0); var side = new Vector3(-axis.y, axis.x, 0);
                int start = vertices.Count;
                // Convex lens petal from the centre to its tip, fanned from the centre vertex.
                for (int s = 0; s <= steps; s++)
                {
                    float t = s / (float)steps * Mathf.PI * 2;
                    vertices.Add(axis * (.50f * (1 - Mathf.Cos(t)) / 2) + side * (.17f * Mathf.Sin(t)));
                }
                for (int s = 0; s < steps; s++) { triangles.Add(0); triangles.Add(start + s); triangles.Add(start + s + 1); }
            }
            blossom = new Mesh { name = "Dialogue blossom chip", vertices = vertices.ToArray(), uv = new Vector2[vertices.Count], triangles = triangles.ToArray() };
            blossom.RecalculateBounds();
            return blossom;
        }

        // A new line of dialogue. The previous line moves above and fades.
        public void Say(DialogueSpeaker speaker, string name, string text, bool warning = false)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            Build();
            text = text.Trim(); if (text.Length > MaxCharacters) text = text.Substring(0, MaxCharacters - 1) + "…";
            bool continuing = Speaker == speaker && FullText.Length == 0 && SpeakerLabel.Length > 0;
            if (!continuing) PushPrevious();
            SetSpeaker(speaker, name);
            this.warning = warning;
            FullText = text; revealed = 0; shownCharacters = -1; idle = 0;
            rate = speaker == DialogueSpeaker.You ? transcriptCharactersPerSecond : speechCharactersPerSecond;
            Wrap(text);
            Show();
            Render();
        }

        // Begin an empty turn (for example the learner holding talk) so the indicator has an owner.
        public void BeginTurn(DialogueSpeaker speaker, string name)
        {
            Build();
            if (Speaker == speaker && FullText.Length == 0 && SpeakerLabel.Length > 0) return;
            PushPrevious(); SetSpeaker(speaker, name);
            FullText = ""; revealed = 0; revealTotal = 0; shownCharacters = -1; lines.Clear(); lineEnds.Clear(); warning = false; idle = 0;
            Render();
        }

        // Speech for the current line ended: reveal the rest immediately.
        public void CompleteLine()
        {
            if (!Typing) return;
            revealed = revealTotal; idle = 0; Render();
        }

        public void SetIndicator(DialogueIndicator state, string responder = "", bool summon = true)
        {
            bool summonNow = state != DialogueIndicator.None && summon;
            if (state == Indicator && summonNow == summoned && (responder ?? "") == indicatorName) return;
            Build();
            Indicator = state; summoned = summonNow; indicatorName = responder ?? ""; indicatorPhase = -1;
            if (summoned) Show();
            if (state == DialogueIndicator.None && indicatorText) indicatorText.text = "";
        }

        public void Clear()
        {
            FullText = ""; PreviousText = ""; SpeakerLabel = ""; lines.Clear(); lineEnds.Clear(); revealed = 0; revealTotal = 0;
            Indicator = DialogueIndicator.None; summoned = false;
            if (content) { body.text = previous.text = nameText.text = initial.text = indicatorText.text = ""; content.SetActive(false); }
            alpha = 0;
        }

        public void Recenter() { snap = true; if (Visible) Follow(0); }

        void PushPrevious()
        {
            if (SpeakerLabel.Length == 0 || FullText.Length == 0) return;
            PreviousText = Ellipsize(SpeakerLabel + ":  " + FullText.Replace('\n', ' '), textWidth, previous);
            previous.text = PreviousText; previousAlpha = 1;
        }

        void SetSpeaker(DialogueSpeaker speaker, string name)
        {
            Speaker = speaker; SpeakerLabel = Label(speaker, name); roleColor = RoleColor(speaker);
            nameText.text = SpeakerLabel;
            string source = speaker == DialogueSpeaker.Attending || speaker == DialogueSpeaker.Coach ? "Jarvis" : speaker == DialogueSpeaker.You ? "You" : name;
            initial.text = string.IsNullOrWhiteSpace(source) ? "·" : char.ToUpperInvariant(source.Trim()[0]).ToString();
            ApplyColors();
        }

        void Show()
        {
            if (!content) return;
            if (!content.activeSelf) { content.SetActive(true); snap = true; alpha = 0; ApplyColors(); }
        }

        void Wrap(string text)
        {
            lines.Clear(); lineEnds.Clear();
            float space = Width(' '), maxPixels = textWidth / Mathf.Max(metersPerPixel, 1e-7f);
            int consumed = 0;
            foreach (var paragraph in text.Split('\n'))
            {
                builder.Clear(); float lineWidth = 0;
                foreach (var word in paragraph.Split(' '))
                {
                    if (word.Length == 0) continue;
                    float wordWidth = 0; foreach (char c in word) wordWidth += Width(c);
                    if (builder.Length > 0 && lineWidth + space + wordWidth > maxPixels) { lines.Add(builder.ToString()); builder.Clear(); lineWidth = 0; }
                    if (builder.Length > 0) { builder.Append(' '); lineWidth += space; }
                    // Hard-split a single word longer than the line.
                    foreach (char c in word)
                    {
                        float w = Width(c);
                        if (lineWidth + w > maxPixels && builder.Length > 0) { lines.Add(builder.ToString()); builder.Clear(); lineWidth = 0; }
                        builder.Append(c); lineWidth += w;
                    }
                }
                lines.Add(builder.ToString());
            }
            // Visible character count at the end of each wrapped line, in reveal order.
            foreach (var line in lines) { consumed += line.Length; lineEnds.Add(consumed); }
            revealTotal = consumed;
        }
        int revealTotal;

        float Width(char c) => Advance(style.bodyFont, c, bodyEm);

        string Ellipsize(string value, float meters, TextMeshPro target)
        {
            float scale = target.fontSize / Mathf.Max(body.fontSize, 1e-6f);
            float limit = meters / Mathf.Max(metersPerPixel * scale, 1e-7f), sum = 0, ellipsis = Width('…');
            for (int i = 0; i < value.Length; i++)
            {
                sum += Width(value[i]);
                if (sum + ellipsis > limit && i < value.Length - 1) return value.Substring(0, i).TrimEnd() + "…";
            }
            return value;
        }

        void Render()
        {
            if (!body) return;
            int visible = Mathf.Min(revealTotal, Mathf.FloorToInt(revealed));
            if (visible == shownCharacters) return;
            shownCharacters = visible;
            if (lines.Count == 0) { body.text = ""; return; }
            // Show at most two wrapped lines: the line being typed and the one before it.
            int cursor = lines.Count - 1;
            for (int i = 0; i < lines.Count; i++) if (lineEnds[i] >= visible) { cursor = i; break; }
            builder.Clear();
            for (int i = Mathf.Max(0, cursor - 1); i <= cursor; i++)
            {
                int start = i == 0 ? 0 : lineEnds[i - 1];
                int count = Mathf.Clamp(visible - start, 0, lines[i].Length);
                if (i > Mathf.Max(0, cursor - 1)) builder.Append('\n');
                builder.Append(lines[i], 0, count);
            }
            body.text = builder.ToString();
        }

        void LateUpdate() => Tick(Time.unscaledDeltaTime);

        public void Tick(float deltaTime)
        {
            if (!content) return;
            if (!content.activeSelf) return;
            if (Time.unscaledTime >= nextSubsystemScan) ScanSubsystems();
            if (revealed < revealTotal)
            {
                revealed = Mathf.Min(revealTotal, revealed + rate * deltaTime);
                Render();
                if (revealed >= revealTotal) idle = 0;
            }
            else if (!summoned) idle += deltaTime;
            float dismissAfter = Mathf.Clamp(minimumIdleSeconds + FullText.Length * .03f, minimumIdleSeconds, maximumIdleSeconds);
            bool wanted = summoned || Typing || (FullText.Length > 0 || Indicator != DialogueIndicator.None) && idle < dismissAfter;
            float target = wanted ? 1 : 0;
            if (alpha != target || previousAlpha > .42f)
            {
                alpha = Mathf.MoveTowards(alpha, target, deltaTime / fadeSeconds);
                previousAlpha = Mathf.MoveTowards(previousAlpha, .42f, deltaTime / .5f);
                ApplyColors();
                if (alpha <= 0 && !wanted) { content.SetActive(false); return; }
            }
            UpdateIndicator(deltaTime);
            Follow(deltaTime);
        }

        void UpdateIndicator(float deltaTime)
        {
            if (Indicator == DialogueIndicator.None) return;
            indicatorClock += deltaTime;
            int phase = (int)(indicatorClock * 3) % 4;
            if (phase == indicatorPhase) return;
            indicatorPhase = phase;
            var frames = Indicator == DialogueIndicator.Listening ? ListeningFrames : ThinkingFrames;
            indicatorText.text = Indicator == DialogueIndicator.Thinking && indicatorName.Length > 0 ? indicatorName + " · " + frames[phase] : frames[phase];
        }

        void ApplyColors()
        {
            if (!content) return;
            float a = alpha;
            // Brand glass: dark card whose hairline takes the speaker's role colour; the outer rim plate stays clear.
            var rim = new Color(roleColor.r, roleColor.g, roleColor.b, .85f * a);
            block.Clear();
            block.SetColor(ColorId, new Color(style.cardTint.r, style.cardTint.g, style.cardTint.b, style.cardTint.a * a));
            block.SetColor(RimId, rim); block.SetColor(RimBId, rim); card.SetPropertyBlock(block);
            block.Clear(); block.SetColor(ColorId, Color.clear); block.SetColor(RimId, Color.clear); block.SetColor(RimBId, Color.clear); border.SetPropertyBlock(block);
            block.Clear(); block.SetColor(ColorId, new Color(roleColor.r, roleColor.g, roleColor.b, .96f * a)); chip.SetPropertyBlock(block);
            var ink = warning ? style.warning : style.ink;
            body.color = new Color(ink.r, ink.g, ink.b, a);
            nameText.color = new Color(roleColor.r, roleColor.g, roleColor.b, a);
            initial.color = new Color(style.chipInk.r, style.chipInk.g, style.chipInk.b, a);
            indicatorText.color = new Color(style.mutedInk.r, style.mutedInk.g, style.mutedInk.b, a);
            previous.color = new Color(style.mutedInk.r, style.mutedInk.g, style.mutedInk.b, a * previousAlpha);
        }

        void Follow(float deltaTime)
        {
            if (!viewer) { var main = Camera.main; if (!main) return; viewer = main.transform; }
            Vector3 head = viewer.position;
            Vector3 forward = Vector3.ProjectOnPlane(viewer.forward, Vector3.up);
            float headYaw = forward.sqrMagnitude > 1e-4f ? Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg : yaw;
            if (snap)
            {
                yaw = headYaw; anchor = head; following = moving = false; yawVelocity = pitchVelocity = 0; anchorVelocity = Vector3.zero;
                TargetPitch = SolvePitch(head); pitch = TargetPitch; snap = false;
            }
            else if (deltaTime > 0)
            {
                if (Mathf.Abs(Mathf.DeltaAngle(yaw, headYaw)) > yawDeadZone) following = true;
                if (following)
                {
                    yaw = Mathf.SmoothDampAngle(yaw, headYaw, ref yawVelocity, followSeconds, Mathf.Infinity, deltaTime);
                    if (Mathf.Abs(Mathf.DeltaAngle(yaw, headYaw)) < 1.5f) { following = false; yawVelocity = 0; }
                }
                float lag = Mathf.DeltaAngle(yaw, headYaw);
                if (Mathf.Abs(lag) > maxYawLag) yaw = headYaw - Mathf.Sign(lag) * maxYawLag;
                if ((head - anchor).magnitude > positionDeadZone) moving = true;
                if (moving)
                {
                    anchor = Vector3.SmoothDamp(anchor, head, ref anchorVelocity, followSeconds, Mathf.Infinity, deltaTime);
                    if ((head - anchor).magnitude < .01f) { moving = false; anchorVelocity = Vector3.zero; }
                }
                var offset = anchor - head;
                if (offset.magnitude > maxPositionLag) anchor = head + offset.normalized * maxPositionLag;
                TargetPitch = SolvePitch(anchor);
                pitch = Mathf.SmoothDamp(pitch, TargetPitch, ref pitchVelocity, followSeconds, Mathf.Infinity, deltaTime);
            }
            var direction = Quaternion.Euler(pitch, yaw, 0) * Vector3.forward;
            var position = anchor + direction * style.distance;
            transform.SetPositionAndRotation(position, Quaternion.LookRotation(direction, Vector3.up));
        }

        // Horizon-relative pitch below eye level, pushed further down past avoided panels,
        // and never so high that the card's top edge reaches the protected face.
        float SolvePitch(Vector3 eye)
        {
            float half = Mathf.Atan2(CardHeight / 2, style.distance) * Mathf.Rad2Deg;
            float halfWidth = Mathf.Atan2(CardWidth / 2, style.distance) * Mathf.Rad2Deg;
            float result = style.pitchDegrees;
            for (int pass = 0; pass < 3; pass++)
            {
                bool moved = false;
                foreach (var renderer in avoid)
                {
                    if (!renderer || !renderer.gameObject.activeInHierarchy) continue;
                    var bounds = renderer.bounds;
                    var flat = new Vector3(bounds.center.x - eye.x, 0, bounds.center.z - eye.z);
                    float distance = flat.magnitude; if (distance < .05f) continue;
                    float centerYaw = Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg;
                    float spanYaw = Mathf.Atan2(Mathf.Max(bounds.extents.x, bounds.extents.z), distance) * Mathf.Rad2Deg;
                    if (Mathf.Abs(Mathf.DeltaAngle(centerYaw, yaw)) > spanYaw + halfWidth) continue;
                    float top = -Mathf.Atan2(bounds.max.y - eye.y, distance) * Mathf.Rad2Deg; // pitch-down degrees
                    float bottom = -Mathf.Atan2(bounds.min.y - eye.y, distance) * Mathf.Rad2Deg;
                    if (result + half > top && result - half < bottom) { result = bottom + half + 1.5f; moved = true; }
                }
                if (!moved) break;
            }
            if (protectedFace)
            {
                var flat = new Vector3(protectedFace.position.x - eye.x, 0, protectedFace.position.z - eye.z);
                float facePitch = -Mathf.Atan2(protectedFace.position.y - eye.y, Mathf.Max(flat.magnitude, .05f)) * Mathf.Rad2Deg;
                result = Mathf.Max(result, facePitch + faceMarginDegrees + half);
            }
            return Mathf.Min(result, maximumPitchDegrees);
        }

        void ScanSubsystems()
        {
            nextSubsystemScan = Time.unscaledTime + 2;
            SubsystemManager.GetSubsystems(inputs);
            foreach (var system in inputs)
                if (!subscribed.Contains(system)) { subscribed.Add(system); system.trackingOriginUpdated += OriginUpdated; }
        }

        void OriginUpdated(XRInputSubsystem system) => Recenter();
    }
}
