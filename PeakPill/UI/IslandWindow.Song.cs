using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PeakPill.Core;
using PeakPill.Services;

namespace PeakPill.UI;

/// <summary>
/// 22 "Welcher Song ist das?" (user wish 2026-11-26): the island listens to what the PC plays – every app together, no
/// microphone – and asks Shazam (the user chose the unofficial way, see <see cref="SongRecognizer"/>). Start it from the
/// control center (the wave button: the view opens and listens right away), with Ctrl+Alt+L or by voice. A first try
/// after 5 s, more after 9, 13 and 17 s while it keeps listening. Found: cover, title, artist, "In Spotify"; the last songs
/// stay in the view.
/// </summary>
public partial class IslandWindow
{
    private CancellationTokenSource _songCts;
    private SongRecognizer.Listener _songEar;
    private ScaleTransform[] _songHintBars, _songViewBars;
    private DispatcherTimer _earTimer;
    private bool _songSearching;
    private double _songLevel;
    private static readonly HttpClient CoverHttp = new() { Timeout = TimeSpan.FromSeconds(8) };

#if DEBUG
    /// <summary>Render test: a made-up song instead of the PC sound and Shazam.</summary>
    private Func<CancellationToken, Task<SongMatch>> _songFake;
    private ImageSource _songFakeCover;
#endif

    private void WireSong()
    {
        SongBtn.ToolTip = L.T("Welcher Song läuft?");
        SongHead.Text = L.T("Song erkennen");
        SongBack.Text = L.T("Zurück");
        SongBack.MouseEnter += (_, _) => SongBack.Foreground = Brushes.White;
        SongBack.MouseLeave += (_, _) => SongBack.SetResourceReference(TextBlock.ForegroundProperty, "SubText");
        SongBack.MouseLeftButtonUp += (_, e) => { e.Handled = true; ShowSongView(false); };
        SongOpen.Text = L.T("In Spotify ↗");
        SongOpen.MouseEnter += (_, _) => SongOpen.TextDecorations = TextDecorations.Underline;
        SongOpen.MouseLeave += (_, _) => SongOpen.TextDecorations = null;
        SongOpen.MouseLeftButtonUp += (_, e) => { e.Handled = true; if (SongOpen.Tag is SongSeen s) OpenSong(s.Title, s.Artist); };
        SongListen.ToolTip = L.T("Zuhören / abbrechen");
        _songViewBars = MakeBars(SongViewWave, 5, 17, 2.6, Brushes.White);
        _songHintBars = MakeBars(SongWave, 5, 13, 2, (Brush)FindResource("Blue"));
    }

    /// <summary>Small round bars (their height is set by <see cref="EarTick"/>).</summary>
    private static ScaleTransform[] MakeBars(StackPanel host, int n, double h, double w, Brush fill)
    {
        var s = new ScaleTransform[n];
        for (int i = 0; i < n; i++)
        {
            s[i] = new ScaleTransform(1, 0.3);
            host.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Width = w, Height = h, RadiusX = w / 2, RadiusY = w / 2, Fill = fill, Margin = new Thickness(i == 0 ? 0 : w * 0.8, 0, 0, 0),
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = s[i], VerticalAlignment = VerticalAlignment.Center,
            });
        }
        return s;
    }

    // =====================================================================
    //  the view in the control center
    // =====================================================================

    private void Song_Click(object sender, RoutedEventArgs e) => ShowSongView(true);

    private void ShowSongView(bool on)
    {
        if (on)
        {
            MixerView.Visibility = Visibility.Collapsed;
            StatusView.Visibility = Visibility.Collapsed;
            _statusOpen = false;
        }
        TabControls.Children[0].Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        TabControls.Children[1].Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        SongView.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (S.Animations != AnimationLevel.Aus)
            TabControls.BeginAnimation(OpacityProperty, new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        if (!on) return;
        if (_songCts != null) { SongPhase(_songSearching ? L.T("Sucht …") : L.T("Hört zu …"), L.T("Ton aller Apps – kein Mikrofon"), _songSearching); BuildSongRows(0, true); return; }
        SongIdle();
        BuildSongRows(0, true);
        RecognizeSong(); // the button in the control center means "listen now" – no second click
    }

    private bool SongInView => _state == State.Expanded && SongView.IsVisible;

    private void SongListen_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        RecognizeSong();
    }

    private void SongIdle()
    {
        SongListen.Background = (Brush)FindResource("Blue");
        SongListenIcon.Kind = "waveform";
        SongListenIcon.Visibility = Visibility.Visible;
        SongViewWave.Visibility = Visibility.Collapsed;
        SongStatus.Text = L.T("Zum Zuhören tippen");
        SongSub.Text = L.T("Ton aller Apps – kein Mikrofon");
        SongOpen.Visibility = Visibility.Collapsed;
    }

    private void BuildSongRows(int skip, bool animate)
    {
        SongRows.Children.Clear();
        var grey = (Brush)FindResource("SubText");
        foreach (var s in S.SongHistory.Skip(skip).Take(2))
        {
            var seen = s;
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 3), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = L.T("In Spotify suchen") };
            var when = new TextBlock { Text = When(s.At), FontSize = 10.5, Foreground = grey, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            DockPanel.SetDock(when, Dock.Right);
            row.Children.Add(when);
            row.Children.Add(new SfIcon { Kind = "music", Size = 11, Foreground = grey, Width = 16, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            var text = new TextBlock { FontSize = 11, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            text.Inlines.Add(new System.Windows.Documents.Run(s.Title));
            if (!string.IsNullOrEmpty(s.Artist)) text.Inlines.Add(new System.Windows.Documents.Run(" · " + s.Artist) { Foreground = grey });
            row.Children.Add(text);
            row.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenSong(seen.Title, seen.Artist); };
            SongRows.Children.Add(row);
        }
        if (!animate || S.Animations == AnimationLevel.Aus) return;
        int i = 0;
        foreach (FrameworkElement r in SongRows.Children) RowIn(r, 120 + i++ * 90);
    }

    private static string When(DateTime at) =>
        at.Date == DateTime.Today ? at.ToString("HH:mm") : at.Date == DateTime.Today.AddDays(-1) ? L.T("gestern") : at.ToString("dd.MM.");

    // =====================================================================
    //  listening
    // =====================================================================

    /// <summary>Starts listening – or, while it listens, stops (the same button, hotkey or word again).</summary>
    private async void RecognizeSong()
    {
        if (_songCts != null) { _songCts.Cancel(); return; }
        var cts = _songCts = new CancellationTokenSource();
        if (!SongInView && _state == State.Expanded) { Collapse(); await Task.Delay(320); }
        SongPhase(L.T("Hört zu …"), L.T("Ton aller Apps – kein Mikrofon"), false);
        if (_earTimer == null)
        {
            _earTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _earTimer.Tick += (_, _) => EarTick();
        }
        _earTimer.Start();
        SongMatch match = null;
        string fail;
        try
        {
#if DEBUG
            if (_songFake != null) { match = await _songFake(cts.Token); fail = match == null ? L.T("Kein Song erkannt") : null; }
            else
#endif
            (match, fail) = await ListenAndAsk(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { fail = "cancel"; }
        catch (HttpRequestException ex) { App.Log(ex); fail = L.T("Shazam nicht erreichbar"); }
        catch (OperationCanceledException ex) { App.Log(ex); fail = L.T("Shazam nicht erreichbar"); } // the request timed out
        catch (Exception ex) { App.Log(ex); fail = L.T("Erkennen ging nicht"); }
        finally
        {
            _earTimer.Stop();
            _songEar = null;
            _songCts = null;
            _songSearching = false;
            cts.Dispose();
        }
        if (match != null) await SongFound(match);
        else SongFailed(fail);
    }

    private async Task<(SongMatch, string)> ListenAndAsk(CancellationToken ct)
    {
        using var ear = new SongRecognizer.Listener();
        _songEar = ear;
        var start = DateTime.Now;
        bool sound = false;
        string lang = L.English ? "en" : "de";
        foreach (int at in new[] { 5, 9, 13, 17 })
        {
            var wait = start.AddSeconds(at) - DateTime.Now;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            var samples = ear.Take(TimeSpan.FromSeconds(12));
            if (!SongRecognizer.HasSound(samples))
            {
                if (at >= 9 && !sound) return (null, L.T("Gerade läuft nichts"));
                continue;
            }
            sound = true;
            if (at == 17) SongPhase(L.T("Sucht …"), null, true);
            var m = await Task.Run(() => SongRecognizer.RecognizeAsync(samples, lang, ct), ct);
            if (m != null && !string.IsNullOrEmpty(m.Title)) return (m, null);
        }
        return (null, L.T("Kein Song erkannt"));
    }

    /// <summary>"Hört zu …" / "Sucht …": in the view, or as a hint in the closed island (bars instead of the symbol).</summary>
    private void SongPhase(string title, string sub, bool searching)
    {
        _songSearching = searching;
        if (SongInView)
        {
            SongListen.Background = (Brush)FindResource("Blue");
            SongListenIcon.Visibility = Visibility.Collapsed;
            SongViewWave.Visibility = Visibility.Visible;
            SongStatus.Text = title;
            if (sub != null) SongSub.Text = sub;
            SongOpen.Visibility = Visibility.Collapsed;
            return;
        }
        if (_state == State.Hint && SongWave.IsVisible) { HintText.Text = title; return; } // the hint is already there
        _hintHoldMs = 20000;
        ShowHint("waveform", title, (Brush)FindResource("Blue"), key: "song",
                 actions: new List<(string, string, Brush, Action)> { ("xmark", L.T("Abbrechen"), (Brush)FindResource("Danger"), () => _songCts?.Cancel()) });
        if (_state != State.Hint) return;
        HintIcon.Visibility = Visibility.Collapsed;
        SongWave.Visibility = Visibility.Visible;
    }

    /// <summary>The bars follow the PC sound; while Shazam is asked a wave runs through them.</summary>
    private void EarTick()
    {
        double t = Environment.TickCount64 / 1000.0;
        double raw = _songEar != null ? Math.Clamp(Math.Sqrt(_songEar.Level) * 2.2, 0, 1) : 0;
#if DEBUG
        if (_songFake != null) raw = 0.6 + 0.35 * Math.Sin(t * 4.1);
#endif
        _songLevel = raw > _songLevel ? raw : _songLevel * 0.8 + raw * 0.2;
        double[] shape = { 0.5, 0.85, 1, 0.8, 0.55 };
        foreach (var bars in new[] { _songHintBars, _songViewBars })
            for (int i = 0; i < bars.Length; i++)
            {
                double v = _songSearching
                    ? 0.3 + 0.6 * Math.Max(0, Math.Sin(t * 6 - i * 0.9))
                    : 0.3 + _songLevel * shape[i] * (0.7 + 0.3 * Math.Sin(t * (8 + i * 2.1) + i));
                bars[i].ScaleY = Math.Clamp(v, 0.3, 1);
            }
    }

    // =====================================================================
    //  the answer
    // =====================================================================

    private async Task SongFound(SongMatch m)
    {
        ImageSource cover;
        bool fake = false;
#if DEBUG
        fake = _songFake != null;
#endif
        cover = fake ? null : await LoadCover(m.CoverUrl);
#if DEBUG
        if (fake) cover = _songFakeCover;
#endif
        var seen = new SongSeen { Title = m.Title, Artist = m.Artist, Url = m.ShazamUrl, Cover = m.CoverUrl, At = DateTime.Now };
        S.SongHistory.RemoveAll(x => x.Title == seen.Title && x.Artist == seen.Artist);
        S.SongHistory.Insert(0, seen);
        if (S.SongHistory.Count > 30) S.SongHistory.RemoveRange(30, S.SongHistory.Count - 30);
        if (!fake) App.SaveSettings();

        if (SongInView)
        {
            SongViewWave.Visibility = Visibility.Collapsed;
            if (cover != null)
            {
                SongListen.Background = new ImageBrush(cover) { Stretch = Stretch.UniformToFill };
                SongListenIcon.Visibility = Visibility.Collapsed;
            }
            else
            {
                SongListenIcon.Kind = "music";
                SongListenIcon.Visibility = Visibility.Visible;
            }
            SongStatus.Text = m.Title;
            SongSub.Text = m.Artist ?? "";
            SongOpen.Tag = seen;
            SongOpen.Visibility = Visibility.Visible;
            BuildSongRows(1, true);
            if (S.Animations != AnimationLevel.Aus)
            {
                // the button becomes the cover with a little bounce
                var pop = new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(520)) { EasingFunction = new ElasticEase { Oscillations = 1, Springiness = 4 } };
                SongListenScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
                SongListenScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
                RowIn(SongStatus, 60);
                RowIn(SongSub, 140);
            }
            return;
        }

        var p = new PeekInfo
        {
            Key = "song", Icon = "music", GlyphBrush = (Brush)FindResource("Blue"),
            Source = L.T("Song erkannt"), SourceIcon = "waveform", SourceBrush = (Brush)FindResource("Blue"),
            Title = m.Title, Text = m.Artist ?? "", Image = cover, Cover = cover != null, TimeoutMs = 12000,
            Click = () => OpenSong(m.Title, m.Artist),
        };
        p.Action(L.T("In Spotify"), () => OpenSong(m.Title, m.Artist), true);
        p.Action(L.T("Kopieren"), () => CopyQuiet(string.IsNullOrEmpty(m.Artist) ? m.Title : $"{m.Artist} – {m.Title}"));
        ShowPeek(p);
    }

    private void SongFailed(string why)
    {
        bool cancelled = why == "cancel";
        if (SongInView)
        {
            SongIdle();
            if (!cancelled)
            {
                SongStatus.Text = why;
                SongSub.Text = L.T("Nochmal tippen");
                if (S.Animations != AnimationLevel.Aus)
                {
                    // a small "no" shake of the button
                    var shake = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(420) };
                    foreach (var (k, v) in new[] { (0.0, 0.0), (0.15, -5.0), (0.35, 4.0), (0.55, -3.0), (0.75, 1.5), (1.0, 0.0) })
                        shake.KeyFrames.Add(new EasingDoubleKeyFrame(v, KeyTime.FromPercent(k)));
                    var tt = new TranslateTransform();
                    SongListen.RenderTransform = new TransformGroup { Children = { SongListenScale, tt } };
                    tt.BeginAnimation(TranslateTransform.XProperty, shake);
                }
            }
            return;
        }
        if (cancelled)
        {
            if (_state == State.Hint && SongWave.IsVisible) ShowHint("xmark", L.T("Abgebrochen"), (Brush)FindResource("SubText"), key: "song");
            return;
        }
        ShowHint(why == L.T("Kein Song erkannt") ? "waveform" : "xmark", why, (Brush)FindResource("SubText"), key: "song");
    }

    private static async Task<ImageSource> LoadCover(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        try
        {
            var bytes = await CoverHttp.GetByteArrayAsync(url);
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.StreamSource = new MemoryStream(bytes);
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.DecodePixelWidth = 160;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    /// <summary>Spotify's own search (the app), or Spotify in the browser when it is not installed.</summary>
    private static void OpenSong(string title, string artist)
    {
        var q = Uri.EscapeDataString($"{title} {artist}".Trim());
        try { Process.Start(new ProcessStartInfo("spotify:search:" + q) { UseShellExecute = true }); }
        catch { Launch("https://open.spotify.com/search/" + q); }
    }
}
