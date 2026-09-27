using System;
using System.Collections.Generic;

namespace NightSignal.AudioTool
{
    /// <summary>Objective measurements on rendered audio. None of these judge musical quality.</summary>
    public static class Analysis
    {
        public static double Db(double linear) => linear <= 1e-12 ? -240.0 : 20.0 * Math.Log10(linear);

        public static double Peak(float[] x, int start, int end)
        {
            double p = 0;
            for (int i = start; i < end; i++) { double a = Math.Abs(x[i]); if (a > p) p = a; }
            return p;
        }

        public static double Rms(float[] x, int start, int end)
        {
            double s = 0;
            for (int i = start; i < end; i++) s += (double)x[i] * x[i];
            return Math.Sqrt(s / Math.Max(1, end - start));
        }

        sealed class Biquad
        {
            readonly double b0, b1, b2, a1, a2;
            double z1, z2;
            public Biquad(double b0, double b1, double b2, double a1, double a2) { this.b0 = b0; this.b1 = b1; this.b2 = b2; this.a1 = a1; this.a2 = a2; }
            public double Process(double x)
            {
                double y = b0 * x + z1;
                z1 = b1 * x - a1 * y + z2;
                z2 = b2 * x - a2 * y;
                return y;
            }
        }

        /// <summary>
        /// Integrated loudness per ITU-R BS.1770-4 (K-weighting coefficients for 48 kHz, 400 ms blocks with
        /// 75 % overlap, −70 LUFS absolute and −10 LU relative gates). Stereo interleaved input at 48 kHz.
        /// </summary>
        public static double IntegratedLoudness(float[] interleaved, int frames, int channels, int sampleRate)
        {
            if (sampleRate != 48000) throw new ArgumentException("loudness coefficients are for 48 kHz");
            var ms = new double[channels][];
            for (int c = 0; c < channels; c++)
            {
                var shelf = new Biquad(1.53512485958697, -2.69169618940638, 1.19839281085285, -1.69065929318241, 0.73248077421585);
                var hp = new Biquad(1.0, -2.0, 1.0, -1.99004745483398, 0.99007225036621);
                var sq = new double[frames];
                for (int i = 0; i < frames; i++)
                {
                    double y = hp.Process(shelf.Process(interleaved[i * channels + c]));
                    sq[i] = y * y;
                }
                ms[c] = sq;
            }
            int block = (int)(0.4 * sampleRate), hop = block / 4;
            var blocks = new List<double>();
            var prefix = new double[channels][];
            for (int c = 0; c < channels; c++)
            {
                prefix[c] = new double[frames + 1];
                for (int i = 0; i < frames; i++) prefix[c][i + 1] = prefix[c][i] + ms[c][i];
            }
            for (int s = 0; s + block <= frames; s += hop)
            {
                double z = 0;
                for (int c = 0; c < channels; c++) z += (prefix[c][s + block] - prefix[c][s]) / block;
                blocks.Add(z);
            }
            if (blocks.Count == 0) return -70;
            double absGate = Math.Pow(10, (-70 + 0.691) / 10);
            double sum = 0; int n = 0;
            foreach (var z in blocks) if (z > absGate) { sum += z; n++; }
            if (n == 0) return -70;
            double relGate = sum / n * Math.Pow(10, -1.0);
            sum = 0; n = 0;
            foreach (var z in blocks) if (z > absGate && z > relGate) { sum += z; n++; }
            if (n == 0) return -70;
            return -0.691 + 10 * Math.Log10(sum / n);
        }

        /// <summary>
        /// Loop-seam discontinuity: the largest sample-to-sample step within ±2 samples of the seam, compared with
        /// the largest step in the surrounding ±50 ms (seam excluded). Ratio ≤ 1 means the seam is not an outlier.
        /// </summary>
        public static void SeamJump(float[] x, int channels, int seamFrame, int sampleRate, out double seamStep, out double localStep)
        {
            seamStep = 0;
            localStep = 0;
            int w = sampleRate / 20;
            for (int f = Math.Max(1, seamFrame - w); f < seamFrame + w && (f + 1) * channels <= x.Length; f++)
            {
                for (int c = 0; c < channels; c++)
                {
                    double d = Math.Abs(x[f * channels + c] - x[(f - 1) * channels + c]);
                    if (Math.Abs(f - seamFrame) <= 2) { if (d > seamStep) seamStep = d; }
                    else if (d > localStep) localStep = d;
                }
            }
        }

        /// <summary>Largest run (seconds) of 100 ms windows below −50 dBFS RMS within [start, end) frames.</summary>
        public static double LongestSilence(float[] x, int channels, int startFrame, int endFrame, int sampleRate)
        {
            int win = sampleRate / 10;
            double best = 0, run = 0;
            for (int f = startFrame; f + win <= endFrame; f += win)
            {
                double r = Rms(x, f * channels, (f + win) * channels);
                if (Db(r) < -50) { run += 0.1; if (run > best) best = run; }
                else run = 0;
            }
            return best;
        }

        public static void Fft(double[] re, double[] im)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = -2 * Math.PI / len;
                double wr = Math.Cos(ang), wi = Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    double cr = 1, ci = 0;
                    for (int k = 0; k < len / 2; k++)
                    {
                        int a = i + k, b = i + k + len / 2;
                        double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                        re[b] = re[a] - tr; im[b] = im[a] - ti;
                        re[a] += tr; im[a] += ti;
                        double ncr = cr * wr - ci * wi;
                        ci = cr * wi + ci * wr;
                        cr = ncr;
                    }
                }
            }
        }

        /// <summary>Hann-windowed magnitude spectrum averaged over consecutive frames of size n (power of two).</summary>
        public static double[] AverageSpectrum(float[] mono, int start, int count, int n)
        {
            var acc = new double[n / 2];
            int frames = 0;
            var re = new double[n];
            var im = new double[n];
            for (int s = start; s + n <= start + count; s += n / 2)
            {
                for (int i = 0; i < n; i++)
                {
                    double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1));
                    re[i] = mono[s + i] * w;
                    im[i] = 0;
                }
                Fft(re, im);
                for (int k = 0; k < n / 2; k++) acc[k] += Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
                frames++;
            }
            if (frames > 0) for (int k = 0; k < acc.Length; k++) acc[k] /= frames;
            return acc;
        }

        /// <summary>Third-octave band levels (dB) from 40 Hz to 10 kHz for a magnitude spectrum.</summary>
        public static double[] ThirdOctaveBands(double[] mag, int n, int sampleRate)
        {
            var centres = new List<double>();
            for (double f = 40; f <= 10000; f *= Math.Pow(2, 1.0 / 3)) centres.Add(f);
            var bands = new double[centres.Count];
            double binHz = (double)sampleRate / n;
            for (int b = 0; b < centres.Count; b++)
            {
                double lo = centres[b] / Math.Pow(2, 1.0 / 6), hi = centres[b] * Math.Pow(2, 1.0 / 6);
                double e = 0;
                for (int k = (int)(lo / binHz); k <= (int)(hi / binHz) && k < mag.Length; k++) e += mag[k] * mag[k];
                bands[b] = 10 * Math.Log10(e + 1e-12);
            }
            return bands;
        }

        /// <summary>Mean absolute difference (dB) between two band profiles after removing their mean level.</summary>
        public static double ProfileDistance(double[] a, double[] b)
        {
            double ma = 0, mb = 0;
            for (int i = 0; i < a.Length; i++) { ma += a[i]; mb += b[i]; }
            ma /= a.Length; mb /= b.Length;
            double d = 0;
            for (int i = 0; i < a.Length; i++) d += Math.Abs((a[i] - ma) - (b[i] - mb));
            return d / a.Length;
        }

        public static double Correlation(double[] a, double[] b)
        {
            double ma = 0, mb = 0;
            for (int i = 0; i < a.Length; i++) { ma += a[i]; mb += b[i]; }
            ma /= a.Length; mb /= b.Length;
            double sab = 0, saa = 0, sbb = 0;
            for (int i = 0; i < a.Length; i++)
            {
                double x = a[i] - ma, y = b[i] - mb;
                sab += x * y; saa += x * x; sbb += y * y;
            }
            return sab / Math.Sqrt(saa * sbb + 1e-30);
        }

        public static float[] MonoOf(float[] interleaved, int channels, int startFrame, int frames)
        {
            var m = new float[frames];
            for (int f = 0; f < frames; f++)
            {
                double s = 0;
                for (int c = 0; c < channels; c++) s += interleaved[(startFrame + f) * channels + c];
                m[f] = (float)(s / channels);
            }
            return m;
        }

        public static ulong Hash(float[] x, int count)
        {
            ulong h = 1469598103934665603UL;
            for (int i = 0; i < count; i++)
            {
                uint bits = BitConverter.SingleToUInt32Bits(x[i]);
                h ^= bits;
                h *= 1099511628211UL;
            }
            return h;
        }
    }
}
