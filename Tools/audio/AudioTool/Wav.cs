using System;
using System.IO;

namespace NightSignal.AudioTool
{
    /// <summary>16-bit PCM WAV writer for listening previews (renders go to Tools/audio/out only).</summary>
    public static class Wav
    {
        public static void Write(string path, float[] interleaved, int frames, int channels, int sampleRate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var w = new BinaryWriter(fs))
            {
                int dataBytes = frames * channels * 2;
                w.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
                w.Write(36 + dataBytes);
                w.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E' });
                w.Write(new[] { (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
                w.Write(16);
                w.Write((short)1);
                w.Write((short)channels);
                w.Write(sampleRate);
                w.Write(sampleRate * channels * 2);
                w.Write((short)(channels * 2));
                w.Write((short)16);
                w.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
                w.Write(dataBytes);
                var buf = new byte[dataBytes];
                int n = frames * channels;
                for (int i = 0; i < n; i++)
                {
                    float v = interleaved[i];
                    if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
                    short s = (short)Math.Round(v * 32767f);
                    buf[i * 2] = (byte)(s & 0xFF);
                    buf[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
                }
                w.Write(buf);
            }
        }
    }
}
