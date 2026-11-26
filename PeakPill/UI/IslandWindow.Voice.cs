using System.Speech.Recognition;
using System.Text.RegularExpressions;
using System.Windows.Input;
using System.Windows.Media;
using PeakPill.Core;
using PeakPill.Services;

namespace PeakPill.UI;

/// <summary>
/// 57 voice commands (user's pick 2026-11-18, made more reliable 2026-11-19 after "funktioniert nicht gut"):
/// hold the hotkey, speak, let go. With a Gemini or ChatGPT key the words are written down by that service (the same as
/// dictating in the KI chat) – much more exact. Without one, Windows' own recognition listens only for the island's
/// commands (timer, next song, note, open an app …) instead of free text, so it rarely mishears.
/// </summary>
public partial class IslandWindow
{
    private bool _voiceBusy;
    private SpeechRecognitionEngine _voice;
    private RecognitionResult _voiceBest;
    private TaskCompletionSource<bool> _voiceDone;

    private async void VoiceCommand()
    {
        if (_voiceBusy) return;
        _voiceBusy = true;
        try
        {
            if (Dictation.Writer() is { } writer) await VoiceByAi(writer);
            else await VoiceByWindows();
        }
        finally { _voiceBusy = false; }
    }

    /// <summary>Waits while the hotkey's key is held (at most 15 s).</summary>
    private async Task WhileHeld()
    {
        int vk = VoiceKey();
        var until = DateTime.Now.AddSeconds(15);
        await Task.Delay(150);
        while (DateTime.Now < until && (GetAsyncKeyState(vk) & 0x8000) != 0) await Task.Delay(50);
    }

    private void Listening()
    {
        _hintHoldMs = 20000;
        ShowHint("mic", L.T("Hört zu …"), (Brush)FindResource("Danger"), key: "voice");
    }

    // ---------- with a Gemini / ChatGPT key ----------

    private async Task VoiceByAi(AiProvider writer)
    {
        using var d = new Dictation();
        try { d.Start(); }
        catch { ShowHint("mic.slash", L.T("Kein Mikrofon"), (Brush)FindResource("Danger"), key: "voice"); return; }
        Listening();
        await WhileHeld();
        var wav = await d.StopAsync();
        _hintHoldMs = 8000;
        ShowHint("sparkles", L.T("Versteht …"), (Brush)FindResource("Blue"), key: "voice");
        string said;
        try { using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)); said = await Dictation.TranscribeAsync(wav, writer, cts.Token); }
        catch (Exception ex) { App.Log(ex); ShowHint("xmark", L.T("Nicht verstanden"), (Brush)FindResource("SubText"), key: "voice"); return; }
        RunSpoken(said?.Trim() ?? "");
    }

    // ---------- Windows, offline: only the island's commands ----------

    private async Task VoiceByWindows()
    {
        var culture = SpeechRecognitionEngine.InstalledRecognizers().Select(r => r.Culture)
            .OrderByDescending(c => c.TwoLetterISOLanguageName == (L.English ? "en" : "de")).FirstOrDefault();
        if (culture == null)
        {
            ShowHint("mic.slash", L.T("Windows-Spracherkennung fehlt (Sprachpaket)"), (Brush)FindResource("Danger"), key: "voice");
            return;
        }
        try
        {
            if (_voice == null || !Equals(_voice.RecognizerInfo.Culture, culture))
            {
                _voice?.Dispose();
                _voice = new SpeechRecognitionEngine(culture);
                foreach (var g in CommandGrammars(culture.TwoLetterISOLanguageName == "de")) _voice.LoadGrammar(g);
                _voice.SpeechRecognized += (_, e) => { if (_voiceBest == null || e.Result.Confidence > _voiceBest.Confidence) _voiceBest = e.Result; };
                _voice.RecognizeCompleted += (_, _) => _voiceDone?.TrySetResult(true);
            }
            _voice.SetInputToDefaultAudioDevice();
            _voiceBest = null;
            _voiceDone = new TaskCompletionSource<bool>();
            _voice.RecognizeAsync(RecognizeMode.Multiple);
        }
        catch (Exception ex)
        {
            App.Log(ex);
            ShowHint("mic.slash", L.T("Kein Mikrofon"), (Brush)FindResource("Danger"), key: "voice");
            return;
        }
        Listening();
        await WhileHeld();
        try { _voice.RecognizeAsyncStop(); } catch { }
        await Task.WhenAny(_voiceDone.Task, Task.Delay(2000));
        RunSpoken(_voiceBest is { Confidence: > 0.35f } r ? r.Text : "");
    }

    /// <summary>Only what the island can do – that is what makes Windows' recognition reliable here.</summary>
    private List<Grammar> CommandGrammars(bool de)
    {
        var list = new List<Grammar>();
        var numbers = new Choices(Enumerable.Range(1, 60).Concat(new[] { 90, 120 }).Select(n => NumberWord(n, de)).ToArray());
        var units = new Choices(de ? new[] { "minuten", "minute", "sekunden", "stunden", "stunde" } : new[] { "minutes", "minute", "seconds", "hours", "hour" });
        var timer = new GrammarBuilder(new Choices(de ? new[] { "timer", "timer auf", "stell einen timer auf", "starte einen timer mit" } : new[] { "timer", "set a timer for", "start a timer for" }));
        timer.Append(numbers);
        timer.Append(units);
        list.Add(new Grammar(timer) { Name = "timer" });
        list.Add(new Grammar(new GrammarBuilder(new Choices(de
            ? new[] { "nächster titel", "nächstes lied", "nächster song", "weiter", "vorheriger titel", "vorheriges lied", "zurück", "pause", "stopp", "abspielen", "musik an", "musik aus", "fokus starten", "einstellungen", "welcher song ist das", "welches lied ist das", "welcher song läuft", "song erkennen", "lied erkennen", "shazam" }
            : new[] { "next song", "next track", "skip", "previous song", "previous track", "back", "pause", "stop", "play", "music on", "music off", "start focus", "settings", "what song is this", "which song is this", "recognize song", "shazam" }))) { Name = "media" });
        var note = new GrammarBuilder(de ? "notiz" : "note");
        note.AppendDictation();
        list.Add(new Grammar(note) { Name = "note" });
        var tabs = de ? new[] { "musik", "zwischenablage", "ablage", "claude", "mitteilungen", "timer", "heute", "erinnerungen", "steuerung", "notizen", "ki" }
                      : new[] { "music", "clipboard", "shelf", "claude", "notifications", "timer", "today", "reminders", "controls", "notes", "ai" };
        if (_apps == null) LoadApps();
        var names = tabs.Concat((_apps ?? new()).Select(a => a.Name.ToLowerInvariant()).Where(n => n.Length is > 1 and < 40)).Distinct().Take(400).ToArray();
        var open = new GrammarBuilder(new Choices(de ? new[] { "öffne", "starte", "zeig" } : new[] { "open", "start", "show" }));
        open.Append(new Choices(names));
        list.Add(new Grammar(open) { Name = "open" });
        return list;
    }

    /// <summary>1 → "eins" / "one", 25 → "fünfundzwanzig" / "twenty five".</summary>
    private static string NumberWord(int n, bool de)
    {
        string[] deOnes = { "", "eins", "zwei", "drei", "vier", "fünf", "sechs", "sieben", "acht", "neun", "zehn", "elf", "zwölf", "dreizehn", "vierzehn", "fünfzehn", "sechzehn", "siebzehn", "achtzehn", "neunzehn" };
        string[] deTens = { "", "", "zwanzig", "dreißig", "vierzig", "fünfzig", "sechzig", "siebzig", "achtzig", "neunzig" };
        string[] enOnes = { "", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen" };
        string[] enTens = { "", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety" };
        if (n == 120) return de ? "hundertzwanzig" : "hundred twenty";
        if (n < 20) return de ? deOnes[n] : enOnes[n];
        int t = n / 10, o = n % 10;
        if (de) return o == 0 ? deTens[t] : (o == 1 ? "ein" : deOnes[o]) + "und" + deTens[t];
        return o == 0 ? enTens[t] : enTens[t] + " " + enOnes[o];
    }

    /// <summary>The key of the voice hotkey ("Ctrl+Alt+G" → G) – held, it keeps listening.</summary>
    private int VoiceKey()
    {
        var combo = S.Hotkeys.GetValueOrDefault(nameof(Core.HotkeyAction.VoiceCommand)) ?? "Ctrl+Alt+G";
        var last = combo.Split('+').Last().Trim();
        return Enum.TryParse<Key>(last, true, out var k) ? KeyInterop.VirtualKeyFromKey(k) : 0x47;
    }

    private void RunSpoken(string said)
    {
        if (said.Length == 0) { ShowHint("mic.slash", L.T("Nicht verstanden"), (Brush)FindResource("SubText"), key: "voice"); return; }
        var q = Normalize(said);
        Action direct = null;
        string label = null;
        if (Regex.IsMatch(q, @"^(nächste[rsn]? (titel|lied|song)|weiter|skip|next( song| track)?)$")) { direct = H.Media.Next; label = L.T("Nächster Titel"); }
        else if (Regex.IsMatch(q, @"^(vorherige[rsn]? (titel|lied|song)|zurück|back|previous( song| track)?)$")) { direct = H.Media.Previous; label = L.T("Vorheriger Titel"); }
        else if (Regex.IsMatch(q, @"^(pause|stopp?|musik (aus|stopp?)|music off|abspielen|play|musik an|music on|weiter abspielen)$")) { direct = H.Media.PlayPause; label = L.T("Wiedergabe / Pause"); }
        else if (Regex.IsMatch(q, @"^(fokus starten|start focus)$")) { direct = () => { Expand(L.T("Timer"), true); SetFocusMode(true); }; label = L.T("Fokus"); }
        else if (Regex.IsMatch(q, @"^(welche[rs]? (song|lied) (ist das|läuft( gerade)?)|(song|lied) erkennen|wh(at|ich) song is (this|playing)|recogni[sz]e( the)? song|shazam)$")) { direct = RecognizeSong; label = L.T("Song erkennen"); }
        if (direct != null)
        {
            ShowHint("check", label, (Brush)FindResource("Green"), check: true, key: "voice");
            direct();
            return;
        }
        q = Regex.Replace(q, @"^(öffne|starte|zeig mir|zeige|zeig|open|start|show)\s+", "");
        if (Regex.Match(q, @"^(?:timer\s*)?(\d+)\s*(sekunden|seconds|stunden|stunde|hours|hour)$") is { Success: true } m)
        {
            int n = int.Parse(m.Groups[1].Value);
            int secs = m.Groups[2].Value.StartsWith("s") ? n : n * 3600;
            ShowHint("check", L.T("Timer läuft"), (Brush)FindResource("Green"), check: true, key: "voice");
            H.Timer.Reset(); H.Timer.SetDurationSeconds(secs); H.Timer.Start(); UpdatePill(true);
            return;
        }
        var hits = Find(q);
        if (hits.Count == 0) { ShowHint("xmark", L.T($"Nicht verstanden: „{said}“"), (Brush)FindResource("SubText"), key: "voice"); return; }
        var hit = hits[0];
        ShowHint("check", hit.Text, (Brush)FindResource("Green"), check: true, key: "voice");
        After(450, hit.Run);
    }

    /// <summary>"Timer fünfundzwanzig Minuten." → "timer 25 min"</summary>
    private static string Normalize(string s)
    {
        s = s.ToLowerInvariant().Trim().TrimEnd('.', '!', '?');
        foreach (bool de in new[] { true, false }) // German and English words, whatever the island's language
            foreach (int n in new[] { 120 }.Concat(Enumerable.Range(1, 99).Reverse())) // longest words first ("fünfundzwanzig" before "fünf")
            {
                var w = NumberWord(n, de);
                if (w.Length > 0) s = Regex.Replace(s, $@"(?<!\w){Regex.Escape(w)}(?!\w)", n.ToString());
            }
        s = Regex.Replace(s, @"\b(eine|einen|ein|one|a)\b(?=\s+(minute|stunde|hour))", "1");
        s = Regex.Replace(s, @"\b(minuten|minute|minutes)\b", "min");
        s = Regex.Replace(s, @"^(stell(e)? (einen |den )?timer auf|starte (einen |den )?timer mit|set a timer for|start a timer for|timer auf)\s*", "timer ");
        return Regex.Replace(s, @"\s+", " ").Trim();
    }
}
