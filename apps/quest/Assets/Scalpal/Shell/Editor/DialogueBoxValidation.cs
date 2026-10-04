using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Scalpal.EncounterOffice;
using Scalpal.Exercises.Coach;
using Scalpal.Quest;
using Scalpal.Surgery;
using Scalpal.Voice;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Scalpal.Shell.Editor
{
    // Creates the one style asset the dialogue box reads and points it at the Scalpal brand
    // (Resources/ScalpalBrand): fonts, materials and palette are synced; tuned geometry and pacing are kept.
    public static class DialogueBoxBuild
    {
        public const string StylePath = "Assets/Scalpal/Shell/Resources/" + DialogueBoxStyle.ResourcePath + ".asset";
        [MenuItem("Scalpal/Shell/Prepare Dialogue Box Style")]
        public static DialogueBoxStyle PrepareStyle()
        {
            var brand = Scalpal.Brand.Editor.ScalpalBrandBuild.Prepare();
            var style = AssetDatabase.LoadAssetAtPath<DialogueBoxStyle>(StylePath);
            if (!style)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StylePath)); AssetDatabase.Refresh();
                style = ScriptableObject.CreateInstance<DialogueBoxStyle>();
                AssetDatabase.CreateAsset(style, StylePath);
            }
            style.bodyFont = brand.body; style.nameFont = brand.label;
            style.glass = brand.glass; style.bodyText = brand.bodyText; style.nameText = brand.labelText;
            style.chip = brand.accent; style.button = brand.button;
            // Brand palette: dark glass, white ink, spark-accent roles (geometry and pacing stay as tuned).
            var palette = ScriptableObject.CreateInstance<DialogueBoxStyle>();
            style.cardTint = palette.cardTint; style.ink = palette.ink; style.mutedInk = palette.mutedInk; style.chipInk = palette.chipInk;
            style.you = palette.you; style.patient = palette.patient; style.parent = palette.parent; style.attending = palette.attending; style.coach = palette.coach; style.warning = palette.warning;
            UnityEngine.Object.DestroyImmediate(palette);
            EditorUtility.SetDirty(style); AssetDatabase.SaveAssets();
            return style;
        }
        static T Load<T>(string path) where T : UnityEngine.Object
        {
            var value = AssetDatabase.LoadAssetAtPath<T>(path);
            if (!value) throw new InvalidOperationException("Dialogue box style source missing: " + path);
            return value;
        }
    }

    // Editor gates for the shared dialogue box. Throws so -executeMethod fails; no headset claim.
    public static class DialogueBoxValidation
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        const float Frame = 1f / 72;
        static int checks;

        [MenuItem("Scalpal/Shell/Validate Dialogue Box")]
        public static void Run()
        {
            checks = 0;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var style = DialogueBoxStyle.Load();
            Check(style && style.bodyFont && style.nameFont && style.glass && style.bodyText && style.nameText && style.chip && style.button, "Resources/DialogueBoxStyle binds fonts and materials from one place");
            var brand = Scalpal.Brand.ScalpalBrand.Active;
            Check(style.bodyFont == brand.body && style.nameFont == brand.label && style.glass == brand.glass && style.bodyText == brand.bodyText && style.nameText == brand.labelText && style.button == brand.button, "box takes Geist Mono SDF type, dark glass and buttons from the Scalpal brand");
            var head = new GameObject("ValidationHead").AddComponent<Camera>();
            head.transform.SetPositionAndRotation(new Vector3(0, 1.6f, 0), Quaternion.identity); head.tag = "MainCamera";
            var box = DialogueBox.Create(style, head.transform);
            try
            {
                Readability(box, style);
                Speakers(box);
                Typewriter(box);
                Follow(box, head.transform);
                Allocations(box, head.transform);
                Choices(box, head.transform);
                SurgeryFeed(style, head.transform);
                OfficeFeed(style, head.transform);
            }
            finally { UnityEngine.Object.DestroyImmediate(box.gameObject); UnityEngine.Object.DestroyImmediate(head.gameObject); }
            Debug.Log("SCALPAL_DIALOGUE_BOX_VERIFY_OK checks=" + checks + " editorOnly=true headset=false");
        }

        static void Readability(DialogueBox box, DialogueBoxStyle style)
        {
            Check(Mathf.Abs(style.distance - 1) < .05f && box.BodyGlyphHeight >= .024f, "measured body line height is at least 24 mm at 1 m: " + box.BodyGlyphHeight);
            // Worst case: translucent card over the bright office wall. The project blends in linear space.
            var wall = new Color(.88f, .90f, .85f);
            float card = style.cardTint.a * Luminance(style.cardTint) + (1 - style.cardTint.a) * Luminance(wall);
            float ratio = (Luminance(style.ink) + .05f) / (card + .05f);
            Check(ratio >= 7, "body ink on the glass card over a bright wall keeps at least 7:1 contrast: " + ratio);
            Check(box.CardHeight < .26f && box.CardWidth <= .85f, "card stays a compact lower-middle strip");
            Debug.Log("SCALPAL_DIALOGUE_BOX_READABILITY bodyGlyphMm=" + (box.BodyGlyphHeight * 1000).ToString("F1") + " contrast=" + ratio.ToString("F1") + " cardMetres=" + box.CardWidth.ToString("F2") + "x" + box.CardHeight.ToString("F3"));
        }

        static void Speakers(DialogueBox box)
        {
            var expected = new[] {
                (DialogueSpeaker.Patient, "Priya Ramaswamy", "Priya Ramaswamy · Patient"),
                (DialogueSpeaker.Parent, "Laura Abernathy", "Laura Abernathy · Parent"),
                (DialogueSpeaker.Attending, "", "Scalpal · Attending"),
                (DialogueSpeaker.Coach, "", "Scalpal · Coach"),
                (DialogueSpeaker.You, "", "You") };
            foreach (var (speaker, name, label) in expected)
            {
                box.Say(speaker, name, "Line from " + label);
                var nameText = box.transform.Find("Card/Name").GetComponent<TMPro.TextMeshPro>();
                var color = box.RoleColor(speaker);
                Check(box.Speaker == speaker && box.SpeakerLabel == label && nameText.text == label, "speaker name tag reads " + label);
                Check(Mathf.Abs(nameText.color.r - color.r) < .01f && Mathf.Abs(nameText.color.g - color.g) < .01f, "name tag uses its role colour: " + label);
                Check(box.transform.Find("Card/Initial").GetComponent<TMPro.TextMeshPro>().text.Length == 1, "role chip carries a single initial: " + label);
            }
            Check(expected.Select(e => box.RoleColor(e.Item1)).Distinct().Count() == expected.Length, "each role has a distinct colour");
            Check(DialogueBox.Label(DialogueSpeaker.Patient, null) == "Patient" && DialogueBox.Label(DialogueSpeaker.Parent, " ") == "Parent", "missing patient or parent name falls back to the role label");
            box.Clear();
        }

        static void Typewriter(DialogueBox box)
        {
            box.Say(DialogueSpeaker.Patient, "Priya Ramaswamy", "It started around my belly button last night.");
            string sentence = "The pain moved down to the right side this morning and it hurts more when I walk, cough or go over bumps in the car on the way here, and I have not wanted to eat anything since.";
            box.Say(DialogueSpeaker.Patient, "Priya Ramaswamy", sentence);
            Check(box.PreviousText.StartsWith("Priya Ramaswamy · Patient:", StringComparison.Ordinal) && box.PreviousText.Contains("belly button"), "previous line moves above the new line with its speaker");
            var previousMesh = box.transform.Find("Card/Previous").GetComponent<TMPro.TextMeshPro>();
            Tick(box, .1f);
            Check(box.Typing && box.VisibleText.Length > 0 && box.VisibleText.Length < 12, "text types out rather than appearing at once: '" + box.VisibleText + "'");
            Tick(box, 1.5f);
            Check(previousMesh.color.a < .5f && previousMesh.color.a > .3f, "previous line fades to a muted level: " + previousMesh.color.a);
            Check(box.VisibleText.Split('\n').Length <= 2, "current line never shows more than two wrapped lines");
            box.CompleteLine();
            Check(!box.Typing && box.VisibleText.Replace("\n", " ").EndsWith("eat anything since.", StringComparison.Ordinal), "line end completes the typewriter instantly");
            Check(box.VisibleText.Split('\n').Length <= 2, "completed long line is clipped to its last two lines");
            box.Say(DialogueSpeaker.Attending, "", "Good. What is your leading diagnosis?");
            for (int i = 0; i < 72 * 4 && box.Typing; i++) box.Tick(Frame);
            Check(!box.Typing && box.VisibleText.Contains("leading diagnosis?"), "typewriter finishes by itself at roughly speech rate");
            for (int i = 0; i < 72 * 14; i++) box.Tick(Frame);
            Check(!box.Visible, "box auto-dismisses after idle");
            box.SetIndicator(DialogueIndicator.Listening, "", false);
            Tick(box, .5f);
            Check(!box.Visible, "a passive listening hint never summons a dismissed box");
            box.BeginTurn(DialogueSpeaker.You, ""); box.SetIndicator(DialogueIndicator.Listening, "", true); Tick(box, .4f);
            var indicator = box.transform.Find("Card/Indicator").GetComponent<TMPro.TextMeshPro>();
            Check(box.Visible && box.SpeakerLabel == "You" && indicator.text.StartsWith("listening", StringComparison.Ordinal), "held talk shows a You turn with a listening indicator");
            box.Say(DialogueSpeaker.You, "", "Where does it hurt?");
            box.SetIndicator(DialogueIndicator.Thinking, "Priya", true); Tick(box, .4f);
            Check(box.SpeakerLabel == "You" && box.PreviousText.Contains("leading diagnosis") && indicator.text.StartsWith("Priya · thinking", StringComparison.Ordinal), "transcript fills the listening turn and the reply shows a thinking indicator");
            box.SetIndicator(DialogueIndicator.None); box.Clear();
        }

        static void Follow(DialogueBox box, Transform head)
        {
            head.SetPositionAndRotation(new Vector3(0, 1.6f, 0), Quaternion.identity);
            box.Say(DialogueSpeaker.Coach, "", "Good exposure. Now find the base of the appendix.");
            box.Tick(0);
            Bounds(box, head, "first placement");
            var placed = box.transform.position;
            head.rotation = Quaternion.Euler(0, 12, 0); Tick(box, 1);
            Check((box.transform.position - placed).magnitude < .001f, "small head turns inside the dead zone do not move the box (not head-locked)");
            head.rotation = Quaternion.Euler(35, 12, 0); Tick(box, 1);
            Check((box.transform.position - placed).magnitude < .001f, "looking down at the field does not drag the box (horizon-relative pitch)");
            head.rotation = Quaternion.Euler(0, 80, 0); box.Tick(Frame);
            float lag = Mathf.Abs(Mathf.DeltaAngle(Yaw(box, head), 80));
            Check(lag > 25 && lag <= box.maxYawLag + .5f, "a large turn lags behind the head but stays within the bounded yaw lag: " + lag);
            Tick(box, 2);
            Check(Mathf.Abs(Mathf.DeltaAngle(Yaw(box, head), 80)) < 2, "box settles back in front after a large turn");
            Bounds(box, head, "after follow");
            head.position += new Vector3(.04f, 0, .04f); var still = box.transform.position; Tick(box, 1);
            Check((box.transform.position - still).magnitude < .001f, "small posture sway does not move the box");
            head.position += new Vector3(.6f, 0, 0); box.Tick(Frame);
            Check((box.transform.position - head.position).magnitude < box.style.distance + box.maxPositionLag + .05f, "walking keeps the box within a bounded positional lag");
            Tick(box, 2); Bounds(box, head, "after walking");
            head.rotation = Quaternion.Euler(0, 95, 0); Tick(box, 1);
            Check(Mathf.Abs(Mathf.DeltaAngle(Yaw(box, head), 95)) > 10, "precondition: box lags a dead-zone turn before recenter");
            box.Recenter();
            Check(Mathf.Abs(Mathf.DeltaAngle(Yaw(box, head), 95)) < .01f, "recenter snaps the box in front of the current view");
            // Avoidance: a console in the lower middle pushes the box below it; a face caps how high it may go.
            var console = GameObject.CreatePrimitive(PrimitiveType.Quad); console.name = "ValidationConsole";
            var face = new GameObject("ValidationFace").transform;
            try
            {
                head.SetPositionAndRotation(new Vector3(0, 1.2f, 0), Quaternion.identity);
                console.transform.position = new Vector3(0, .92f, .85f); console.transform.localScale = new Vector3(.64f, .22f, 1);
                box.avoid.Add(console.GetComponent<Renderer>());
                box.Recenter();
                var bounds = console.GetComponent<Renderer>().bounds;
                float consoleBottom = -Mathf.Atan2(bounds.min.y - 1.2f, .85f) * Mathf.Rad2Deg;
                float half = Mathf.Atan2(box.CardHeight / 2, box.style.distance) * Mathf.Rad2Deg;
                Check(box.CurrentPitch - half >= consoleBottom, "box moves below an overlapping lower-middle console: " + box.CurrentPitch);
                box.avoid.Clear(); box.Recenter();
                // A face 18 degrees below eye level forces the card lower than its 22 degree default.
                face.position = new Vector3(0, 1.2f - Mathf.Tan(18 * Mathf.Deg2Rad) * 1.5f, 1.5f);
                box.protectedFace = face; box.Recenter();
                Check(box.CurrentPitch > box.style.pitchDegrees && box.CurrentPitch - half >= 18 + box.faceMarginDegrees - .01f, "box top edge stays below a protected face: " + box.CurrentPitch);
            }
            finally { box.avoid.Clear(); box.protectedFace = null; UnityEngine.Object.DestroyImmediate(console); UnityEngine.Object.DestroyImmediate(face.gameObject); }
            head.SetPositionAndRotation(new Vector3(0, 1.6f, 0), Quaternion.identity); box.Clear();
        }

        static void Bounds(DialogueBox box, Transform head, string label)
        {
            var offset = box.transform.position - head.position;
            float distance = offset.magnitude;
            float below = -Mathf.Asin(offset.y / distance) * Mathf.Rad2Deg;
            Check(distance > .95f && distance < 1.05f, "box sits about 1 m ahead (" + label + "): " + distance);
            Check(below >= 19.5f && below <= 25.5f, "box sits 20-25 degrees below eye level (" + label + "): " + below);
            Check(Vector3.Dot(box.transform.forward, offset.normalized) > .999f, "box faces the viewer (" + label + ")");
        }

        static void Allocations(DialogueBox box, Transform head)
        {
            box.Say(DialogueSpeaker.Patient, "Priya Ramaswamy", "Since last night.");
            box.CompleteLine(); Tick(box, .5f);
            Check(box.Visible && !box.Typing, "precondition: box shows a finished line");
            Check(Allocated(() => box.Tick(Frame)) == 0, "visible idle box allocates nothing per frame");
            Tick(box, 15);
            Check(!box.Visible && Allocated(() => box.Tick(Frame)) == 0, "dismissed box allocates nothing per frame");
            box.Clear();
        }

        static void SurgeryFeed(DialogueBoxStyle style, Transform head)
        {
            var root = new GameObject("ValidationOR"); root.SetActive(false);
            var box = DialogueBox.Create(style, head);
            try
            {
                var session = root.AddComponent<NativeCaseSession>(); var voice = root.AddComponent<QuestJarvisVoice>(); session.voice = voice;
                var feed = box.gameObject.AddComponent<DialogueFeed>(); feed.box = box; feed.BindSurgery(session);
                Call(voice, "Handle", "{\"type\":\"user_transcript\",\"user_transcription_event\":{\"user_transcript\":\"Is this the appendix?\"}}");
                Update(feed);
                Check(box.Speaker == DialogueSpeaker.You && box.SpeakerLabel == "You" && box.FullText == "Is this the appendix?", "OR learner transcript arrives as You");
                Check(box.Indicator == DialogueIndicator.Thinking, "OR shows Scalpal thinking after the learner speaks");
                Call(voice, "Handle", "{\"type\":\"agent_response\",\"agent_response_event\":{\"agent_response\":\"Yes. Trace the taenia coli down to its base before you clamp anything.\"}}");
                Call(voice, "SetMode", "speaking"); Update(feed); box.Tick(.2f);
                Check(box.SpeakerLabel == "Scalpal · Coach" && box.Typing && box.Indicator != DialogueIndicator.Thinking, "OR agent transcript arrives as Scalpal · Coach and types while speaking");
                Call(voice, "SetMode", "listening"); Update(feed);
                Check(!box.Typing, "OR speech end completes the line");
                var coach = root.AddComponent<OpenSurgeryCoach>();
                typeof(DialogueFeed).GetField("nextCoachLookup", Private).SetValue(feed, 0f); Update(feed);
                int captionsBefore = UnityEngine.Object.FindObjectsByType<TextMesh>(FindObjectsInactive.Include, FindObjectsSortMode.None).Count(t => t.name == "Open surgery coach caption");
                Call(coach, "Show", new CoachAlertDto { kind = "mistake", tier = "warning", say = "Stop. That is the ileum, not the appendix." });
                Check(box.SpeakerLabel == "Scalpal · Coach" && box.FullText.StartsWith("Stop.", StringComparison.Ordinal) && coach.LastCaption.StartsWith("Stop.", StringComparison.Ordinal), "authored coach caption arrives as Scalpal · Coach");
                Check(UnityEngine.Object.FindObjectsByType<TextMesh>(FindObjectsInactive.Include, FindObjectsSortMode.None).Count(t => t.name == "Open surgery coach caption") == captionsBefore, "coach draws no duplicate head-locked caption while the box presents it");
                box.CompleteLine(); typeof(DialogueFeed).GetField("awaitingUntil", Private).SetValue(feed, 0f);
                Update(feed); box.Tick(Frame);
                Check(Allocated(() => { Update(feed); box.Tick(Frame); }) == 0, "idle OR feed and box allocate nothing per frame");
            }
            finally { UnityEngine.Object.DestroyImmediate(box.gameObject); UnityEngine.Object.DestroyImmediate(root); }
        }

        // Interview choices live in the box: four selectable rows under the card, hit by the office ray or pinch.
        static void Choices(DialogueBox box, Transform head)
        {
            head.SetPositionAndRotation(new Vector3(0, 1.6f, 0), Quaternion.identity);
            box.Say(DialogueSpeaker.Parent, "Laura Abernathy", "He won't let anyone touch his belly.");
            string longest = string.Join(" ", Enumerable.Repeat("Ask about the onset, the location and how it has moved since last night", 4));
            box.ShowChoices("1/9", "Laura is waiting for you to start. What do you do next?", new[] { "A", "B", "C", "D" },
                new[] { "Ask whether anyone at home smokes", "Ask when the pain started, where it began, and where it is now", "Ask Theo to rate his pain", longest });
            box.CompleteLine(); Tick(box, .6f);
            var rows = box.ChoiceRows;
            Check(box.ChoicesVisible && rows.Count == 4 && rows.Select(r => r.key).SequenceEqual(new[] { "A", "B", "C", "D" }), "a round shows four keyed choice rows");
            Check(rows.All(r => r.GetComponent<BoxCollider>() && r.GetComponent<BoxCollider>().size.x > .7f && r.GetComponent<BoxCollider>().size.y > .02f), "each choice row has a ray/pinch collider across the card width");
            var texts = rows.Select(r => r.GetComponentInChildren<TMPro.TextMeshPro>()).ToArray();
            Check(texts[1].text.StartsWith("B) Ask when the pain started", StringComparison.Ordinal) && texts[3].text.Split('\n').Length == 3 && texts[3].text.EndsWith("…", StringComparison.Ordinal), "choice text carries its key, wraps, and a very long choice is cut at three lines: " + texts[3].text.Replace('\n', '|'));
            var cardBottom = box.transform.TransformPoint(new Vector3(0, -box.CardHeight / 2, 0)).y;
            Check(rows.All(r => r.GetComponent<Renderer>().bounds.max.y < cardBottom), "choices sit below the dialogue line, never over it");
            float bottom = rows.Min(r => r.GetComponent<Renderer>().bounds.min.y);
            float glance = -Mathf.Atan2(bottom - head.position.y, Vector3.Distance(new Vector3(box.transform.position.x, 0, box.transform.position.z), new Vector3(head.position.x, 0, head.position.z))) * Mathf.Rad2Deg;
            Check(glance < 50, "the last choice stays within a comfortable downward glance: " + glance);
            string picked = null; box.ChoicePicked += key => picked = key;
            rows[2].Highlight(); box.Tick(Frame);
            rows[2].Press();
            Check(picked == "C", "pressing a row (ray trigger or pinch) reports its key");
            box.SetChoiceNote("Say A, B, C or D, or tap one. Heard: \"um\"");
            Check(box.ChoiceNote.StartsWith("Say A, B, C or D", StringComparison.Ordinal) && box.transform.Find("Card/Choices/ChoiceNote").GetComponent<TMPro.TextMeshPro>().text.Length > 0, "an unclear spoken answer shows the re-ask note under the choices");
            for (int i = 0; i < 72 * 20; i++) box.Tick(Frame);
            Check(box.Visible && box.ChoicesVisible, "the box does not auto-dismiss while a round waits for an answer");
            Check(Allocated(() => box.Tick(Frame)) == 0, "a visible round allocates nothing per frame");
            box.HideChoices(); picked = null; rows = box.ChoiceRows;
            Check(!box.ChoicesVisible, "choices hide while the patient speaks");
            box.Pick("A"); Check(picked == null, "hidden choices cannot be picked");
            box.Clear();
        }

        static void OfficeFeed(DialogueBoxStyle style, Transform head)
        {
            var root = new GameObject("ValidationOffice"); root.SetActive(false);
            var box = DialogueBox.Create(style, head);
            try
            {
                var session = root.AddComponent<NativeEncounterSession>();
                var feed = box.gameObject.AddComponent<DialogueFeed>(); feed.box = box; feed.BindOffice(session);
                SetProperty(session, "State", new EncounterState { encounterId = "int-validation01", patientId = "patient-demo-multi-source", patientName = "Priya Ramaswamy", speakerName = "Priya Ramaswamy", speaker = "patient", phase = "interview" });
                typeof(NativeEncounterSession).GetField("encounterId", Private).SetValue(session, "int-validation01");
                Call(session, "Transcript", "agent", "It hurts on the right side.");
                Check(box.SpeakerLabel == "Priya Ramaswamy · Patient", "office patient voice transcript labels the patient by name");
                Call(session, "Transcript", "agent", "[slow] It hurts [wince] when I move.");
                Check(session.LastResponse == "It hurts when I move.", "voice delivery tags like [slow] never reach the patient's transcript");
                Call(session, "Transcript", "user", "[CLINICIAN] Ask where it hurts");
                Check(box.SpeakerLabel == "Priya Ramaswamy · Patient", "the agent's echo of a pick is not shown as learner speech");
                session.State.speaker = "parent"; session.State.patientName = "Theo Abernathy"; session.State.speakerName = "Laura Abernathy";
                Call(session, "Transcript", "agent", "He has been coughing at night.");
                Check(box.SpeakerLabel == "Laura Abernathy · Parent", "office parent speaker is labelled with the parent's name");
                var round = new InterviewRoundView { roundId = "history_onset", number = 1, of = 9, stage = "history", prompt = "What do you do next?",
                    choices = new[] { "A", "B", "C", "D" }.Select(k => new InterviewChoiceView { key = k, text = "Choice " + k }).ToArray() };
                typeof(NativeEncounterSession).GetField("round", Private).SetValue(session, round);
                SetProperty(session, "AwaitingPatient", true); Update(feed);
                Check(!box.ChoicesVisible, "no round is shown while the patient is still speaking");
                SetProperty(session, "AwaitingPatient", false); Update(feed); Tick(box, .6f);
                Check(box.ChoicesVisible && box.ChoiceHeader == "1/9" && box.ChoicePrompt == "What do you do next?" && box.ChoiceRows.Count == 4, "the round appears in the box once the patient has finished");
                box.ChoiceRows[1].Press();
                var pending = (System.Collections.IEnumerable)typeof(NativeEncounterSession).GetField("pending", Private).GetValue(session);
                var operation = pending.Cast<object>().Last();
                Check(Field<string>(operation, "path") == "/interviews/int-validation01/answer" && Field<string>(operation, "body") == "{\"key\":\"B\"}" && session.Busy,
                    "a picked row posts {key} to the interview answer route");
                Check(box.transform.Find("Card/Choices/ChoiceNote").GetComponent<TMPro.TextMeshPro>().text == DialogueBox.ChoiceHint, "with no note the box's last line is the hint: " + DialogueBox.ChoiceHint);
                SetProperty(session, "LastFinding", new InterviewFinding { label = "CBC", text = "WBC 14.2", abnormal = true }); Update(feed);
                Check(box.ChoiceResult == "Result: CBC: WBC 14.2" && box.transform.Find("Card/Choices/ChoiceResult").gameObject.activeSelf, "an exam or test finding shows as one Result line inside the box");
                SetProperty(session, "AnswerNote", "Say A, B, C or D, or tap one."); Update(feed);
                Check(box.ChoiceNote == "Say A, B, C or D, or tap one.", "the session's re-ask note reaches the box");
                SetProperty(session, "TalkHeld", true); Update(feed);
                Check(box.SpeakerLabel == "You" && box.Indicator == DialogueIndicator.Listening && box.Visible, "holding the answer button opens a listening You turn");
                SetProperty(session, "TalkHeld", false); Update(feed);
                Check(box.Indicator == DialogueIndicator.None, "no voice connection means no pending reply indicator");
                typeof(NativeEncounterSession).GetField("round", Private).SetValue(session, null); Update(feed);
                Check(!box.ChoicesVisible, "after the last pick the choices close");
            }
            finally { UnityEngine.Object.DestroyImmediate(box.gameObject); UnityEngine.Object.DestroyImmediate(root); }
        }

        static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(target);
        static float Yaw(DialogueBox box, Transform head)
        {
            var flat = Vector3.ProjectOnPlane(box.transform.position - head.position, Vector3.up);
            return Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg;
        }
        static void Tick(DialogueBox box, float seconds) { for (float t = 0; t < seconds; t += Frame) box.Tick(Frame); }
        static long Allocated(Action frame)
        {
            for (int i = 0; i < 10; i++) frame();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 300; i++) frame();
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
        static float Luminance(Color color)
        {
            float Channel(float c) => c <= .03928f ? c / 12.92f : Mathf.Pow((c + .055f) / 1.055f, 2.4f);
            return .2126f * Channel(color.r) + .7152f * Channel(color.g) + .0722f * Channel(color.b);
        }
        static void Update(DialogueFeed feed) => typeof(DialogueFeed).GetMethod("Update", Private).Invoke(feed, null);
        static void Call(object target, string method, params object[] arguments)
        {
            var info = target.GetType().GetMethods(Private | BindingFlags.Public).First(m => m.Name == method && m.GetParameters().Length == arguments.Length);
            info.Invoke(target, arguments);
        }
        static void SetProperty(object target, string name, object value) =>
            target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SetValue(target, value);
        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Dialogue box validation: " + message);
            checks++;
        }
    }
}
