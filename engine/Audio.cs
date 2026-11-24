// Wego engine – call audio
// Own microphone capture and speaker playback (instead of the library defaults) so Wego can:
//  * keep the customer's voice steady and loud enough (auto volume + limiter + jitter buffer)
//  * play hold music and a spoken announcement to the customer while on hold
//  * record both sides of the call

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using System.Text;
using NAudio.Wave;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace Wego.Engine;

// ------------------------------------------------------------------ settings shared by all calls
internal static class AudioSettings
{
    public static volatile bool Agc = true;          // automatic volume for both directions
    public static volatile float MicGain = 1.0f;     // extra boost for the agent's voice
    public static volatile float SpkGain = 1.0f;     // extra boost for the customer's voice
    public static volatile bool NoiseGate = true;    // keep background noise down between words
}

// ------------------------------------------------------------------ automatic gain control
// Smoothly raises quiet audio and lowers loud audio towards one level, without pumping.
internal sealed class Agc
{
    float _gain = 1f;
    readonly float _target, _maxGain;
    public Agc(float targetRms = 4200f, float maxGain = 8f) { _target = targetRms; _maxGain = maxGain; }

    public void Process(short[] pcm, float userGain, bool auto, bool gate)
    {
        if (pcm.Length == 0) return;
        double sum = 0; for (int i = 0; i < pcm.Length; i++) sum += (double)pcm[i] * pcm[i];
        float rms = (float)Math.Sqrt(sum / pcm.Length);

        if (auto)
        {
            if (rms > 180f) // speech present: move gain slowly towards the target
            {
                float want = Math.Clamp(_target / rms, 0.5f, _maxGain);
                float rate = want < _gain ? 0.25f : 0.04f; // fast attack, slow release = no fluctuation
                _gain += (want - _gain) * rate;
            }
        }
        else _gain += (1f - _gain) * 0.2f;

        float g = _gain * userGain;
        float gateMul = gate && rms < 90f ? 0.35f : 1f; // soften hiss between words
        for (int i = 0; i < pcm.Length; i++)
        {
            float x = pcm[i] * g * gateMul;
            // soft limiter: no harsh clipping when someone shouts
            const float knee = 24000f;
            if (x > knee) x = knee + (32767f - knee) * MathF.Tanh((x - knee) / (32767f - knee));
            else if (x < -knee) x = -knee - (32767f - knee) * MathF.Tanh((-x - knee) / (32767f - knee));
            pcm[i] = (short)Math.Clamp(x, -32767f, 32767f);
        }
    }
}

// ------------------------------------------------------------------ simple resampling helpers
internal static class Pcm
{
    public static short[] Resample(short[] src, int from, int to)
    {
        if (from == to || src.Length == 0) return src;
        int n = (int)((long)src.Length * to / from);
        var dst = new short[n];
        double step = (double)from / to;
        for (int i = 0; i < n; i++)
        {
            double pos = i * step; int a = (int)pos; double f = pos - a;
            int b = Math.Min(a + 1, src.Length - 1);
            dst[i] = (short)(src[a] * (1 - f) + src[b] * f);
        }
        return dst;
    }

    // 16 kHz -> 8 kHz with a small low-pass so it does not sound harsh
    public static short[] Down2(short[] src)
    {
        var dst = new short[src.Length / 2];
        for (int i = 0; i < dst.Length; i++)
        {
            int j = i * 2;
            int a = src[Math.Max(j - 1, 0)], b = src[j], c = src[Math.Min(j + 1, src.Length - 1)];
            dst[i] = (short)((a + 2 * b + c) / 4);
        }
        return dst;
    }

    public static byte[] ToBytes(short[] s) { var b = new byte[s.Length * 2]; Buffer.BlockCopy(s, 0, b, 0, b.Length); return b; }
    public static short[] FromBytes(byte[] b, int count) { var s = new short[count / 2]; Buffer.BlockCopy(b, 0, s, 0, s.Length * 2); return s; }
}

// ------------------------------------------------------------------ shared microphone (16 kHz mono)
internal static class Mic
{
    static WaveInEvent _wave;
    static readonly List<Media> Subs = new();
    static readonly object L = new();
    public const int Rate = 16000, FrameMs = 20;

    public static void Subscribe(Media m)
    {
        lock (L)
        {
            if (!Subs.Contains(m)) Subs.Add(m);
            if (_wave != null) return;
            try
            {
                _wave = new WaveInEvent { DeviceNumber = Program.InDevice, WaveFormat = new WaveFormat(Rate, 16, 1), BufferMilliseconds = FrameMs, NumberOfBuffers = 4 };
                _wave.DataAvailable += (s, e) =>
                {
                    var pcm = Pcm.FromBytes(e.Buffer, e.BytesRecorded);
                    Media[] list; lock (L) list = Subs.ToArray();
                    foreach (var media in list) { try { media.OnMic(pcm); } catch { } }
                };
                _wave.RecordingStopped += (s, e) => { if (e.Exception != null) Program.Emit(new { ev = "error", msg = "Microphone stopped: " + e.Exception.Message }); };
                _wave.StartRecording();
            }
            catch (Exception ex)
            {
                _wave = null;
                Program.Emit(new { ev = "error", msg = "Could not open the microphone: " + ex.Message });
            }
        }
    }

    public static void Unsubscribe(Media m)
    {
        lock (L)
        {
            Subs.Remove(m);
            if (Subs.Count == 0 && _wave != null)
            {
                try { _wave.StopRecording(); _wave.Dispose(); } catch { }
                _wave = null;
            }
        }
    }

    public static void Restart() { lock (L) { if (_wave == null) return; try { _wave.StopRecording(); _wave.Dispose(); } catch { } _wave = null; } Media[] l; lock (L) l = Subs.ToArray(); foreach (var m in l) Subscribe(m); }
}

// ------------------------------------------------------------------ hold music + announcement
internal static class HoldAudio
{
    public const int Rate = 16000;
    static short[] _music = Array.Empty<short>();
    static short[] _announce = Array.Empty<short>();
    public static volatile bool Announce = true;
    public static volatile int AnnounceAfterSec = 30;
    public static string MusicName = "Wego Jazz (default)";
    public static string Text = "Please stay on the line, as we value your call.";

    public static short[] Music => _music;
    public static short[] Announcement => _announce;

    static string DefaultMusicPath => Path.Combine(AppContext.BaseDirectory, "assets", "hold-jazz.wav");

    public static void Init()
    {
        LoadMusic(null);
        _ = Task.Run(() => MakeAnnouncement(Text));
    }

    public static bool LoadMusic(string path)
    {
        bool custom = !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        string file = custom ? path : DefaultMusicPath;
        try
        {
            using var reader = new AudioFileReader(file);
            var fmt = reader.WaveFormat;
            var buf = new float[fmt.SampleRate * fmt.Channels];
            var mono = new List<short>(fmt.SampleRate * 60);
            int read;
            long maxSamples = (long)fmt.SampleRate * 600; // first 10 minutes is plenty
            while ((read = reader.Read(buf, 0, buf.Length)) > 0 && mono.Count < maxSamples)
            {
                for (int i = 0; i + fmt.Channels - 1 < read; i += fmt.Channels)
                {
                    float v = 0; for (int c = 0; c < fmt.Channels; c++) v += buf[i + c];
                    v /= fmt.Channels;
                    mono.Add((short)Math.Clamp(v * 32767f, -32767f, 32767f));
                }
            }
            var pcm = Pcm.Resample(mono.ToArray(), fmt.SampleRate, Rate);
            Normalize(pcm, 0.55f);
            _music = pcm;
            MusicName = custom ? Path.GetFileName(path) : "Wego Jazz (default)";
            Program.Emit(new { ev = "hold", music = MusicName, seconds = pcm.Length / Rate, announce = _announce.Length > 0 });
            return true;
        }
        catch (Exception ex)
        {
            Program.Emit(new { ev = "error", msg = "Could not load hold music: " + ex.Message });
            if (custom) return LoadMusic(null);
            return false;
        }
    }

    public static void MakeAnnouncement(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) text = "Please stay on the line, as we value your call.";
        Text = text;
        try
        {
            using var synth = new SpeechSynthesizer();
            // pick an English female voice: Zira (US), Hazel (UK), Heera (India)…
            var voices = synth.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo).ToList();
            VoiceInfo pick = null;
            foreach (var pref in new[] { "en-US", "en-GB", "en-IN", "en-AU", "en-CA", "en" })
            {
                pick = voices.FirstOrDefault(v => v.Gender == VoiceGender.Female && SafeCulture(v).StartsWith(pref, StringComparison.OrdinalIgnoreCase));
                if (pick != null) break;
            }
            pick ??= voices.FirstOrDefault(v => v.Gender == VoiceGender.Female);
            if (pick != null) synth.SelectVoice(pick.Name);
            synth.Rate = -1;
            synth.Volume = 100;
            using var ms = new MemoryStream();
            synth.SetOutputToAudioStream(ms, new SpeechAudioFormatInfo(Rate, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
            synth.Speak(text);
            synth.SetOutputToNull();
            var pcm = Pcm.FromBytes(ms.ToArray(), (int)ms.Length);
            Normalize(pcm, 0.75f);
            _announce = pcm;
            Program.Emit(new { ev = "hold", music = MusicName, seconds = _music.Length / Rate, announce = true, voice = pick?.Name ?? "default" });
        }
        catch (Exception ex)
        {
            Program.Emit(new { ev = "error", msg = "Could not create the hold announcement: " + ex.Message });
        }
    }

    static string SafeCulture(VoiceInfo v) { try { return v.Culture?.Name ?? ""; } catch { return ""; } }

    static void Normalize(short[] pcm, float peak)
    {
        int max = 1; foreach (var s in pcm) max = Math.Max(max, Math.Abs((int)s));
        float g = peak * 32767f / max;
        for (int i = 0; i < pcm.Length; i++) pcm[i] = (short)Math.Clamp(pcm[i] * g, -32767f, 32767f);
    }

    // A player that walks through: music (N seconds) -> announcement -> short pause -> music … in a loop.
    internal sealed class Player
    {
        long _musicPos;
        int _phasePos;
        int _phase; // 0 music, 1 announcement, 2 pause
        int _musicPlayedInPhase;

        public short[] Next(int samples)
        {
            var outp = new short[samples];
            var music = Music; var ann = Announcement;
            int afterSamples = Math.Max(5, AnnounceAfterSec) * Rate;
            for (int i = 0; i < samples; i++)
            {
                if (_phase == 0)
                {
                    outp[i] = music.Length > 0 ? music[_musicPos++ % music.Length] : (short)0;
                    if (Announce && ann.Length > 0 && ++_musicPlayedInPhase >= afterSamples) { _phase = 2; _phasePos = 0; }
                }
                else if (_phase == 2)
                {
                    // 0.6 s fade-out / pause before (and after) the announcement
                    outp[i] = 0;
                    if (++_phasePos >= Rate * 6 / 10) { _phase = _musicPlayedInPhase > 0 ? 1 : 0; _phasePos = 0; }
                }
                else
                {
                    outp[i] = _phasePos < ann.Length ? ann[_phasePos] : (short)0;
                    if (++_phasePos >= ann.Length) { _phase = 2; _phasePos = 0; _musicPlayedInPhase = 0; }
                }
            }
            return outp;
        }
    }

    // ---- local preview on the agent's speaker
    static WaveOutEvent _preview;
    public static void Preview(int seconds)
    {
        StopPreview();
        try
        {
            var p = new Player();
            int total = Math.Clamp(seconds, 5, 120) * Rate;
            var pcm = p.Next(total);
            var prov = new BufferedWaveProvider(new WaveFormat(Rate, 16, 1)) { BufferLength = total * 2 + Rate, DiscardOnBufferOverflow = true };
            var bytes = Pcm.ToBytes(pcm);
            prov.AddSamples(bytes, 0, bytes.Length);
            _preview = new WaveOutEvent { DeviceNumber = Program.OutDevice, DesiredLatency = 200 };
            _preview.Init(prov);
            _preview.Play();
        }
        catch (Exception ex) { Program.Emit(new { ev = "error", msg = "Preview failed: " + ex.Message }); }
    }
    public static void StopPreview() { try { _preview?.Stop(); _preview?.Dispose(); } catch { } _preview = null; }
}

// ------------------------------------------------------------------ one call's media
internal sealed class Media
{
    public VoIPMediaSession Session;
    AudioExtrasSource _source;
    readonly AudioEncoder _enc = new(), _decRemote = new();
    AudioFormat _format; bool _hasFormat;
    int _codecRate = 8000;

    readonly Agc _micAgc = new(4200f, 6f), _spkAgc = new(5200f, 10f);
    WaveOutEvent _out; BufferedWaveProvider _outBuf;

    volatile bool _muted, _onHold, _live;
    public bool Recording = true;
    readonly List<short> _recRemote = new(), _recLocal = new();
    readonly object _recLock = new();
    string _path;

    Thread _holdThread;
    HoldAudio.Player _holdPlayer;

    // ---- 3-way conference: audio received on one call is mixed into what we send on the other
    readonly List<Media> _peers = new();
    readonly object _confLock = new();
    readonly Queue<short> _confBuf = new();

    public static void Link(Media a, Media b)
    {
        lock (a._confLock) if (!a._peers.Contains(b)) a._peers.Add(b);
        lock (b._confLock) if (!b._peers.Contains(a)) b._peers.Add(a);
    }
    public static void Unlink(Media a, Media b)
    {
        lock (a._confLock) { a._peers.Remove(b); a._confBuf.Clear(); }
        lock (b._confLock) { b._peers.Remove(a); b._confBuf.Clear(); }
    }
    void PushConf(short[] pcm16k)
    {
        lock (_confLock)
        {
            foreach (var x in pcm16k) _confBuf.Enqueue(x);
            while (_confBuf.Count > Mic.Rate * 3 / 10) _confBuf.Dequeue(); // keep it under 300 ms
        }
    }

    public static Media Create(Program.Line line, bool srtp)
    {
        var m = new Media();
        m._source = new AudioExtrasSource(new AudioEncoder(), new AudioSourceOptions { AudioSource = AudioSourcesEnum.None });
        m._source.RestrictFormats(f => f.Codec == AudioCodecsEnum.G722 || f.Codec == AudioCodecsEnum.PCMU || f.Codec == AudioCodecsEnum.PCMA);
        m.Session = new VoIPMediaSession(new VoIPMediaSessionConfig
        {
            MediaEndPoint = new MediaEndPoints { AudioSource = m._source },
            RtpSecureMediaOption = srtp ? RtpSecureMediaOptionEnum.SdpCryptoNegotiation : RtpSecureMediaOptionEnum.None
        });
        m.Session.AcceptRtpFromAny = true;
        m.Session.OnAudioFormatsNegotiated += formats =>
        {
            if (formats == null || formats.Count == 0) return;
            m._format = formats[0]; m._hasFormat = true;
            m._codecRate = m._format.Codec == AudioCodecsEnum.G722 ? 16000 : 8000;
            m.OpenSpeaker();
        };
        m.Session.OnRtpPacketReceived += (ep, media, pkt) =>
        {
            if (media != SDPMediaTypesEnum.audio || pkt?.Payload == null) return;
            if (pkt.Header.PayloadType > 34 && !(m._hasFormat && pkt.Header.PayloadType == m._format.FormatID)) return; // skip DTMF / comfort noise
            m.OnRemote(pkt.Payload);
        };
        return m;
    }

    AudioFormat Fmt => _hasFormat ? _format : new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMU);

    // ---- speaker: jitter buffer + steady volume
    void OpenSpeaker()
    {
        if (_out != null) return;
        try
        {
            _outBuf = new BufferedWaveProvider(new WaveFormat(_codecRate, 16, 1))
            {
                BufferDuration = TimeSpan.FromMilliseconds(600),
                DiscardOnBufferOverflow = true,
                ReadFully = true
            };
            _out = new WaveOutEvent { DeviceNumber = Program.OutDevice, DesiredLatency = 120, NumberOfBuffers = 3 };
            _out.Init(_outBuf);
            _out.Play();
        }
        catch (Exception ex) { Program.Emit(new { ev = "error", msg = "Could not open the speaker: " + ex.Message }); }
    }

    void OnRemote(byte[] payload)
    {
        short[] pcm;
        try { pcm = _decRemote.DecodeAudio(payload, Fmt); } catch { return; }
        if (pcm == null || pcm.Length == 0) return;
        if (_out == null) { _codecRate = Fmt.Codec == AudioCodecsEnum.G722 ? 16000 : 8000; OpenSpeaker(); }
        _spkAgc.Process(pcm, AudioSettings.SpkGain, AudioSettings.Agc, AudioSettings.NoiseGate);
        if (_outBuf != null)
        {
            // keep latency low: if more than 300 ms is waiting, drop the oldest audio
            if (_outBuf.BufferedDuration > TimeSpan.FromMilliseconds(300)) _outBuf.ClearBuffer();
            var bytes = Pcm.ToBytes(pcm);
            _outBuf.AddSamples(bytes, 0, bytes.Length);
        }
        var p16 = _codecRate == 16000 ? pcm : Pcm.Resample(pcm, 8000, 16000);
        Media[] peers; lock (_confLock) peers = _peers.ToArray();
        foreach (var p in peers) p.PushConf(p16);
        if (Recording && _path != null && !_onHold) lock (_recLock) _recRemote.AddRange(p16);
    }

    // ---- microphone -> customer
    public void Begin() { _live = true; Mic.Subscribe(this); }

    public void OnMic(short[] pcm16k)
    {
        if (!_live || _onHold) return;
        var pcm = (short[])pcm16k.Clone();
        _micAgc.Process(pcm, AudioSettings.MicGain, AudioSettings.Agc, AudioSettings.NoiseGate);
        if (_muted) Array.Clear(pcm);
        if (Recording && _path != null && !_muted) lock (_recLock) _recLocal.AddRange(pcm);
        lock (_confLock)
        {
            for (int i = 0; i < pcm.Length && _confBuf.Count > 0; i++)
                pcm[i] = (short)Math.Clamp(pcm[i] + _confBuf.Dequeue(), -32767, 32767);
        }
        Send(pcm);
    }

    void Send(short[] pcm16k)
    {
        if (!_hasFormat) return;
        var pcm = _codecRate == 16000 ? pcm16k : Pcm.Down2(pcm16k);
        byte[] enc;
        try { enc = _enc.EncodeAudio(pcm, _format); } catch { return; }
        uint units = (uint)(pcm16k.Length * 8000 / Mic.Rate); // RTP clock is 8 kHz for G.711 and G.722
        try { Session.SendAudio(units, enc); } catch { }
    }

    public void SetMute(bool on) => _muted = on;

    // ---- hold: stop the microphone and stream music + announcement to the customer
    public void SetHold(bool on)
    {
        if (on == _onHold) return;
        _onHold = on;
        if (on)
        {
            _holdPlayer = new HoldAudio.Player();
            _holdThread = new Thread(HoldLoop) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "wego-hold" };
            _holdThread.Start();
        }
    }

    void HoldLoop()
    {
        var sw = Stopwatch.StartNew();
        long next = 0; int frame = Mic.Rate * Mic.FrameMs / 1000;
        while (_onHold && _live)
        {
            long now = sw.ElapsedMilliseconds;
            if (now >= next)
            {
                Send(_holdPlayer.Next(frame));
                next += Mic.FrameMs;
                if (now - next > 200) next = now; // resync after a stall
            }
            else Thread.Sleep(1);
        }
    }

    // ---- recording
    public void StartRecording(string number, bool inbound)
    {
        var now = DateTime.Now;
        var dir = Path.Combine(Program.RecordingFolder, now.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(dir);
        var clean = new string((number ?? "unknown").Where(char.IsLetterOrDigit).ToArray());
        _path = Path.Combine(dir, $"{now:HH-mm-ss}_{(inbound ? "in" : "out")}_{(clean.Length > 0 ? clean : "unknown")}.wav");
    }

    public string Close()
    {
        _live = false; _onHold = false;
        Mic.Unsubscribe(this);
        try { Session?.Close("call ended"); } catch { }
        try { _out?.Stop(); _out?.Dispose(); } catch { }
        _out = null;
        if (_path == null) return null;
        short[] a, b;
        lock (_recLock) { a = _recRemote.ToArray(); b = _recLocal.ToArray(); }
        if (a.Length == 0 && b.Length == 0) return null;
        WriteWav(_path, a, b, 16000);
        return _path;
    }

    // Stereo WAV: left = customer, right = agent
    static void WriteWav(string path, short[] left, short[] right, int rate)
    {
        int n = Math.Max(left.Length, right.Length);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);
        int dataLen = n * 4;
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + dataLen); w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)2);
        w.Write(rate); w.Write(rate * 4); w.Write((short)4); w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data")); w.Write(dataLen);
        for (int i = 0; i < n; i++)
        {
            w.Write(i < left.Length ? left[i] : (short)0);
            w.Write(i < right.Length ? right[i] : (short)0);
        }
    }
}

internal static class TimerResolution
{
    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint p);
    public static void High() { try { timeBeginPeriod(1); } catch { } }
}
