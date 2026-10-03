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
        bool rejected = false;
        try { QuestJarvisVoice.DecodePcm(new byte[] { 1 }); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "partial PCM sample must be rejected");
        var extractor = typeof(QuestJarvisVoice).GetMethod("ExtractObject", BindingFlags.Static | BindingFlags.NonPublic);
        const string json = "{\"client_tool_call\":{\"parameters\":{\"note\":\"brace } and quote \\\"\",\"nested\":{\"x\":1}}}}";
        var parameters = (string)extractor.Invoke(null, new object[] { json, "parameters" });
        Check(parameters.StartsWith("{\"note\"") && parameters.EndsWith("{\"x\":1}}"), "preserve nested/escaped tool parameters");
        Console.WriteLine("voice-check passed: audio-format negotiation, signed PCM encoding/decoding/downmix, malformed PCM and nested tool parameters.");
        return 0;
    }
}
