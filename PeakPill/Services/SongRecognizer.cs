using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NAudio.Wave;

namespace PeakPill.Services;

/// <summary>A song Shazam knows.</summary>
public sealed record SongMatch(string Title, string Artist, string CoverUrl, string ShazamUrl);

/// <summary>
/// "Welcher Song ist das?" (user wish 2026-11-26, the user chose the unofficial Shazam way): a few seconds of what the PC
/// plays – every app together, through the speakers' loopback, no microphone – become a Shazam fingerprint (the algorithm
/// of the open-source SongRec), and only that fingerprint is sent to Shazam. Unofficial: it may stop working whenever
/// Shazam changes something.
/// </summary>
public static class SongRecognizer
{
    private const int Rate = 16000;

    // =====================================================================
    //  1 · what the PC plays (all apps together)
    // =====================================================================

    /// <summary>
    /// Listens to the sound output until disposed – every app together (loopback of the default speakers). Windows only
    /// delivers data while something plays, so silence stays empty.
    /// </summary>
    public sealed class Listener : IDisposable
    {
        private readonly WasapiLoopbackCapture _cap;
        private readonly List<float> _mono = new();
        private readonly int _from;
        private double _level;

        public Listener()
        {
            _cap = new WasapiLoopbackCapture();
            var fmt = _cap.WaveFormat;
            _from = fmt.SampleRate;
            int ch = fmt.Channels, bytes = fmt.BitsPerSample / 8, frame = bytes * ch;
            bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat || fmt.Encoding == WaveFormatEncoding.Extensible && fmt.BitsPerSample == 32;
            _cap.DataAvailable += (_, e) =>
            {
                double sq = 0;
                int n = 0;
                lock (_mono)
                    for (int i = 0; i + frame <= e.BytesRecorded; i += frame)
                    {
                        float sum = 0;
                        for (int c = 0; c < ch; c++)
                        {
                            int o = i + c * bytes;
                            sum += isFloat ? BitConverter.ToSingle(e.Buffer, o) : bytes == 2 ? BitConverter.ToInt16(e.Buffer, o) / 32768f : BitConverter.ToInt32(e.Buffer, o) / 2147483648f;
                        }
                        float v = sum / ch;
                        _mono.Add(v);
                        sq += v * v;
                        n++;
                    }
                if (n > 0) _level = Math.Sqrt(sq / n);
            };
            _cap.StartRecording();
        }

        /// <summary>How loud it is right now (0…1, RMS of the last packet).</summary>
        public double Level => _level;

        /// <summary>The last <paramref name="max"/> of what played, as 16 kHz mono 16-bit.</summary>
        public short[] Take(TimeSpan max)
        {
            float[] src;
            lock (_mono)
            {
                int n = Math.Min(_mono.Count, (int)(_from * max.TotalSeconds));
                src = _mono.GetRange(_mono.Count - n, n).ToArray();
            }
            return Resample(src, _from);
        }

        public void Dispose()
        {
            try { _cap.StopRecording(); } catch { }
            _cap.Dispose();
        }
    }

    /// <summary>
    /// To 16 kHz through a windowed-sinc low-pass (7.2 kHz): a plain average lets the treble fold back into the bands
    /// the fingerprint uses (250–5500 Hz) and spoils its peaks.
    /// </summary>
    private static short[] Resample(float[] src, int from)
    {
        double ratio = (double)from / Rate;
        int n = (int)(src.Length / ratio);
        var dst = new short[n];
        double fc = Math.Min(7200.0, Rate * 0.45) / from; // cycles per input sample
        const int half = 32;
        var taps = new double[2 * half];
        for (int i = 0; i < n; i++)
        {
            double x = i * ratio;
            int c = (int)x;
            double sum = 0, wsum = 0;
            for (int k = c - half + 1, j = 0; k <= c + half; k++, j++)
            {
                double d = x - k;
                double s = Math.Abs(d) < 1e-9 ? 2 * fc : Math.Sin(2 * Math.PI * fc * d) / (Math.PI * d);
                double w = 0.42 + 0.5 * Math.Cos(Math.PI * d / half) + 0.08 * Math.Cos(2 * Math.PI * d / half); // Blackman
                taps[j] = s * w;
                wsum += taps[j];
                if (k >= 0 && k < src.Length) sum += src[k] * taps[j];
            }
            double v = wsum != 0 ? sum / wsum : 0;
            dst[i] = (short)Math.Clamp(v * 32767, -32768, 32767);
        }
        return dst;
    }

    /// <summary>Loud enough to be music (not silence or a click).</summary>
    public static bool HasSound(short[] s) => s.Length >= Rate * 2 && s.Count(x => Math.Abs((int)x) > 300) > s.Length / 10;

    // =====================================================================
    //  2 · the fingerprint (port of SongRec's signature generator)
    // =====================================================================

    private sealed record Peak(int Pass, int Magnitude, int Bin);

    private static readonly double[] Hann = Enumerable.Range(1, 2048).Select(n => 0.5 - 0.5 * Math.Cos(2 * Math.PI * n / 2049)).ToArray();

    private sealed class Generator
    {
        private readonly short[] _ring = new short[2048];
        private int _ringPos;
        private readonly double[][] _ffts = Enumerable.Range(0, 256).Select(_ => new double[1025]).ToArray();
        private int _fftPos;
        private readonly double[][] _spread = Enumerable.Range(0, 256).Select(_ => new double[1025]).ToArray();
        private int _spreadPos, _spreadWritten;
        private readonly double[] _re = new double[2048], _im = new double[2048];
        public readonly SortedDictionary<int, List<Peak>> Bands = new();
        public int Samples;

        private static int Mod(int x) => ((x % 256) + 256) % 256;

        public void Feed(short[] s)
        {
            for (int off = 0; off + 128 <= s.Length; off += 128)
            {
                Samples += 128;
                DoFft(s, off);
                DoSpreading();
                if (_spreadWritten >= 46) DoPeaks();
            }
        }

        private void DoFft(short[] s, int off)
        {
            Array.Copy(s, off, _ring, _ringPos, 128);
            _ringPos = (_ringPos + 128) % 2048;
            for (int i = 0; i < 2048; i++) { _re[i] = _ring[(_ringPos + i) % 2048] * Hann[i]; _im[i] = 0; }
            Fft(_re, _im);
            var o = _ffts[_fftPos];
            for (int k = 0; k <= 1024; k++) o[k] = Math.Max((_re[k] * _re[k] + _im[k] * _im[k]) / (1 << 17), 1e-10);
            _fftPos = (_fftPos + 1) % 256;
        }

        private void DoSpreading()
        {
            var origin = _ffts[Mod(_fftPos - 1)];
            var sp = _spread[_spreadPos];
            Array.Copy(origin, sp, 1025);
            for (int p = 0; p < 1025; p++)
            {
                if (p < 1023) sp[p] = Math.Max(sp[p], Math.Max(sp[p + 1], sp[p + 2]));
                double max = sp[p];
                foreach (int f in new[] { -1, -3, -6 })
                {
                    var former = _spread[Mod(_spreadPos + f)];
                    former[p] = max = Math.Max(former[p], max);
                }
            }
            _spreadPos = (_spreadPos + 1) % 256;
            _spreadWritten++;
        }

        private static readonly int[] Near = { -10, -7, -4, -3, 1, 2, 5, 8 };
        private static readonly int[] Other = { -53, -45, 165, 172, 179, 186, 193, 200, 214, 221, 228, 235, 242, 249 };

        private void DoPeaks()
        {
            var f46 = _ffts[Mod(_fftPos - 46)];
            var f49 = _spread[Mod(_spreadPos - 49)];
            for (int bin = 10; bin < 1015; bin++)
            {
                if (!(f46[bin] >= 1.0 / 64 && f46[bin] >= f49[bin - 1])) continue;
                double maxNear = 0;
                foreach (int o in Near) maxNear = Math.Max(f49[bin + o], maxNear);
                if (!(f46[bin] > maxNear)) continue;
                double maxOther = maxNear;
                foreach (int o in Other) maxOther = Math.Max(_spread[Mod(_spreadPos + o)][bin - 1], maxOther);
                if (!(f46[bin] > maxOther)) continue;
                double Mag(double v) => Math.Log(Math.Max(1.0 / 64, v)) * 1477.3 + 6144;
                double mag = Mag(f46[bin]), before = Mag(f46[bin - 1]), after = Mag(f46[bin + 1]);
                double v1 = mag * 2 - before - after;
                if (v1 <= 0) continue;
                double v2 = (after - before) * 32 / v1;
                double corrected = bin * 64 + v2;
                double hz = corrected * (16000.0 / 2 / 1024 / 64);
                int band = hz < 250 ? -1 : hz < 520 ? 0 : hz < 1450 ? 1 : hz < 3500 ? 2 : hz <= 5500 ? 3 : -1;
                if (band < 0) continue;
                if (!Bands.TryGetValue(band, out var list)) Bands[band] = list = new();
                list.Add(new Peak(_spreadWritten - 46, (int)mag, (int)corrected));
            }
        }

        /// <summary>In-place radix-2 FFT (2048 points).</summary>
        private static void Fft(double[] re, double[] im)
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
                double ang = -2 * Math.PI / len, wr = Math.Cos(ang), wi = Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    double cr = 1, ci = 0;
                    for (int k = 0; k < len / 2; k++)
                    {
                        int a = i + k, b = a + len / 2;
                        double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                        re[b] = re[a] - tr; im[b] = im[a] - ti;
                        re[a] += tr; im[a] += ti;
                        double nr = cr * wr - ci * wi;
                        ci = cr * wi + ci * wr;
                        cr = nr;
                    }
                }
            }
        }

        /// <summary>Shazam's binary signature format, as a data URI.</summary>
        public string Encode()
        {
            var contents = new MemoryStream();
            foreach (var (band, peaks) in Bands)
            {
                var pb = new MemoryStream();
                int pass = 0;
                foreach (var p in peaks)
                {
                    if (p.Pass - pass >= 255) { pb.WriteByte(0xff); pb.Write(BitConverter.GetBytes(p.Pass)); pass = p.Pass; }
                    pb.WriteByte((byte)(p.Pass - pass));
                    pb.Write(BitConverter.GetBytes((ushort)p.Magnitude));
                    pb.Write(BitConverter.GetBytes((ushort)p.Bin));
                    pass = p.Pass;
                }
                contents.Write(BitConverter.GetBytes(0x60030040 + band));
                contents.Write(BitConverter.GetBytes((int)pb.Length));
                contents.Write(pb.ToArray());
                for (int pad = (int)((4 - pb.Length % 4) % 4); pad > 0; pad--) contents.WriteByte(0);
            }
            var body = contents.ToArray();
            var msg = new MemoryStream();
            void U32(uint v) => msg.Write(BitConverter.GetBytes(v));
            U32(0xcafe2580); U32(0);                                // magic, CRC (filled in below)
            U32((uint)body.Length + 8);                             // size minus the 48-byte header
            U32(0x94119c00); U32(0); U32(0); U32(0);                // magic, void
            U32(3u << 27);                                          // 16 kHz
            U32(0); U32(0);                                         // void
            U32((uint)(Samples + Rate * 0.24));
            U32((15u << 19) + 0x40000);
            U32(0x40000000);
            U32((uint)body.Length + 8);
            msg.Write(body);
            var all = msg.ToArray();
            BitConverter.GetBytes(Crc32(all, 8, all.Length - 8)).CopyTo(all, 4);
            return "data:audio/vnd.shazam.sig;base64," + Convert.ToBase64String(all);
        }

        private static uint Crc32(byte[] d, int off, int len)
        {
            uint crc = 0xffffffff;
            for (int i = off; i < off + len; i++)
            {
                crc ^= d[i];
                for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
            return ~crc;
        }
    }

    // =====================================================================
    //  3 · ask Shazam
    // =====================================================================

    #if DEBUG
    public static string LastAnswer;
    #endif
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>The song, or null when Shazam does not know it.</summary>
    public static async Task<SongMatch> RecognizeAsync(short[] samples, string language, CancellationToken ct)
    {
        var gen = new Generator();
        gen.Feed(samples);
        if (gen.Bands.Count == 0) return null;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var body = new JsonObject
        {
            // no real place: SongRec sends a random one too
            ["geolocation"] = new JsonObject { ["altitude"] = 300, ["latitude"] = 45, ["longitude"] = 2 },
            ["signature"] = new JsonObject { ["samplems"] = (long)(gen.Samples * 1000.0 / Rate), ["timestamp"] = now, ["uri"] = gen.Encode() },
            ["timestamp"] = now,
            ["timezone"] = "Europe/Berlin",
        };
        string lang = language == "de" ? "de/DE" : "en/US";
        var url = $"https://amp.shazam.com/discovery/v5/{lang}/android/-/tag/{Guid.NewGuid().ToString().ToUpperInvariant()}/{Guid.NewGuid()}" +
                  "?sync=true&webv3=true&sampling=true&connected=&shazamapiversion=v3&sharehub=true&video=v3";
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.TryAddWithoutValidation("User-Agent", "Dalvik/2.1.0 (Linux; U; Android 10)");
        req.Headers.TryAddWithoutValidation("Content-Language", language == "de" ? "de_DE" : "en_US");
        using var res = await Http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
#if DEBUG
        LastAnswer = text;
#endif
        using var doc = JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty("track", out var t)) return null;
        string S(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        string cover = t.TryGetProperty("images", out var img) ? S(img, "coverarthq") ?? S(img, "coverart") : null;
        return new SongMatch(S(t, "title"), S(t, "subtitle"), cover, S(t, "url"));
    }
}
