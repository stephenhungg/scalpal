using System;
using System.IO;
using System.Reflection;
using Scalpal.Voice;

static class Program
{
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    static int Main()
    {
        int rate;
        Check(QuestJarvisVoice.TryPcmRate("pcm_16000", out rate) && rate == 16000, "input negotiation");
        Check(QuestJarvisVoice.TryPcmRate("pcm_44100", out rate) && rate == 44100, "output negotiation");
        Check(!QuestJarvisVoice.TryPcmRate("mp3_44100", out rate), "compressed output must fail explicitly");
        Check(!QuestJarvisVoice.TryPcmRate("ulaw_8000", out rate), "mu-law must fail explicitly");
        Check(!QuestJarvisVoice.TryPcmRate("pcm_123", out rate), "unrecognized sample rate");
        var encoded = QuestJarvisVoice.EncodePcm(new[] { -1f, 0f, 1f }, 1);
        Check(encoded.Length == 6 && encoded[0] == 0 && encoded[1] == 128 && encoded[4] == 255 && encoded[5] == 127, "signed little-endian PCM");
        var decoded = QuestJarvisVoice.DecodePcm(encoded);
        Check(decoded[0] == -1f && decoded[1] == 0f && Math.Abs(decoded[2] - 1f) < 0.00004f, "PCM roundtrip");
        var stereo = QuestJarvisVoice.DecodePcm(QuestJarvisVoice.EncodePcm(new[] { 1f, -1f, 0.5f, 0.5f }, 2));
        Check(stereo.Length == 2 && stereo[0] == 0f && Math.Abs(stereo[1] - 0.5f) < 0.00004f, "stereo microphone downmix");
        var muted = QuestJarvisVoice.DecodePcm(QuestJarvisVoice.EncodeMicrophonePcm(new[] { 1f, -1f, .75f, .25f }, 2, true));
        Check(muted.Length == 2 && muted[0] == 0 && muted[1] == 0, "muted hold-to-talk sends timed zero PCM, never captured words");
        var held = QuestJarvisVoice.DecodePcm(QuestJarvisVoice.EncodeMicrophonePcm(new[] { .75f, .25f }, 2, false));
        Check(held.Length == 1 && Math.Abs(held[0] - .5f) < .00004f, "held microphone uses the normal PCM downmix");
        Check(QuestJarvisVoice.MeasurePlaybackLevel(new[] { 0f, 0f }) == 0, "silent playback closes mouth");
        Check(Math.Abs(QuestJarvisVoice.MeasurePlaybackLevel(new[] { .1f, -.1f }) - .552f) < .0001f, "mouth envelope uses RMS, not cancelling signed sample average");
        Check(QuestJarvisVoice.MeasurePlaybackLevel(new[] { 1f, -1f }) == 1, "loud playback envelope remains bounded");
        Check(QuestJarvisVoice.MeasurePlaybackLevel(new[] { float.NaN, float.PositiveInfinity }) == 0, "invalid audio cannot poison jaw transforms");
        bool rejected = false;
        try { QuestJarvisVoice.DecodePcm(new byte[] { 1 }); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "partial PCM sample must be rejected");
        var extractor = typeof(QuestJarvisVoice).GetMethod("ExtractObject", BindingFlags.Static | BindingFlags.NonPublic);
        const string json = "{\"client_tool_call\":{\"parameters\":{\"note\":\"brace } and quote \\\"\",\"nested\":{\"x\":1}}}}";
        var parameters = (string)extractor.Invoke(null, new object[] { json, "parameters" });
        Check(parameters.StartsWith("{\"note\"") && parameters.EndsWith("{\"x\":1}}"), "preserve nested/escaped tool parameters");
        Check(QuestJarvisVoice.ValidEncounterId("enc-abcdef123456"), "authored encounter identity");
        Check(!QuestJarvisVoice.ValidEncounterId("coach-abcdef123456"), "coach identity cannot enter encounter route");
        Check(!QuestJarvisVoice.ValidEncounterId("enc-../../coach"), "reject route traversal");
        Check(!QuestJarvisVoice.ValidEncounterId("enc-ABCDEF"), "reject noncanonical encounter identity");
        Check(!QuestJarvisVoice.ValidEncounterId("enc-abc"), "reject truncated encounter identity");
        Check(!QuestJarvisVoice.ValidEncounterId("enc-" + new string('a', 41)), "bounded encounter identity");
        Console.WriteLine("voice-check passed: PCM negotiation/roundtrip/downmix, muted hold-to-talk silence, actual playback RMS, malformed PCM, nested tool parameters and encounter identity boundaries.");
        return 0;
    }
}
