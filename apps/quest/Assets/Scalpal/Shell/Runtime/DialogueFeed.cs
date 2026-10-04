using Scalpal.EncounterOffice;
using Scalpal.Quest;
using Scalpal.Surgery;
using Scalpal.Voice;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Scalpal.Shell
{
    // Routes the existing transcript and caption events into the shared dialogue box.
    // Office: NativeEncounterSession.Line (voice transcripts, authored greeting, visual answers, Jarvis feedback).
    // OR: QuestJarvisVoice.Transcript (learner + Jarvis coach) and OpenSurgeryCoach.Captioned (authored coaching).
    // Read-only listener: it never connects, mutes or configures voice.
    public sealed class DialogueFeed : MonoBehaviour
    {
        public DialogueBox box;
        public NativeEncounterSession Office { get; private set; }
        public NativeCaseSession Surgery { get; private set; }
        public QuestJarvisVoice Voice { get; private set; }
        OpenSurgeryCoach coach;
        string lastMode = "listening", responder = "";
        bool wasHeld;
        float awaitingUntil, nextCoachLookup;
        const float ReplyTimeout = 10;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            SceneManager.sceneLoaded -= Loaded; SceneManager.sceneLoaded += Loaded;
            Attach();
        }
        static void Loaded(Scene scene, LoadSceneMode mode) => Attach();

        // One box per conversational scene; scenes without a session get none.
        public static DialogueFeed Attach()
        {
            var office = FindFirstObjectByType<NativeEncounterSession>();
            var surgery = office ? null : FindFirstObjectByType<NativeCaseSession>();
            if (!office && !surgery) return null;
            var existing = FindFirstObjectByType<DialogueFeed>();
            if (existing) return existing;
            var style = DialogueBoxStyle.Load();
            if (!style) { Debug.LogWarning("SCALPAL_DIALOGUE_BOX_STYLE_MISSING Resources/" + DialogueBoxStyle.ResourcePath); return null; }
            var box = DialogueBox.Create(style, Camera.main ? Camera.main.transform : null);
            var feed = box.gameObject.AddComponent<DialogueFeed>(); feed.box = box;
            if (office) feed.BindOffice(office); else feed.BindSurgery(surgery);
            return feed;
        }

        public void BindOffice(NativeEncounterSession session)
        {
            Unbind();
            Office = session; Voice = session.voice;
            session.Line += OfficeLine;
            var rig = FindFirstObjectByType<EncounterOfficeRig>();
            if (rig && rig.head) box.viewer = rig.head.transform;
            var panel = FindFirstObjectByType<EncounterOfficePanel>();
            box.avoid.Clear();
            // Each console's first renderer is its frosted glass card.
            foreach (var console in new[] { panel ? panel.assessment : null, panel ? panel.keyboard : null })
                if (console) { var glass = console.GetComponentInChildren<Renderer>(true); if (glass) box.avoid.Add(glass); }
            ProtectFace();
        }

        public void BindSurgery(NativeCaseSession session)
        {
            Unbind();
            Surgery = session; Voice = session.voice;
            if (Voice) Voice.Transcript += SurgeryTranscript;
            box.avoid.Clear(); box.protectedFace = null;
        }

        void Unbind()
        {
            if (Office) Office.Line -= OfficeLine;
            if (Surgery && Voice) Voice.Transcript -= SurgeryTranscript;
            if (coach) coach.Captioned -= CoachCaption;
            Office = null; Surgery = null; Voice = null; coach = null;
        }
        void OnDestroy() => Unbind();

        void ProtectFace()
        {
            var presentation = Office ? Office.patient : null;
            box.protectedFace = null;
            if (!presentation) return;
            foreach (var model in new[] { presentation.female, presentation.male })
            {
                if (!model || !model.activeInHierarchy) continue;
                foreach (var child in model.GetComponentsInChildren<Transform>())
                    if (child.name == "NoseTip") { box.protectedFace = child; return; }
            }
        }

        // speaker: learner | patient | parent | attending (NativeEncounterSession.Line contract).
        void OfficeLine(string speaker, string text)
        {
            if (speaker == "learner")
            {
                box.Say(DialogueSpeaker.You, "", text);
                Await();
                return;
            }
            awaitingUntil = 0;
            var state = Office ? Office.State : null;
            if (speaker == "attending") box.Say(DialogueSpeaker.Attending, "", text);
            else if (speaker == "parent") box.Say(DialogueSpeaker.Parent, state?.speakerName, text);
            else box.Say(DialogueSpeaker.Patient, state?.patientName, text);
            if (!box.protectedFace || !box.protectedFace.gameObject.activeInHierarchy) ProtectFace();
        }

        void SurgeryTranscript(string source, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (source == "user") { box.Say(DialogueSpeaker.You, "", text); awaitingUntil = Time.unscaledTime + ReplyTimeout; }
            else { awaitingUntil = 0; box.Say(DialogueSpeaker.Coach, "", text); }
        }

        void CoachCaption(string text, bool warning) => box.Say(DialogueSpeaker.Coach, "", text, warning);

        void Update()
        {
            if (!box) return;
            if (Surgery && !coach && Time.unscaledTime >= nextCoachLookup)
            {
                nextCoachLookup = Time.unscaledTime + 1;
                if (Surgery.TryGetComponent(out coach)) coach.Captioned += CoachCaption;
            }
            // Speech for the current line ended: finish the typewriter immediately.
            string mode = Voice ? Voice.Mode : "listening";
            if (mode != lastMode)
            {
                if (lastMode == "speaking" && box.Speaker != DialogueSpeaker.You) box.CompleteLine();
                lastMode = mode;
            }
            if (Office) UpdateOfficeIndicator(); else if (Surgery) UpdateSurgeryIndicator();
        }

        void UpdateOfficeIndicator()
        {
            bool held = Office.TalkHeld;
            if (held && !wasHeld) { box.BeginTurn(DialogueSpeaker.You, ""); awaitingUntil = 0; }
            // Released talk on a live connection: the reply (and the learner's transcript) are on the way.
            if (!held && wasHeld && Voice && Voice.Connected) Await();
            wasHeld = held;
            bool openListening = Office.OpenMicrophone && Voice && Voice.Connected && Voice.Mode == "listening";
            if (held) box.SetIndicator(DialogueIndicator.Listening, "", true);
            else if (Time.unscaledTime < awaitingUntil && (!Voice || Voice.Mode != "speaking")) box.SetIndicator(DialogueIndicator.Thinking, responder, true);
            else if (openListening) box.SetIndicator(DialogueIndicator.Listening, "", false);
            else box.SetIndicator(DialogueIndicator.None);
        }

        void UpdateSurgeryIndicator()
        {
            // The OR microphone is open; listening is a passive hint that never summons the box.
            if (Time.unscaledTime < awaitingUntil && Voice && Voice.Mode != "speaking") box.SetIndicator(DialogueIndicator.Thinking, "Jarvis", true);
            else if (Voice && Voice.Connected && Voice.Mode == "listening") box.SetIndicator(DialogueIndicator.Listening, "", false);
            else box.SetIndicator(DialogueIndicator.None);
        }

        void Await() { awaitingUntil = Time.unscaledTime + ReplyTimeout; responder = OfficeResponder(); }

        string OfficeResponder()
        {
            if (Office.Role == "attending") return "Jarvis";
            var state = Office.State;
            string name = state == null ? "" : state.speaker == "parent" ? state.speakerName : state.patientName;
            if (string.IsNullOrWhiteSpace(name)) return "";
            int space = name.IndexOf(' ');
            return space > 0 ? name.Substring(0, space) : name;
        }
    }
}
