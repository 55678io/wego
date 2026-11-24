// Wego SIP engine
// The background program that does the real telephony for the Wego app:
// SIP over UDP, TCP or TLS (optionally with SRTP), several accounts at once, audio through
// the Windows sound devices, hold (with music), mute, DTMF, blind and attended transfer,
// call waiting, 3-way conference, call forwarding, BLF and voicemail indicator, recording.
// It talks to the Wego app with one JSON object per line on stdin (commands) and stdout (events).

using System.Collections.Concurrent;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;

namespace Wego.Engine;

public static class Program
{
    static readonly object OutLock = new();
    static SIPTransport _transport;
    static readonly ConcurrentDictionary<string, AccountState> Accounts = new();
    static readonly ConcurrentDictionary<string, Line> Lines = new();
    static int _inDevice = -1, _outDevice = -1;
    static string _recDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Wego Recordings");

    // call forwarding
    static string _fwdMode = "off", _fwdTarget = "";
    static int _fwdAfter = 20;

    public static async Task Main()
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;
        TimerResolution.High();
        Emit(new { ev = "ready", version = "1.3" });
        HoldAudio.Init();

        string line;
        while ((line = await Console.In.ReadLineAsync()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try { await Handle(line); }
            catch (Exception ex) { Emit(new { ev = "error", msg = ex.Message }); }
        }
        Shutdown();
    }

    // ------------------------------------------------------------------ commands
    static async Task Handle(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        string cmd = Str(r, "cmd");
        string id = Str(r, "id");

        switch (cmd)
        {
            case "register": Register(r); break;
            case "unregister": Unregister(Str(r, "acc")); break;
            case "call": _ = PlaceCall(id, Str(r, "target"), Str(r, "acc")); break;
            case "answer": await Answer(id); break;
            case "reject": Reject(id, Int(r, "code", 486)); break;
            case "hangup": Hangup(id); break;
            case "hold": Hold(id, Bool(r, "on")); break;
            case "mute": Mute(id, Bool(r, "on")); break;
            case "dtmf": await Dtmf(id, Str(r, "digit")); break;
            case "transfer": await Transfer(id, Str(r, "target")); break;
            case "attxfer": await AttendedTransfer(id, Str(r, "with")); break;
            case "conference": Conference(id, Str(r, "with"), Bool(r, "on")); break;
            case "record": SetRecording(id, Bool(r, "on")); break;
            case "forward":
                _fwdMode = (Str(r, "mode") ?? "off").ToLowerInvariant();
                _fwdTarget = Str(r, "target") ?? "";
                _fwdAfter = Math.Clamp(Int(r, "after", 20), 5, 120);
                if (string.IsNullOrWhiteSpace(_fwdTarget)) _fwdMode = "off";
                Emit(new { ev = "forward", mode = _fwdMode, target = _fwdTarget });
                break;
            case "blf": Blf(Str(r, "acc"), r); break;
            case "devices": Emit(new { ev = "devices", inputs = AudioDevices.Inputs(), outputs = AudioDevices.Outputs() }); break;
            case "setDevices":
                _inDevice = Int(r, "input", -1); _outDevice = Int(r, "output", -1); Mic.Restart();
                Emit(new { ev = "log", msg = $"Audio devices set: in={_inDevice} out={_outDevice}" }); break;
            case "setAudio":
                AudioSettings.Agc = !r.TryGetProperty("agc", out var ag) || ag.ValueKind != JsonValueKind.False;
                AudioSettings.NoiseGate = !r.TryGetProperty("gate", out var ng) || ng.ValueKind != JsonValueKind.False;
                AudioSettings.MicGain = Math.Clamp(Flt(r, "micGain", 1f), 0.25f, 4f);
                AudioSettings.SpkGain = Math.Clamp(Flt(r, "spkGain", 1f), 0.25f, 4f);
                break;
            case "setHold":
                HoldAudio.Announce = !r.TryGetProperty("announce", out var an) || an.ValueKind != JsonValueKind.False;
                HoldAudio.AnnounceAfterSec = Math.Clamp(Int(r, "after", 30), 5, 600);
                var music = Str(r, "music");
                var text = Str(r, "text");
                _ = Task.Run(() =>
                {
                    HoldAudio.LoadMusic(music);
                    if (!string.IsNullOrWhiteSpace(text) && text != HoldAudio.Text || HoldAudio.Announcement.Length == 0) HoldAudio.MakeAnnouncement(text);
                });
                break;
            case "previewHold": HoldAudio.Preview(Int(r, "seconds", 40)); break;
            case "stopPreview": HoldAudio.StopPreview(); break;
            case "setRecordingFolder":
                var dir = Str(r, "path"); if (!string.IsNullOrWhiteSpace(dir)) _recDir = dir; break;
            case "quit": Shutdown(); Environment.Exit(0); break;
            default: Emit(new { ev = "error", msg = "Unknown command: " + cmd }); break;
        }
    }

    // ------------------------------------------------------------------ transport + accounts
    static void EnsureTransport()
    {
        if (_transport != null) return;
        _transport = new SIPTransport();
        _transport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(IPAddress.Any, 0)));
        _transport.AddSIPChannel(new SIPTCPChannel(new IPEndPoint(IPAddress.Any, 0)));
        try { _transport.AddSIPChannel(new SIPTLSChannel(new IPEndPoint(IPAddress.Any, 0))); }
        catch (Exception ex) { Emit(new { ev = "log", msg = "TLS not available: " + ex.Message }); }
        _transport.SIPTransportRequestReceived += OnRequest;
    }

    static void Register(JsonElement r)
    {
        var accId = Str(r, "acc") ?? "a1";
        Unregister(accId, quiet: true);
        var acc = new Account
        {
            Id = accId,
            Label = Str(r, "label") ?? accId,
            User = Str(r, "user"),
            AuthUser = string.IsNullOrWhiteSpace(Str(r, "auth")) ? Str(r, "user") : Str(r, "auth"),
            Password = Str(r, "pass"),
            Domain = Str(r, "domain"),
            Proxy = Str(r, "proxy"),
            Transport = (Str(r, "transport") ?? "udp").ToLowerInvariant(),
            Expiry = Math.Clamp(Int(r, "expiry", 300), 60, 3600),
            DisplayName = Str(r, "display"),
            Srtp = Bool(r, "srtp"),
            Voicemail = Str(r, "voicemail"),
        };
        if (string.IsNullOrWhiteSpace(acc.User) || string.IsNullOrWhiteSpace(acc.Domain))
        {
            Emit(new { ev = "reg", acc = accId, state = "failed", reason = "Username and domain are required" }); return;
        }
        EnsureTransport();

        var st = new AccountState { Acc = acc };
        Accounts[accId] = st;
        string server = acc.ServerFor(acc.Domain);
        st.Reg = new SIPRegistrationUserAgent(_transport, acc.AuthUser == acc.User ? acc.User : acc.AuthUser, acc.Password, server, acc.Expiry);
        st.Reg.RegistrationSuccessful += (uri, resp) =>
        {
            st.Registered = true;
            Emit(new { ev = "reg", acc = accId, state = "ok", reason = "Registered", uri = uri?.ToString() });
            if (st.Mwi == null) StartMwi(st);
        };
        st.Reg.RegistrationFailed += (uri, resp, err) => { st.Registered = false; Emit(new { ev = "reg", acc = accId, state = "failed", reason = Describe(resp, err), code = resp == null ? 0 : (int)resp.Status }); };
        st.Reg.RegistrationTemporaryFailure += (uri, resp, err) => { st.Registered = false; Emit(new { ev = "reg", acc = accId, state = "retrying", reason = Describe(resp, err), code = resp == null ? 0 : (int)resp.Status }); };
        st.Reg.RegistrationRemoved += (uri, resp) => { st.Registered = false; Emit(new { ev = "reg", acc = accId, state = "off", reason = "Registration removed" }); };
        Emit(new { ev = "reg", acc = accId, state = "connecting", reason = "Registering over " + acc.Transport.ToUpperInvariant() });
        st.Reg.Start();
    }

    static void Unregister(string accId, bool quiet = false)
    {
        var ids = accId == null ? Accounts.Keys.ToList() : new List<string> { accId };
        foreach (var a in ids)
        {
            if (!Accounts.TryRemove(a, out var st)) continue;
            foreach (var l in Lines.Values.Where(l => l.Acc == st.Acc)) { try { l.Hangup(); } catch { } }
            try { st.Reg?.Stop(); } catch { }
            try { st.Mwi?.Stop(); } catch { }
            foreach (var b in st.Blf.Values) { try { b.Stop(); } catch { } }
            if (!quiet) Emit(new { ev = "reg", acc = a, state = "off", reason = "Disconnected" });
        }
        if (Accounts.IsEmpty && _transport != null)
        {
            foreach (var l in Lines.Values) { try { l.Hangup(); } catch { } }
            _transport.SIPTransportRequestReceived -= OnRequest;
            var t = _transport; _transport = null;
            _ = Task.Run(async () => { await Task.Delay(1500); try { t.Shutdown(); } catch { } });
        }
    }

    static void Shutdown() { try { Unregister(null, quiet: true); } catch { } }

    static Account AccountFor(string accId)
    {
        if (accId != null && Accounts.TryGetValue(accId, out var st)) return st.Acc;
        return Accounts.Values.OrderByDescending(s => s.Registered).Select(s => s.Acc).FirstOrDefault();
    }

    // ------------------------------------------------------------------ voicemail indicator (MWI) + BLF
    static void StartMwi(AccountState st)
    {
        try
        {
            var acc = st.Acc;
            var uri = SIPURI.ParseSIPURIRelaxed(acc.ServerFor($"{acc.User}@{acc.Domain}"));
            st.Mwi = new SIPNotifierClient(_transport, acc.OutboundProxy(), SIPEventPackagesEnum.MessageSummary, uri, acc.AuthUser, acc.Domain, acc.Password, 3600, null);
            st.Mwi.NotificationReceived += (pkg, body) => ParseMwi(st.Acc.Id, body);
            st.Mwi.Start();
        }
        catch (Exception ex) { Emit(new { ev = "log", msg = "Voicemail indicator not available: " + ex.Message }); }
    }

    static void ParseMwi(string accId, string body)
    {
        if (string.IsNullOrEmpty(body)) return;
        bool waiting = Regex.IsMatch(body, @"Messages-Waiting:\s*yes", RegexOptions.IgnoreCase);
        int newMsgs = 0, oldMsgs = 0;
        var m = Regex.Match(body, @"Voice-Message:\s*(\d+)\s*/\s*(\d+)", RegexOptions.IgnoreCase);
        if (m.Success) { newMsgs = int.Parse(m.Groups[1].Value); oldMsgs = int.Parse(m.Groups[2].Value); }
        Emit(new { ev = "mwi", acc = accId, waiting, newMsgs, oldMsgs });
    }

    static void Blf(string accId, JsonElement r)
    {
        var acc = AccountFor(accId);
        if (acc == null || !Accounts.TryGetValue(acc.Id, out var st)) return;
        foreach (var b in st.Blf.Values) { try { b.Stop(); } catch { } }
        st.Blf.Clear();
        if (!r.TryGetProperty("exts", out var arr) || arr.ValueKind != JsonValueKind.Array) return;
        foreach (var e in arr.EnumerateArray())
        {
            var ext = e.GetString();
            if (string.IsNullOrWhiteSpace(ext)) continue;
            try
            {
                var uri = SIPURI.ParseSIPURIRelaxed(acc.ServerFor($"{ext}@{acc.Domain}"));
                var client = new SIPNotifierClient(_transport, acc.OutboundProxy(), SIPEventPackagesEnum.Dialog, uri, acc.AuthUser, acc.Domain, acc.Password, 3600, null);
                client.NotificationReceived += (pkg, body) => Emit(new { ev = "blf", ext, state = DialogState(body) });
                client.Start();
                st.Blf[ext] = client;
                Emit(new { ev = "blf", ext, state = "unknown" });
            }
            catch (Exception ex) { Emit(new { ev = "blf", ext, state = "unknown", error = ex.Message }); }
        }
    }

    static string DialogState(string xml)
    {
        if (string.IsNullOrEmpty(xml)) return "idle";
        var states = Regex.Matches(xml, @"<state[^>]*>\s*([a-z]+)\s*</state>", RegexOptions.IgnoreCase).Select(m => m.Groups[1].Value.ToLowerInvariant()).ToList();
        if (states.Contains("confirmed")) return "busy";
        if (states.Contains("early") || states.Contains("proceeding") || states.Contains("trying")) return "ringing";
        return "idle";
    }

    // ------------------------------------------------------------------ incoming requests
    static Task OnRequest(SIPEndPoint local, SIPEndPoint remote, SIPRequest req)
    {
        bool inDialog = req.Header.To != null && !string.IsNullOrEmpty(req.Header.To.ToTag);
        if (inDialog) return Task.CompletedTask; // handled by the call's own user agent or subscription

        if (req.Method == SIPMethodsEnum.INVITE)
        {
            string toUser = req.URI?.User ?? req.Header.To?.ToURI?.User;
            var acc = Accounts.Values.Select(s => s.Acc).FirstOrDefault(a => string.Equals(a.User, toUser, StringComparison.OrdinalIgnoreCase))
                      ?? AccountFor(null);
            var id = "in-" + Guid.NewGuid().ToString("N")[..10];
            var line = new Line(id, inbound: true) { Acc = acc };
            Lines[id] = line;
            line.Ua = NewUa(line);
            line.Uas = line.Ua.AcceptCall(req);
            line.Number = req.Header.From?.FromURI?.User ?? "";
            line.Name = req.Header.From?.FromName ?? "";
            bool busy = Lines.Values.Any(l => l != line && l.Active);

            // call forwarding
            if (_fwdMode == "always" || (_fwdMode == "busy" && busy))
            {
                if (Forward(line, "Forwarded to " + _fwdTarget)) return Task.CompletedTask;
            }
            try { line.Uas.Progress(SIPResponseStatusCodesEnum.Ringing, null, null, null, null); } catch { }
            Emit(new { ev = "incoming", id, acc = acc?.Id, number = line.Number, name = line.Name, waiting = busy });
            if (_fwdMode == "noanswer")
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(_fwdAfter * 1000);
                    if (!line.Ended && !line.Active) Forward(line, $"No answer – forwarded to {_fwdTarget}");
                });
            }
        }
        else if (req.Method == SIPMethodsEnum.NOTIFY)
        {
            // unsolicited voicemail notifications
            if ((req.Header.Event ?? "").Contains("message-summary", StringComparison.OrdinalIgnoreCase))
            {
                string toUser = req.Header.To?.ToURI?.User;
                var acc = Accounts.Values.Select(s => s.Acc).FirstOrDefault(a => string.Equals(a.User, toUser, StringComparison.OrdinalIgnoreCase)) ?? AccountFor(null);
                ParseMwi(acc?.Id, req.Body);
            }
            var ok = SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.Ok, null);
            return _transport?.SendResponseAsync(ok) ?? Task.CompletedTask;
        }
        else if (req.Method == SIPMethodsEnum.OPTIONS)
        {
            var ok = SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.Ok, null);
            return _transport?.SendResponseAsync(ok) ?? Task.CompletedTask;
        }
        return Task.CompletedTask;
    }

    static bool Forward(Line line, string reason)
    {
        try
        {
            var acc = line.Acc;
            string dst = _fwdTarget.Contains('@') ? _fwdTarget : $"{CleanNumber(_fwdTarget)}@{acc?.Domain}";
            var uri = SIPURI.ParseSIPURIRelaxed(acc != null ? acc.ServerFor(dst) : dst);
            line.Uas.Redirect(SIPResponseStatusCodesEnum.MovedTemporarily, uri);
            line.End("local", reason, 302, forwarded: true);
            return true;
        }
        catch (Exception ex) { Emit(new { ev = "log", msg = "Forward failed: " + ex.Message }); return false; }
    }

    static SIPUserAgent NewUa(Line line)
    {
        var ua = new SIPUserAgent(_transport, line.Acc?.OutboundProxy());
        ua.OnCallHungup += dlg => line.End("remote", "Customer ended the call", 0);
        ua.ServerCallCancelled += (uas, req) => line.End("remote", "Caller hung up before answer", 487);
        ua.ClientCallRinging += (uac, resp) => Emit(new { ev = "ringing", id = line.Id });
        ua.ClientCallFailed += (uac, err, resp) =>
            line.End("remote", Describe(resp, err), resp == null ? 0 : (int)resp.Status);
        ua.OnDtmfTone += (key, dur) => Emit(new { ev = "dtmfIn", id = line.Id, key = DtmfChar(key) });
        return ua;
    }

    // ------------------------------------------------------------------ calls
    static async Task PlaceCall(string id, string target, string accId)
    {
        var acc = AccountFor(accId);
        if (_transport == null || acc == null) { Emit(new { ev = "ended", id, by = "local", reason = "Not connected to the phone system", code = 0 }); return; }
        var line = new Line(id, inbound: false) { Number = target, Acc = acc };
        Lines[id] = line;
        line.Ua = NewUa(line);
        line.Media = Media.Create(line, acc.Srtp);
        string dst = target.Contains('@') ? target : $"{CleanNumber(target)}@{acc.Domain}";
        dst = acc.ServerFor(dst);
        if (!dst.StartsWith("sip", StringComparison.OrdinalIgnoreCase)) dst = (acc.Transport == "tls" && acc.Srtp ? "sip:" : "sip:") + dst;
        Emit(new { ev = "calling", id, acc = acc.Id, target = dst });
        bool ok;
        try { ok = await line.Ua.Call(dst, acc.AuthUser, acc.Password, line.Media.Session); }
        catch (Exception ex) { line.End("local", "Call failed: " + ex.Message, 0); return; }
        if (ok) line.Connected();
        else if (!line.Ended) line.End("remote", "No answer", 0);
    }

    static async Task Answer(string id)
    {
        if (!Lines.TryGetValue(id, out var line) || line.Uas == null) return;
        // call waiting: put the other active call on hold first
        foreach (var other in Lines.Values.Where(l => l != line && l.Active && !l.OnHold)) { other.Unlink(); other.SetHold(true); }
        line.Media = Media.Create(line, line.Acc?.Srtp ?? false);
        bool ok = await line.Ua.Answer(line.Uas, line.Media.Session);
        if (ok) line.Connected();
        else line.End("local", "Could not answer the call", 0);
    }

    static void Reject(string id, int code)
    {
        if (!Lines.TryGetValue(id, out var line)) return;
        var status = code == 603 ? SIPResponseStatusCodesEnum.Decline : SIPResponseStatusCodesEnum.BusyHere;
        try { line.Uas?.Reject(status, null); } catch { }
        line.End("local", "Declined by agent", code);
    }

    static void Hangup(string id) { if (Lines.TryGetValue(id, out var line)) line.Hangup(); }
    static void Hold(string id, bool on) { if (Lines.TryGetValue(id, out var line)) { if (on) line.Unlink(); line.SetHold(on); } }
    static void Mute(string id, bool on) { if (Lines.TryGetValue(id, out var line)) line.Media?.SetMute(on); }
    static void SetRecording(string id, bool on) { if (Lines.TryGetValue(id, out var line) && line.Media != null) line.Media.Recording = on; }

    static async Task Dtmf(string id, string digit)
    {
        if (!Lines.TryGetValue(id, out var line) || line.Ua == null || string.IsNullOrEmpty(digit)) return;
        byte code = digit[0] switch { '*' => 10, '#' => 11, >= '0' and <= '9' => (byte)(digit[0] - '0'), _ => 255 };
        if (code != 255) await line.Ua.SendDtmf(code);
    }

    static async Task Transfer(string id, string target)
    {
        if (!Lines.TryGetValue(id, out var line) || line.Ua == null) return;
        var acc = line.Acc ?? AccountFor(null);
        string dst = target.Contains('@') ? target : $"{CleanNumber(target)}@{acc?.Domain}";
        var uri = SIPURI.ParseSIPURIRelaxed(acc != null ? acc.ServerFor(dst) : dst);
        Emit(new { ev = "transfer", id, state = "trying", target });
        bool ok = await line.Ua.BlindTransfer(uri, TimeSpan.FromSeconds(15), CancellationToken.None);
        Emit(new { ev = "transfer", id, state = ok ? "ok" : "failed", target });
        if (ok) line.End("local", "Transferred to " + target, 0, hangup: true);
    }

    // Attended transfer: connect the customer on `id` (usually on hold) with the person on `with`.
    static async Task AttendedTransfer(string id, string with)
    {
        if (!Lines.TryGetValue(id, out var a) || !Lines.TryGetValue(with ?? "", out var b) || a.Ua == null || b.Ua?.Dialogue == null) return;
        a.Unlink(); b.Unlink();
        Emit(new { ev = "transfer", id, state = "trying", target = b.Number });
        bool ok;
        try { ok = await a.Ua.AttendedTransfer(b.Ua.Dialogue, TimeSpan.FromSeconds(15), CancellationToken.None); }
        catch (Exception ex) { ok = false; Emit(new { ev = "log", msg = "Attended transfer: " + ex.Message }); }
        Emit(new { ev = "transfer", id, state = ok ? "ok" : "failed", target = b.Number });
        if (ok)
        {
            a.End("local", "Connected to " + b.Number + " (attended transfer)", 0, hangup: true);
            b.End("local", "Connected to " + a.Number + " (attended transfer)", 0, hangup: true);
        }
    }

    // 3-way conference: both calls off hold and their audio mixed together in Wego.
    static void Conference(string id, string with, bool on)
    {
        if (!Lines.TryGetValue(id, out var a) || !Lines.TryGetValue(with ?? "", out var b)) return;
        if (!a.Active || !b.Active || a.Media == null || b.Media == null) return;
        if (on)
        {
            if (a.OnHold) a.SetHold(false);
            if (b.OnHold) b.SetHold(false);
            Media.Link(a.Media, b.Media);
            a.ConfWith = b; b.ConfWith = a;
        }
        else { a.Unlink(); a.SetHold(true); }
        Emit(new { ev = "conference", id, with, on });
    }

    // ------------------------------------------------------------------ helpers
    internal static void Emit(object o)
    {
        var s = JsonSerializer.Serialize(o);
        lock (OutLock) { Console.Out.WriteLine(s); Console.Out.Flush(); }
    }

    internal static string RecordingFolder => _recDir;
    internal static int InDevice => _inDevice;
    internal static int OutDevice => _outDevice;
    internal static void Remove(string id) => Lines.TryRemove(id, out _);

    static string Str(JsonElement r, string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static int Int(JsonElement r, string k, int d) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : d;
    static float Flt(JsonElement r, string k, float d) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var x) ? (float)x : d;
    static bool Bool(JsonElement r, string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
    static string CleanNumber(string n) => new string((n ?? "").Where(c => char.IsDigit(c) || c == '*' || c == '#' || c == '+').ToArray());
    static string DtmfChar(byte k) => k switch { 10 => "*", 11 => "#", < 10 => k.ToString(), _ => "?" };

    internal static string Describe(SIPResponse resp, string err)
    {
        if (resp == null) return string.IsNullOrWhiteSpace(err) ? "No response from server" : err;
        int c = (int)resp.Status;
        string text = c switch
        {
            401 or 403 or 407 => "Wrong username or password",
            404 => "Number not found",
            408 => "No answer",
            480 => "No answer / unavailable",
            486 or 600 => "Line busy",
            487 => "Call cancelled",
            488 => "Audio format or encryption not supported",
            503 => "Phone system unavailable",
            603 => "Call declined",
            _ => resp.ReasonPhrase
        };
        return $"{text} ({c})";
    }

    // ------------------------------------------------------------------ account
    internal class Account
    {
        public string Id = "a1", Label, User, AuthUser, Password, Domain, Proxy, Transport = "udp", DisplayName, Voicemail;
        public int Expiry = 300;
        public bool Srtp;

        public string ServerFor(string hostOrUri)
        {
            string s = hostOrUri;
            if (Transport == "tcp") s += ";transport=tcp";
            else if (Transport == "tls") s += ";transport=tls";
            return s;
        }

        public SIPEndPoint OutboundProxy()
        {
            if (string.IsNullOrWhiteSpace(Proxy)) return null;
            try
            {
                var proto = Transport == "tcp" ? "tcp" : Transport == "tls" ? "tls" : "udp";
                var p = Proxy.Contains(':') ? Proxy : Proxy + (proto == "tls" ? ":5061" : ":5060");
                return SIPEndPoint.ParseSIPEndPoint($"{proto}:{p}");
            }
            catch { return null; }
        }
    }

    internal class AccountState
    {
        public Account Acc;
        public SIPRegistrationUserAgent Reg;
        public SIPNotifierClient Mwi;
        public readonly ConcurrentDictionary<string, SIPNotifierClient> Blf = new();
        public bool Registered;
    }

    // ------------------------------------------------------------------ a call line
    internal class Line
    {
        public readonly string Id;
        public readonly bool Inbound;
        public Account Acc;
        public SIPUserAgent Ua;
        public SIPServerUserAgent Uas;
        public Media Media;
        public Line ConfWith;
        public string Number = "", Name = "";
        public bool Active, OnHold, Ended;
        DateTime _answeredAt;

        public Line(string id, bool inbound) { Id = id; Inbound = inbound; }

        public void Connected()
        {
            Active = true; _answeredAt = DateTime.Now;
            Media?.StartRecording(Number, Inbound);
            Media?.Begin();
            Emit(new { ev = "answered", id = Id });
        }

        public void SetHold(bool on)
        {
            if (Ua == null || !Active) return;
            try { if (on) Ua.PutOnHold(); else Ua.TakeOffHold(); } catch { }
            OnHold = on;
            Media?.SetHold(on);
            Emit(new { ev = "held", id = Id, on });
        }

        public void Unlink()
        {
            var other = ConfWith;
            if (other == null) return;
            ConfWith = null; other.ConfWith = null;
            if (Media != null && other.Media != null) Media.Unlink(Media, other.Media);
            Emit(new { ev = "conference", id = Id, with = other.Id, on = false });
        }

        public void Hangup()
        {
            if (Ended) return;
            try
            {
                if (Active) Ua?.Hangup();
                else if (Inbound) Uas?.Reject(SIPResponseStatusCodesEnum.BusyHere, null);
                else Ua?.Cancel();
            }
            catch { }
            End("local", Active ? "Agent ended the call" : (Inbound ? "Declined by agent" : "Cancelled by agent before answer"), 0);
        }

        public void End(string by, string reason, int code, bool hangup = false, bool forwarded = false)
        {
            if (Ended) return;
            Ended = true;
            Unlink();
            if (hangup) { try { Ua?.Hangup(); } catch { } }
            string rec = null;
            try { rec = Media?.Close(); } catch { }
            int dur = Active ? (int)(DateTime.Now - _answeredAt).TotalSeconds : 0;
            Active = false;
            Remove(Id);
            Emit(new { ev = "ended", id = Id, by, reason, code, duration = dur, recording = rec, forwarded });
        }
    }
}

// ---------------------------------------------------------------------- audio devices (winmm)
static class AudioDevices
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WAVEINCAPS { public ushort wMid, wPid; public uint vDriverVersion; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname; public uint dwFormats; public ushort wChannels, wReserved1; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WAVEOUTCAPS { public ushort wMid, wPid; public uint vDriverVersion; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname; public uint dwFormats; public ushort wChannels, wReserved1; public uint dwSupport; }

    [DllImport("winmm.dll")] static extern int waveInGetNumDevs();
    [DllImport("winmm.dll")] static extern int waveOutGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "waveInGetDevCapsW")] static extern int waveInGetDevCaps(IntPtr id, ref WAVEINCAPS caps, int size);
    [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "waveOutGetDevCapsW")] static extern int waveOutGetDevCaps(IntPtr id, ref WAVEOUTCAPS caps, int size);

    public static List<object> Inputs()
    {
        var list = new List<object>();
        try
        {
            int n = waveInGetNumDevs();
            for (int i = 0; i < n; i++)
            {
                var c = new WAVEINCAPS();
                if (waveInGetDevCaps((IntPtr)i, ref c, Marshal.SizeOf<WAVEINCAPS>()) == 0) list.Add(new { index = i, name = c.szPname });
            }
        }
        catch { }
        return list;
    }

    public static List<object> Outputs()
    {
        var list = new List<object>();
        try
        {
            int n = waveOutGetNumDevs();
            for (int i = 0; i < n; i++)
            {
                var c = new WAVEOUTCAPS();
                if (waveOutGetDevCaps((IntPtr)i, ref c, Marshal.SizeOf<WAVEOUTCAPS>()) == 0) list.Add(new { index = i, name = c.szPname });
            }
        }
        catch { }
        return list;
    }
}
