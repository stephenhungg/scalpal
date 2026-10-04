using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Scalpal.Capture
{
    // Streams Motion-JPEG frames into a RIFF AVI 1.0 file. Only the per-frame index
    // (16 bytes/frame) stays in memory; pixels go straight to disk. OpenCV/FFmpeg
    // (services/motion uses cv2.VideoCapture) decode this container natively.
    // AVI has one constant frame rate: exact per-frame device timestamps live in the
    // capture timing artifact, never in the container.
    public sealed class MjpegAviWriter : IDisposable
    {
        public const long MaxRiffBytes = 1L << 30; // AVI 1.0 limit without OpenDML.
        readonly FileStream stream;
        readonly BinaryWriter writer;
        readonly List<uint> offsets = new List<uint>(), sizes = new List<uint>();
        readonly long riffSizeAt, usPerFrameAt, maxBytesPerSecAt, totalFramesAt, suggestedAt, scaleAt, rateAt, lengthAt, streamSuggestedAt, moviSizeAt, moviFourccAt;
        uint largestFrame;
        bool finished;
        public int Width { get; }
        public int Height { get; }
        public int FrameCount => sizes.Count;
        public long Length => stream.Length;

        public MjpegAviWriter(string path, int width, int height)
        {
            if (width <= 0 || height <= 0 || width > 4096 || height > 4096) throw new ArgumentException("Invalid capture frame size.");
            Width = width; Height = height;
            stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16);
            writer = new BinaryWriter(stream, Encoding.ASCII, true);
            Fourcc("RIFF"); riffSizeAt = stream.Position; writer.Write(0u); Fourcc("AVI ");
            Fourcc("LIST"); writer.Write(192u); Fourcc("hdrl");
            Fourcc("avih"); writer.Write(56u);
            usPerFrameAt = stream.Position; writer.Write(0u);
            maxBytesPerSecAt = stream.Position; writer.Write(0u);
            writer.Write(0u);          // padding granularity
            writer.Write(0x10u);       // AVIF_HASINDEX
            totalFramesAt = stream.Position; writer.Write(0u);
            writer.Write(0u);          // initial frames
            writer.Write(1u);          // streams
            suggestedAt = stream.Position; writer.Write(0u);
            writer.Write((uint)width); writer.Write((uint)height);
            for (int i = 0; i < 4; i++) writer.Write(0u);
            Fourcc("LIST"); writer.Write(116u); Fourcc("strl");
            Fourcc("strh"); writer.Write(56u);
            Fourcc("vids"); Fourcc("MJPG");
            writer.Write(0u); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write(0u);
            scaleAt = stream.Position; writer.Write(1000u);
            rateAt = stream.Position; writer.Write(0u);
            writer.Write(0u);          // start
            lengthAt = stream.Position; writer.Write(0u);
            streamSuggestedAt = stream.Position; writer.Write(0u);
            writer.Write(uint.MaxValue); // default quality
            writer.Write(0u);          // sample size: variable
            writer.Write((short)0); writer.Write((short)0); writer.Write((short)width); writer.Write((short)height);
            Fourcc("strf"); writer.Write(40u);
            writer.Write(40u); writer.Write(width); writer.Write(height);
            writer.Write((ushort)1); writer.Write((ushort)24);
            Fourcc("MJPG"); writer.Write((uint)(width * height * 3));
            writer.Write(0); writer.Write(0); writer.Write(0u); writer.Write(0u);
            Fourcc("LIST"); moviSizeAt = stream.Position; writer.Write(0u);
            moviFourccAt = stream.Position; Fourcc("movi");
        }

        void Fourcc(string code) { for (int i = 0; i < 4; i++) writer.Write((byte)code[i]); }

        public static bool IsJpeg(byte[] data, int length)
            => data != null && length >= 4 && length <= data.Length && data[0] == 0xFF && data[1] == 0xD8 && data[length - 2] == 0xFF && data[length - 1] == 0xD9;

        public void AppendFrame(byte[] jpeg, int length)
        {
            if (finished) throw new InvalidOperationException("The capture file is already finalized.");
            if (!IsJpeg(jpeg, length)) throw new ArgumentException("Capture frames must be complete JPEG images.");
            long after = stream.Position + 8 + length + (length & 1) + 16L * (sizes.Count + 1) + 8;
            if (after >= MaxRiffBytes) throw new IOException("Capture exceeds the AVI 1.0 size limit.");
            offsets.Add((uint)(stream.Position - moviFourccAt));
            Fourcc("00dc"); writer.Write((uint)length); writer.Write(jpeg, 0, length);
            if ((length & 1) == 1) writer.Write((byte)0);
            sizes.Add((uint)length);
            if ((uint)length > largestFrame) largestFrame = (uint)length;
        }

        // framesPerSecond is the measured average; it only seeds container playback timing.
        public void Finish(double framesPerSecond)
        {
            if (finished) return;
            if (double.IsNaN(framesPerSecond) || double.IsInfinity(framesPerSecond) || framesPerSecond <= 0) throw new ArgumentException("Invalid nominal frame rate.");
            long moviEnd = stream.Position;
            Fourcc("idx1"); writer.Write((uint)(16 * sizes.Count));
            for (int i = 0; i < sizes.Count; i++) { Fourcc("00dc"); writer.Write(0x10u); writer.Write(offsets[i]); writer.Write(sizes[i]); }
            long end = stream.Position;
            uint rate = (uint)Math.Max(1, Math.Round(framesPerSecond * 1000));
            Patch(riffSizeAt, (uint)(end - 8));
            Patch(moviSizeAt, (uint)(moviEnd - moviSizeAt - 4));
            Patch(usPerFrameAt, (uint)Math.Round(1e6 / framesPerSecond));
            Patch(maxBytesPerSecAt, (uint)Math.Min(uint.MaxValue, Math.Ceiling(largestFrame * framesPerSecond)));
            Patch(totalFramesAt, (uint)sizes.Count);
            Patch(suggestedAt, largestFrame + 8);
            Patch(scaleAt, 1000u); Patch(rateAt, rate);
            Patch(lengthAt, (uint)sizes.Count);
            Patch(streamSuggestedAt, largestFrame + 8);
            stream.Seek(end, SeekOrigin.Begin);
            writer.Flush(); stream.Flush(true);
            finished = true;
        }

        void Patch(long at, uint value) { stream.Seek(at, SeekOrigin.Begin); writer.Write(value); }

        public void Dispose() { writer.Dispose(); stream.Dispose(); }

        // Minimal reader for validation: returns per-frame JPEG lengths in index order.
        public static bool TryReadIndex(string path, out int width, out int height, out uint frames, out uint rate, out uint scale, out List<uint> frameSizes, out string error)
        {
            width = height = 0; frames = rate = scale = 0; frameSizes = new List<uint>(); error = "";
            try
            {
                using (var s = File.OpenRead(path))
                using (var r = new BinaryReader(s))
                {
                    if (Tag(r) != "RIFF") { error = "not RIFF"; return false; }
                    uint riff = r.ReadUInt32();
                    if (riff + 8 != s.Length) { error = "RIFF size mismatch"; return false; }
                    if (Tag(r) != "AVI ") { error = "not AVI"; return false; }
                    long moviData = -1;
                    while (s.Position + 8 <= s.Length)
                    {
                        string id = Tag(r); uint size = r.ReadUInt32(); long next = s.Position + size + (size & 1);
                        if (id == "LIST")
                        {
                            string type = Tag(r);
                            if (type == "hdrl")
                            {
                                if (Tag(r) != "avih" || r.ReadUInt32() != 56) { error = "missing avih"; return false; }
                                r.ReadUInt32(); r.ReadUInt32(); r.ReadUInt32(); r.ReadUInt32();
                                frames = r.ReadUInt32(); r.ReadUInt32(); r.ReadUInt32(); r.ReadUInt32();
                                width = (int)r.ReadUInt32(); height = (int)r.ReadUInt32();
                                for (int i = 0; i < 4; i++) r.ReadUInt32();
                                if (Tag(r) != "LIST") { error = "missing strl"; return false; }
                                r.ReadUInt32();
                                if (Tag(r) != "strl" || Tag(r) != "strh" || r.ReadUInt32() != 56 || Tag(r) != "vids" || Tag(r) != "MJPG") { error = "stream is not MJPG video"; return false; }
                                r.ReadUInt32(); r.ReadUInt32(); r.ReadUInt32();
                                scale = r.ReadUInt32(); rate = r.ReadUInt32();
                            }
                            else if (type == "movi") moviData = s.Position - 4;
                        }
                        else if (id == "idx1")
                        {
                            if (moviData < 0) { error = "index before movi"; return false; }
                            for (uint i = 0; i < size / 16; i++)
                            {
                                if (Tag(r) != "00dc") { error = "unexpected index chunk"; return false; }
                                r.ReadUInt32(); uint offset = r.ReadUInt32(), length = r.ReadUInt32();
                                long back = s.Position;
                                s.Seek(moviData + offset, SeekOrigin.Begin);
                                if (Tag(r) != "00dc" || r.ReadUInt32() != length) { error = "index offset mismatch"; return false; }
                                byte[] data = r.ReadBytes((int)length);
                                if (!IsJpeg(data, data.Length)) { error = "frame is not a complete JPEG"; return false; }
                                frameSizes.Add(length);
                                s.Seek(back, SeekOrigin.Begin);
                            }
                        }
                        s.Seek(next, SeekOrigin.Begin);
                    }
                }
                if (frameSizes.Count != frames) { error = "frame count mismatch"; return false; }
                return true;
            }
            catch (Exception e) { error = e.Message; return false; }
        }
        static string Tag(BinaryReader r) => Encoding.ASCII.GetString(r.ReadBytes(4));
    }
}
