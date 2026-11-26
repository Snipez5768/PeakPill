using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using PeakPill.Core;
using WF = System.Windows.Forms;

namespace PeakPill.UI;

public partial class SettingsWindow : Window
{
    private static SettingsWindow _instance;
    private bool _loading = true;
    private readonly Dictionary<HotkeyAction, (TextBox Box, TextBlock Status)> _keyRows = new();
    private static readonly int[] Limits = { 50, 100, 200, 500, 1000 };
    private static AppSettings S => App.Settings;

    public static void ShowSingle(string page = null)
    {
        if (_instance == null)
        {
            _instance = new SettingsWindow();
            _instance.Closed += (_, _) => _instance = null;
            _instance.Show();
        }
        if (_instance.WindowState == WindowState.Minimized) _instance.WindowState = WindowState.Normal;
        if (page != null) _instance.ShowPage(page);
        _instance.Activate();
    }

    public SettingsWindow()
    {
        InitializeComponent();
        L.TranslateTree(this);
        WireGuides(this);
        SwitchSpring.Attach(this); // switches spring over (2026-11-17)
        Loaded += (_, _) => L.TranslateTree(this);
        Icon = IconFactory.CreateWindowIcon();
        VersionText.Text = L.T("Version ") + (typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.1.0");
        DataDirText.Text = Storage.Dir;
        SourceInitialized += (_, _) =>
        {
            Native.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
            if (Native.TryApplyBackdrop(this, 2))
            {
                // Mica shines through the sidebar like the translucent macOS sidebar
                Background = Brushes.Transparent;
                RootGrid.Background = Brushes.Transparent;
                Sidebar.Background = new SolidColorBrush(Color.FromArgb(0x59, 0x2B, 0x2B, 0x2D));
                PageScroll.Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x1E, 0x1E, 0x1E));
            }
        };
        BuildThemeOptions();
        BuildKeyRows();
        BuildWorldClocks();
        BuildDesktopWidgets();
        BuildPreviewChips();
        AnimGallery.Build(AnimMoves, AnimMoments);
        LoadValues();
        _loading = false;
        // the preview follows every look setting at once (form, style, tab bar, accent)
        Action refresh = () => Dispatcher.BeginInvoke(new Action(() => Preview.Refresh()));
        App.SettingsChanged += refresh;
        Closed += (_, _) => { App.SettingsChanged -= refresh; App.ApplyHotkeys(); App.Hub.Sounds.Stop(); };
    }

#if DEBUG
    /// <summary>Pictures of every page, every preview page and film strips of the animation cards (nothing is changed).</summary>
    public static async Task TestAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var anim = S.Animations;
        S.Animations = AnimationLevel.An;
        ShowSingle("Look");
        var w = _instance;
        w.Left = 40; w.Top = 40;
        await Task.Delay(1200);
        void Save(FrameworkElement e, string name, double? h = null)
        {
            var dpi = VisualTreeHelper.GetDpi(e);
            double ww = e.ActualWidth, hh = h ?? e.ActualHeight;
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(ww * dpi.DpiScaleX), (int)(hh * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            var bg = new DrawingVisual();
            using (var dc = bg.RenderOpen()) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)), null, new Rect(0, 0, ww, hh));
            rtb.Render(bg);
            rtb.Render(e);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var fs = File.Create(System.IO.Path.Combine(dir, name + ".png"));
            enc.Save(fs);
        }
        // the preview through all pages
        var card = (FrameworkElement)w.Preview.Parent;
        foreach (var (key, _) in IslandPreview.Views)
        {
            w.Preview.View = key;
            await Task.Delay(800);
            Save((FrameworkElement)card.Parent, "preview_" + key);
        }
        w.Preview.View = "music";
        await Task.Delay(700);
        foreach (var (tag, page) in new (string, FrameworkElement)[] { ("Look", w.PageLook), ("General", w.PageGeneral), ("Anim", w.PageAnim), ("Clip", w.PageClip), ("Shot", w.PageShot), ("Widgets", w.PageWidgets), ("About", w.PageAbout), ("Keys", w.PageKeys) })
        {
            w.ShowPage(tag);
            await Task.Delay(tag == "Anim" ? 300 : 600);
            Save(page, "page_" + tag);
        }
        // film strips: each animation card, 10 frames
        w.ShowPage("Anim");
        await Task.Delay(6000); // the first visit plays all once – let it finish
        foreach (var grid in new[] { w.AnimMoves, w.AnimMoments })
            foreach (Border c in grid.Children)
            {
                var frames = new List<System.Windows.Media.Imaging.BitmapSource>();
                c.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                for (int i = 0; i < 10; i++)
                {
                    var dpi = VisualTreeHelper.GetDpi(c);
                    var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(c.ActualWidth * dpi.DpiScaleX), (int)(110 * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                    // via a brush: rendering the card itself would carry its place in the grid along (off the picture)
                    var shot = new DrawingVisual();
                    using (var dc = shot.RenderOpen()) dc.DrawRectangle(new VisualBrush(c) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, c.ActualWidth, c.ActualHeight));
                    rtb.Render(shot); rtb.Freeze(); frames.Add(rtb);
                    await Task.Delay(200);
                }
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen()) for (int i = 0; i < frames.Count; i++) dc.DrawImage(frames[i], new Rect(i * c.ActualWidth, 0, c.ActualWidth, 110));
                var dpi2 = VisualTreeHelper.GetDpi(c);
                var all = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(frames.Count * c.ActualWidth * dpi2.DpiScaleX), (int)(110 * dpi2.DpiScaleY), dpi2.PixelsPerInchX, dpi2.PixelsPerInchY, PixelFormats.Pbgra32);
                all.Render(dv);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(all));
                var title = ((TextBlock)((StackPanel)((DockPanel)((StackPanel)c.Child).Children[1]).Children[1]).Children[0]).Text;
                using (var fs = File.Create(System.IO.Path.Combine(dir, "anim_" + string.Concat(title.Where(char.IsLetterOrDigit)) + ".png"))) enc.Save(fs);
                await Task.Delay(400);
            }
        S.Animations = anim;
        App.Quit();
    }
#endif

#if DEBUG
    /// <summary>
    /// Pictures of the whole window for the README's settings tour (only pages without personal data: no Info –
    /// the data folder holds the user name –, no Widgets – the weather place –, no Bildschirmfoto – the folder).
    /// Also writes where the clicked things sit, for the pointer in the animation.
    /// </summary>
    public static async Task TourAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        S.Animations = AnimationLevel.An;
        S.TabBar = TabBarStyle.Titel;
        S.TabBarAutoHide = false;
        ShowSingle("Look");
        var w = _instance;
        w.Width = 800; w.Height = 600; w.Left = 40; w.Top = 40;
        await Task.Delay(1500);
        var spots = new System.Text.StringBuilder();
        int n = 0;
        void Shot(string name)
        {
            var e = w.RootGrid;
            int pw = (int)e.ActualWidth, ph = (int)e.ActualHeight;
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)), null, new Rect(0, 0, pw, ph));
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2D)), null, new Rect(0, 0, w.Sidebar.ActualWidth, ph));
                dc.DrawRectangle(new VisualBrush(e) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, pw, ph));
            }
            rtb.Render(dv);
            var enc = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 82 };
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var fs = File.Create(System.IO.Path.Combine(dir, $"{n++:00}_{name}.jpg"));
            enc.Save(fs);
        }
        string Spot(FrameworkElement el)
        {
            var p = el.TranslatePoint(new Point(el.ActualWidth / 2, el.ActualHeight / 2), w.RootGrid);
            return $"{p.X:0},{p.Y:0}";
        }
        RadioButton Nav(string tag) => w.Nav.Children.OfType<RadioButton>().First(r => (string)r.Tag == tag);

        // Erscheinungsbild: the preview through a few pages (chips), then the style tiles
        Shot("look_music");
        foreach (var v in new[] { "banner", "timer", "clip" })
        {
            spots.AppendLine($"{n} chip {Spot(w._chips[v])}");
            w._chips[v].IsChecked = true;
            await Task.Delay(900);
            Shot("look_" + v);
        }
        foreach (var (tag, name) in new[] { ("Anim", "anim"), ("Clip", "clip"), ("General", "general"), ("Keys", "keys") })
        {
            spots.AppendLine($"{n} nav {Spot(Nav(tag))}");
            w.ShowPage(tag);
            await Task.Delay(tag == "Anim" ? 2600 : 900); // the gallery plays once on its first visit
            Shot(name);
        }
        spots.AppendLine($"{n} nav {Spot(Nav("Look"))}");
        File.WriteAllText(System.IO.Path.Combine(dir, "spots.txt"), spots.ToString());
        App.Quit();
    }
#endif

    // ---------- island preview: chips to click through the pages ----------

    private readonly Dictionary<string, RadioButton> _chips = new();

    private void BuildPreviewChips()
    {
        foreach (var (key, name) in IslandPreview.Views)
        {
            var rb = new RadioButton { Content = name, IsChecked = key == Preview.View };
            rb.SetResourceReference(StyleProperty, "Chip");
            var k = key;
            rb.Checked += (_, _) => Preview.View = k;
            _chips[key] = rb;
            PreviewChips.Children.Add(rb);
        }
        // a click on a tab symbol inside the preview moves the chip too
        Preview.ViewChanged += v => { if (_chips.TryGetValue(v, out var rb)) rb.IsChecked = true; };
    }

    // ---------- folded sections (connections, tab order) ----------

    private void Expand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton tb || FindName((string)tb.Tag) is not FrameworkElement panel) return;
        panel.Visibility = tb.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        // the last row's divider only shows while something is folded open below it
        if (panel == ObsDetails) ObsRow.BorderThickness = new Thickness(0, 0, 0, tb.IsChecked == true ? 0.5 : 0);
    }

    private void TabOrderRow_Click(object sender, MouseButtonEventArgs e)
    {
        bool open = TabOrderPanel.Visibility != Visibility.Visible;
        TabOrderPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        TabOrderChevron.Kind = open ? "chevron.up" : "chevron.down";
    }

    private void UpdateConnectionButtons()
    {
        DiscordExpand.Content = Services.DiscordService.Configured ? L.T("Details") : L.T("Einrichten");
        SpotifyExpand.Content = string.IsNullOrEmpty(S.SpotifyClientId) ? L.T("Einrichten") : L.T("Details");
    }

    // ---------- animations page ----------

    private void AnimLevel_Checked(object sender, RoutedEventArgs e)
    {
        var level = sender == AnimOff ? AnimationLevel.Aus : sender == AnimReduced ? AnimationLevel.Reduziert : AnimationLevel.An;
        AnimLevelDesc.Text = level switch
        {
            AnimationLevel.An => L.T("Alle Momente mit Licht, Ringen und Tropfen."),
            AnimationLevel.Reduziert => L.T("Ohne Lichtstreifen, Ringe und Tropfen."),
            _ => L.T("Nur Öffnen, Schließen und Seitenwechsel bewegen sich.")
        };
        if (_loading) return;
        S.Animations = level;
        App.SaveSettings();
        AnimGallery.Refresh();
    }

    private void PlayAll_Click(object sender, RoutedEventArgs e) => AnimGallery.PlayAll();

    private static List<string> Items(string text) =>
        text.Split(new[] { ',', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

    private void Feeds_LostFocus(object sender, RoutedEventArgs e)
    {
        S.SportLeague = string.IsNullOrWhiteSpace(OptSportLeague.Text) ? "bl1" : OptSportLeague.Text.Trim().ToLowerInvariant();
        S.SportTeam = OptSportTeam.Text.Trim();
        S.TwitchClientId = OptTwitchId.Text.Trim();
        if (OptTwitchSecret.Password.Length > 0 && OptTwitchSecret.Password != SecretMask) S.TwitchSecret = OptTwitchSecret.Password.Trim();
        S.TwitchChannels = Items(OptTwitchChannels.Text);
        S.YouTubeChannels = Items(OptYouTube.Text);
        if (OptDhlKey.Password.Length > 0 && OptDhlKey.Password != SecretMask) S.DhlKey = OptDhlKey.Password.Trim();
        S.Parcels = OptParcels.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        App.SaveSettings();
    }

    /// <summary>Shown in a password field when a secret is saved – the secret itself never comes back into the window.</summary>
    private const string SecretMask = "••••••••";

    private void MailUrl_LostFocus(object sender, RoutedEventArgs e)
    {
        var url = OptMailUrl.Text.Trim();
        if (url.Length > 0 && !url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;
        S.MailUrl = string.IsNullOrEmpty(url) ? "https://mail.google.com" : url;
        OptMailUrl.Text = S.MailUrl;
        App.SaveSettings();
    }

    private void LoadValues()
    {
        // the switch shows what Windows will really do (it can be turned off in the Task-Manager too)
        S.Autostart = Autostart.IsOn();
        OptAutostart.IsChecked = S.Autostart;
        OptHover.IsChecked = S.OpenOnHover;
        OptSmartTab.IsChecked = S.SmartTab;
        OptHoverDelay.Value = S.HoverDelayMs;
        HoverDelayText.Text = $"{S.HoverDelayMs} ms";
        OptSeconds.IsChecked = S.ShowSeconds;
        OptClock.IsChecked = S.ClockInIsland;
        OptSideBubbles.IsChecked = S.SideBubbles;
        ClockSecondsRow.Visibility = S.ClockInIsland ? Visibility.Visible : Visibility.Collapsed;
        OptTrackPeek.IsChecked = S.TrackPeek;
        OptLevel.IsChecked = S.LevelInIsland;
        OptEyes.IsChecked = S.IslandEyes;
        OptReplySend.IsChecked = S.ReplyAutoSend;
        OptScaleScreen.IsChecked = S.ScaleWithScreen;
        OptFollowScreen.IsChecked = S.FollowActiveScreen;
        OptFadeTyping.IsChecked = S.FadeWhileTyping;
        OptBottom.IsChecked = S.IslandBottom;
        OptEyeBreak.IsChecked = S.EyeBreak;
        OptMorning.IsChecked = S.MorningMoment;
        OptEvening.IsChecked = S.EveningMoment;
        OptScreenTime.IsChecked = S.ScreenTime;
        OptLyricLine.IsChecked = S.LyricLine;
        OptCopyHelpers.IsChecked = S.CopyHelpers;
        OptSelectionMenu.IsChecked = S.SelectionMenu;
        OptSavedFiles.IsChecked = S.SavedFiles;
        OptWishlist.IsChecked = S.WishlistSales;
        OptWeatherFx.IsChecked = S.WeatherFx;
        OptHolidays.IsChecked = S.Holidays;
        OptWater.IsChecked = S.Water;
        OptBirthdays.Text = string.Join("\n", S.Birthdays.Select(b => b.Replace("|", " ")));
        BuildAchievements();
        if (OptGameReminder.Items.Count == 0)
            foreach (var m in new[] { 0, 60, 90, 120, 180, 240 })
                OptGameReminder.Items.Add(new ComboBoxItem { Content = m == 0 ? L.T("Aus") : m % 60 == 0 ? L.T($"{m / 60} h") : $"{m / 60}:{m % 60:00} h", Tag = m });
        OptGameReminder.SelectedItem = OptGameReminder.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == S.GameReminderMinutes) ?? OptGameReminder.Items[3];
        if (OptEveningAt.Items.Count == 0)
            for (int m = 15 * 60; m <= 22 * 60; m += 30) OptEveningAt.Items.Add(new ComboBoxItem { Content = $"{m / 60}:{m % 60:00}", Tag = m });
        OptEveningAt.SelectedItem = OptEveningAt.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == S.EveningMinutes) ?? OptEveningAt.Items[6];
        OptEveningAt.IsEnabled = S.EveningMoment;
        OptIslandSize.Value = Math.Round(S.IslandSize * 100);
        IslandSizeText.Text = $"{Math.Round(S.IslandSize * 100)} %";
        OptDevice.IsChecked = S.DeviceInIsland;
        OptPower.IsChecked = S.PowerInIsland;
        FsBar.IsChecked = S.Fullscreen == FullscreenMode.StatsBar;
        FsHide.IsChecked = S.Fullscreen == FullscreenMode.Hide;
        FsNormal.IsChecked = S.Fullscreen == FullscreenMode.Normal;
        OptCodeMail.IsChecked = S.CodeMail;
        OptUpdateCheck.IsChecked = S.UpdateCheck;
        OptMailUrl.Text = S.MailUrl;
        OptSportLeague.Text = S.SportLeague;
        OptSportTeam.Text = S.SportTeam;
        OptTwitchId.Text = S.TwitchClientId;
        OptTwitchSecret.Password = string.IsNullOrEmpty(S.TwitchSecretProtected) ? "" : SecretMask;
        OptTwitchChannels.Text = string.Join(", ", S.TwitchChannels);
        OptYouTube.Text = string.Join(", ", S.YouTubeChannels);
        OptDhlKey.Password = string.IsNullOrEmpty(S.DhlKeyProtected) ? "" : SecretMask;
        LoadAi();
        LoadLanguage();
        OptParcels.Text = string.Join("\n", S.Parcels);
        ShapePill.IsChecked = S.Shape == IslandShape.Pill;
        ShapeNotch.IsChecked = S.Shape == IslandShape.Notch;
        OptGlow.IsChecked = S.MusicGlow;
        TabStyleA.IsChecked = S.TabBar == TabBarStyle.Leise;
        TabStyleB.IsChecked = S.TabBar == TabBarStyle.Gruppen;
        TabStyleC.IsChecked = S.TabBar == TabBarStyle.Titel;
        OptTabAutoHide.IsChecked = S.TabBarAutoHide;
        BuildTabOrder();
        LoadMinimal();
        OptLocalSend.IsChecked = S.LocalSendOn;
        OptLocalSendName.Text = S.LocalSendName;
        OptLocalSendName.Tag = Environment.MachineName; // the placeholder: what the others see without a name
        (S.Animations switch { AnimationLevel.Aus => AnimOff, AnimationLevel.Reduziert => AnimReduced, _ => AnimOn }).IsChecked = true;
        ClipLayoutA.IsChecked = S.ClipLayout == ClipLayout.Liste;
        ClipLayoutB.IsChecked = S.ClipLayout == ClipLayout.Kacheln;
        ClipLayoutC.IsChecked = S.ClipLayout == ClipLayout.Streifen;
        OptQueue.IsChecked = App.Hub.Clipboard.QueueMode;
        OptClickPastes.IsChecked = S.ClickPastes;
        OptConfirmCopy.IsChecked = S.ConfirmCopy;
        OptPersist.IsChecked = S.ClipboardPersist;
        OptLimit.SelectedIndex = Math.Max(0, Array.IndexOf(Limits, Limits.OrderBy(l => Math.Abs(l - S.ClipboardLimit)).First()));
        OptShotFolder.Text = S.ScreenshotFolder;
        OptShotAsk.IsChecked = S.ScreenshotAsk;
        OptShotAuto.IsChecked = S.ScreenshotAutoSave;
        OptCity.Text = S.WeatherCity;
        OptTimerRepeat.IsChecked = S.TimerSoundRepeat;
        FillTimerSounds();
        CityStatus.Text = string.IsNullOrEmpty(S.WeatherPlace) ? "" : "Aktuell: " + S.WeatherPlace;
        OptDiscordId.Text = S.DiscordClientId;
        OptDiscordStreamKey.Text = HotkeyGesture.Display(S.DiscordStreamKey);
        OptMicBlue.IsChecked = S.MicIndicatorBlue;
        OptSpotifyId.Text = S.SpotifyClientId;
        if (!string.IsNullOrEmpty(S.ObsPassword)) OptObsPassword.Password = "••••••••";
        void Refresh()
        {
            SpotifyStatus.Text = string.IsNullOrEmpty(S.SpotifyClientId) ? L.T("Nicht eingerichtet") : App.Hub.Spotify.Status;
            ObsSettingsStatus.Text = App.Hub.Obs.Connected ? L.T("Verbunden") : App.Hub.Obs.Status;
            UpdateConnectionButtons();
        }
        Refresh();
        App.Hub.Spotify.PropertyChanged += (_, _) => Dispatcher.BeginInvoke(new Action(Refresh));
        App.Hub.Obs.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(Services.ObsService.Connected) or nameof(Services.ObsService.Status)) Dispatcher.BeginInvoke(new Action(Refresh)); };
        if (!string.IsNullOrEmpty(S.DiscordSecret)) OptDiscordSecret.Password = "••••••••"; // placeholder – the real secret is never shown
        UpdateDiscordStatus();
        App.Hub.Discord.PropertyChanged += (_, _) => Dispatcher.BeginInvoke(new Action(UpdateDiscordStatus));
        UpdateClaudeStatus();
    }

    // ---------- Claude Code: hooks for the Agent page ----------

    private void UpdateClaudeStatus(string message = null)
    {
        var st = Services.ClaudeHooks.Check();
        int live = App.Hub.Agents.Sessions.Count(s => s.Id != null && !s.Id.StartsWith("demo"));
        ClaudeStatus.Text = st switch
        {
            Services.ClaudeHooks.State.Ready => live > 0 ? L.T($"Eingerichtet · {live} aktiv") : L.T("Eingerichtet"),
            Services.ClaudeHooks.State.Partly => L.T("Teilweise eingerichtet"),
            Services.ClaudeHooks.State.NoClaude => L.T("Claude Code nicht gefunden"),
            Services.ClaudeHooks.State.Broken => L.T("Einstellungen nicht lesbar"),
            _ => L.T("Nicht eingerichtet"),
        };
        ClaudeStatus.Foreground = st == Services.ClaudeHooks.State.Ready ? (Brush)FindResource("Green") : (Brush)FindResource("SubText");
        ClaudeInstallBtn.Visibility = st == Services.ClaudeHooks.State.Ready ? Visibility.Collapsed : Visibility.Visible;
        ClaudeRemoveBtn.Visibility = st is Services.ClaudeHooks.State.Ready or Services.ClaudeHooks.State.Partly ? Visibility.Visible : Visibility.Collapsed;
        ClaudeHint.Text = message ?? (st == Services.ClaudeHooks.State.Ready
            ? L.T("Neue Claude-Sitzungen erscheinen im Agent-Tab.")
            : st == Services.ClaudeHooks.State.NoClaude ? L.T("Erst Claude Code installieren und einmal starten.") : "");
    }

    private void ClaudeInstall_Click(object sender, RoutedEventArgs e)
    {
        var err = Services.ClaudeHooks.Install();
        UpdateClaudeStatus(err ?? L.T("Fertig – laufende Claude-Sitzungen einmal neu starten."));
    }

    private void ClaudeRemove_Click(object sender, RoutedEventArgs e)
    {
        var err = Services.ClaudeHooks.Remove();
        UpdateClaudeStatus(err ?? L.T("Entfernt – Claude meldet der Insel nichts mehr."));
    }

    private void ClaudeCopy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(Services.ClaudeHooks.Snippet()); ClaudeHint.Text = L.T("Kopiert – in settings.json unter die oberste Klammer einfügen."); }
        catch { }
    }

    /// <summary>A made-up task runs through the island for a few seconds – so you see where it shows up.</summary>
    private async void ClaudeTest_Click(object sender, RoutedEventArgs e)
    {
        var a = App.Hub.Agents;
        a.Demo(L.T("Beispiel: Wetter-Widget bauen"), L.T("Liest WeatherService.cs"), 1, 4, 12, Services.AgentState.Running, "demo-test", "PeakPill");
        await Task.Delay(2500);
        a.Demo(L.T("Beispiel: Wetter-Widget bauen"), L.T("Bearbeitet WeatherWidget.xaml"), 3, 4, 40, Services.AgentState.Running, "demo-test", "PeakPill");
        await Task.Delay(2500);
        a.Demo(L.T("Beispiel: Wetter-Widget bauen"), L.T("Fertig"), 4, 4, 55, Services.AgentState.Done, "demo-test", "PeakPill");
        await Task.Delay(3000);
        a.EndDemo();
    }

    // ---------- Discord ----------

    private void UpdateDiscordStatus()
    {
        var d = App.Hub.Discord;
        DiscordStatus.Text = !Services.DiscordService.Configured ? L.T("Nicht eingerichtet")
            : d.Connected ? L.T("Verbunden") + (d.InVoice ? (string.IsNullOrEmpty(d.Channel) ? L.T(" · im Anruf") : L.T($" · in {d.Channel}")) : "") + (d.Muted ? L.T(" · stumm") : "")
            : d.Status;
        DiscordStatus.Foreground = d.Connected ? (Brush)FindResource("Green") : (Brush)FindResource("SubText");
        UpdateConnectionButtons();
    }

    private void DiscordConnect_Click(object sender, RoutedEventArgs e)
    {
        var id = OptDiscordId.Text.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(id, @"^\d{15,22}$")) { DiscordStatus.Text = L.T("Die Client-ID besteht nur aus Ziffern"); return; }
        var secret = OptDiscordSecret.Password;
        if (secret != "••••••••")
        {
            if (string.IsNullOrWhiteSpace(secret)) { DiscordStatus.Text = L.T("Bitte das Client-Secret einfügen"); return; }
            S.DiscordSecret = Services.DiscordService.Protect(secret.Trim());
        }
        if (id != S.DiscordClientId) Services.DiscordService.ForgetToken();
        S.DiscordClientId = id;
        App.SaveSettings();
        OptDiscordSecret.Password = "••••••••";
        DiscordStatus.Text = L.T("Verbinde … bestätige gleich das Fenster in Discord");
        App.Hub.Discord.Restart();
    }

    private async void SpotifyConnect_Click(object sender, RoutedEventArgs e)
    {
        var id = OptSpotifyId.Text.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[0-9a-fA-F]{32}$")) { SpotifyStatus.Text = L.T("Die Client-ID hat 32 Zeichen (0-9, a-f)"); return; }
        if (id != S.SpotifyClientId) App.Hub.Spotify.SignOut();
        S.SpotifyClientId = id;
        App.SaveSettings();
        await App.Hub.Spotify.SignInAsync();
    }

    private void SpotifyDisconnect_Click(object sender, RoutedEventArgs e)
    {
        App.Hub.Spotify.SignOut();
        S.SpotifyClientId = "";
        App.SaveSettings();
        OptSpotifyId.Text = "";
    }

    private void ObsSave_Click(object sender, RoutedEventArgs e)
    {
        var pw = OptObsPassword.Password;
        if (pw != "••••••••") S.ObsPassword = string.IsNullOrEmpty(pw) ? "" : Services.DiscordService.Protect(pw);
        App.SaveSettings();
        OptObsPassword.Password = string.IsNullOrEmpty(S.ObsPassword) ? "" : "••••••••";
        App.Hub.Obs.Reconnect();
        ObsSettingsStatus.Text = L.T("OBS: verbinde …");
    }

    private void MicBlue_Click(object sender, RoutedEventArgs e)
    {
        S.MicIndicatorBlue = OptMicBlue.IsChecked == true;
        App.NotifySettingsChanged(); // the island repaints the mic in the new colour
    }

    /// <summary>Records the keybind the user also set in Discord (Backspace/Delete clears it).</summary>
    private void DiscordStreamKey_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        if (key is Key.Back or Key.Delete && mods == ModifierKeys.None)
        {
            S.DiscordStreamKey = "";
            App.SaveSettings();
            OptDiscordStreamKey.Text = "";
            return;
        }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.Escape) return;
        if (mods == ModifierKeys.None && !(key >= Key.F13 && key <= Key.F24))
        {
            OptDiscordStreamKey.Text = L.T("Mit Strg / Alt / Shift kombinieren");
            return;
        }
        var g = new HotkeyGesture(mods, key);
        S.DiscordStreamKey = g.ToStorage();
        App.SaveSettings();
        OptDiscordStreamKey.Text = g.ToDisplay();
    }

    private void DiscordDisconnect_Click(object sender, RoutedEventArgs e)
    {
        App.Hub.Discord.Stop();
        Services.DiscordService.ForgetToken();
        S.DiscordClientId = "";
        S.DiscordSecret = "";
        App.SaveSettings();
        OptDiscordId.Text = "";
        OptDiscordSecret.Password = "";
        UpdateDiscordStatus();
    }

    private void Link_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
        e.Handled = true;
    }

    public void ShowPage(string tag)
    {
        foreach (RadioButton rb in Nav.Children)
            if ((string)rb.Tag == tag) rb.IsChecked = true;
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageGeneral == null) return;
        var tag = (string)((RadioButton)sender).Tag;
        SnapLeavingPage(); // a picture of the old page – it slides away under the sidebar (SettingsWindow.Motion.cs)
        PageGeneral.Visibility = tag == "General" ? Visibility.Visible : Visibility.Collapsed;
        PageLook.Visibility = tag == "Look" ? Visibility.Visible : Visibility.Collapsed;
        PageAnim.Visibility = tag == "Anim" ? Visibility.Visible : Visibility.Collapsed;
        PageKeys.Visibility = tag == "Keys" ? Visibility.Visible : Visibility.Collapsed;
        PageClip.Visibility = tag == "Clip" ? Visibility.Visible : Visibility.Collapsed;
        PageShot.Visibility = tag == "Shot" ? Visibility.Visible : Visibility.Collapsed;
        PageWidgets.Visibility = tag == "Widgets" ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = tag == "About" ? Visibility.Visible : Visibility.Collapsed;
        PageAi.Visibility = tag == "Ai" ? Visibility.Visible : Visibility.Collapsed;
        PageScroll.ScrollToTop();
        SlideToPage(tag);
        if (tag == "Keys") RefreshKeyStatus(App.ApplyHotkeys());
        // the first time the page opens, every animation plays once, one after another
        if (tag == "Anim" && !_animSeen) { _animSeen = true; Dispatcher.BeginInvoke(new Action(AnimGallery.PlayAll), System.Windows.Threading.DispatcherPriority.Background); }
    }

    private bool _animSeen;

    // ---------------- simple options ----------------

    private void Birthdays_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.Birthdays = OptBirthdays.Text.Split('\n').Select(l => System.Text.RegularExpressions.Regex.Match(l.Trim(), @"^(.+?)\s+(\d{1,2})\.(\d{1,2})\.?$"))
            .Where(m => m.Success).Select(m => $"{m.Groups[1].Value.Trim()}|{int.Parse(m.Groups[2].Value):00}.{int.Parse(m.Groups[3].Value):00}").ToList();
        App.SaveSettings();
    }

    /// <summary>Every achievement: golden when reached (with the day), grey until then.</summary>
    private void BuildAchievements()
    {
        AchievementList.Children.Clear();
        foreach (var (title, text, when) in IslandWindow.AchievementList(S))
        {
            var gold = new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x0A));
            var chip = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 3, 10, 4), Margin = new Thickness(0, 0, 6, 6),
                                    Background = new SolidColorBrush(when != null ? Color.FromArgb(0x33, 0xFF, 0xD6, 0x0A) : Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
                                    ToolTip = when is { } t ? $"{text} · {t:dd.MM.yyyy}" : text };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new SfIcon { Kind = "trophy", Size = 11, Foreground = when != null ? gold : Brushes.Gray, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = title, FontSize = 11, Foreground = when != null ? Brushes.White : Brushes.Gray, VerticalAlignment = VerticalAlignment.Center });
            chip.Child = sp;
            AchievementList.Children.Add(chip);
        }
    }

    private void GameReminder_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || OptGameReminder.SelectedItem is not ComboBoxItem { Tag: int m }) return;
        S.GameReminderMinutes = m;
        App.SaveSettings();
    }

    private void EveningAt_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || OptEveningAt.SelectedItem is not ComboBoxItem { Tag: int m }) return;
        S.EveningMinutes = m;
        App.SaveSettings();
    }

    private void Opt_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.Autostart = OptAutostart.IsChecked == true;
        S.OpenOnHover = OptHover.IsChecked == true;
        S.SmartTab = OptSmartTab.IsChecked == true;
        App.Hub.Clipboard.RefreshHook(); // noticing Ctrl+V is only needed for the queue or for this
        S.ShowSeconds = OptSeconds.IsChecked == true;
        S.ClockInIsland = OptClock.IsChecked == true;
        S.SideBubbles = OptSideBubbles.IsChecked == true;
        ClockSecondsRow.Visibility = S.ClockInIsland ? Visibility.Visible : Visibility.Collapsed;
        S.TrackPeek = OptTrackPeek.IsChecked == true;
        S.LevelInIsland = OptLevel.IsChecked == true;
        S.IslandEyes = OptEyes.IsChecked == true;
        S.ReplyAutoSend = OptReplySend.IsChecked == true;
        S.ScaleWithScreen = OptScaleScreen.IsChecked == true;
        S.FollowActiveScreen = OptFollowScreen.IsChecked == true;
        S.FadeWhileTyping = OptFadeTyping.IsChecked == true;
        S.IslandBottom = OptBottom.IsChecked == true;
        S.EyeBreak = OptEyeBreak.IsChecked == true;
        S.MorningMoment = OptMorning.IsChecked == true;
        S.EveningMoment = OptEvening.IsChecked == true;
        S.ScreenTime = OptScreenTime.IsChecked == true;
        S.LyricLine = OptLyricLine.IsChecked == true;
        S.CopyHelpers = OptCopyHelpers.IsChecked == true;
        S.SelectionMenu = OptSelectionMenu.IsChecked == true;
        S.SavedFiles = OptSavedFiles.IsChecked == true;
        S.WishlistSales = OptWishlist.IsChecked == true;
        S.WeatherFx = OptWeatherFx.IsChecked == true;
        S.Holidays = OptHolidays.IsChecked == true;
        S.Water = OptWater.IsChecked == true;
        OptEveningAt.IsEnabled = S.EveningMoment;
        S.DeviceInIsland = OptDevice.IsChecked == true;
        S.PowerInIsland = OptPower.IsChecked == true;
        S.MusicGlow = OptGlow.IsChecked == true;
        S.TabBarAutoHide = OptTabAutoHide.IsChecked == true;
        S.ClickPastes = OptClickPastes.IsChecked == true;
        S.ConfirmCopy = OptConfirmCopy.IsChecked == true;
        S.ClipboardPersist = OptPersist.IsChecked == true;
        S.ScreenshotAsk = OptShotAsk.IsChecked == true;
        S.ScreenshotAutoSave = OptShotAuto.IsChecked == true;
        S.TimerSoundRepeat = OptTimerRepeat.IsChecked == true;
        S.CodeMail = OptCodeMail.IsChecked == true;
        S.UpdateCheck = OptUpdateCheck.IsChecked == true;
        if (sender == OptAutostart) Autostart.Set(S.Autostart);
        if (sender == OptQueue) App.Hub.Clipboard.QueueMode = OptQueue.IsChecked == true;
        App.NotifySettingsChanged();
    }

    private void HoverDelay_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (HoverDelayText == null) return;
        int v = (int)Math.Round(e.NewValue / 10) * 10;
        HoverDelayText.Text = $"{v} ms";
        if (_loading) return;
        S.HoverDelayMs = v;
        App.SaveSettings();
    }

    private void IslandSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IslandSizeText == null) return;
        IslandSizeText.Text = $"{Math.Round(e.NewValue)} %";
        if (_loading) return;
        S.IslandSize = Math.Round(e.NewValue) / 100.0;
        App.NotifySettingsChanged(); // the island resizes live while the slider moves
    }

    private void IslandMove_Click(object sender, RoutedEventArgs e) => App.Island.EnterMoveMode();

    private void ArrangeWidgets_Click(object sender, RoutedEventArgs e)
    {
        DesktopWidgetWindow.SetArranging(!DesktopWidgetWindow.Arranging);
        ArrangeBtn.Content = DesktopWidgetWindow.Arranging ? L.T("Fertig") : L.T("Anordnen");
    }

    private void IslandCenter_Click(object sender, RoutedEventArgs e) => App.Island.ResetPosition();
    private void WeatherTest_Click(object sender, RoutedEventArgs e) => App.Island.WeatherPreview(); // 48: see it without waiting for rain

    private void Fullscreen_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.Fullscreen = sender == FsHide ? FullscreenMode.Hide : sender == FsNormal ? FullscreenMode.Normal : FullscreenMode.StatsBar;
        App.NotifySettingsChanged();
    }

    private void Limit_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || OptLimit.SelectedIndex < 0) return;
        S.ClipboardLimit = Limits[OptLimit.SelectedIndex];
        App.SaveSettings();
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e) => App.Hub.Clipboard.ClearUnpinned();

    // ---------------- timer sound ----------------

    private const string PickId = "pick";

    private void FillTimerSounds()
    {
        bool wasLoading = _loading;
        _loading = true;
        var opts = App.Hub.Sounds.Options(S.TimerSound);
        opts.Add(new Services.SoundOption(PickId, L.T("Eigene Datei auswählen …")));
        OptTimerSound.ItemsSource = opts;
        OptTimerSound.SelectedItem = opts.FirstOrDefault(o => o.Id == S.TimerSound) ?? opts[0];
        _loading = wasLoading;
    }

    private void TimerSound_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || OptTimerSound.SelectedItem is not Services.SoundOption opt) return;
        if (opt.Id == PickId)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = L.T("Ton für den Timer auswählen"),
                Filter = "Audiodateien|*.wav;*.mp3;*.wma;*.m4a;*.aac|Alle Dateien|*.*"
            };
            if (dlg.ShowDialog(this) == true) S.TimerSound = "file:" + dlg.FileName;
            FillTimerSounds();
        }
        else S.TimerSound = opt.Id;
        App.SaveSettings();
        App.Hub.Sounds.Play(S.TimerSound, false); // play once so you hear what you picked
    }

    private void SoundPreview_Click(object sender, RoutedEventArgs e) => App.Hub.Sounds.Play(S.TimerSound, false);

    // ---------------- look ----------------

    private void Shape_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.Shape = ShapeNotch.IsChecked == true ? IslandShape.Notch : IslandShape.Pill;
        App.NotifySettingsChanged();
    }

    // ---------- tab order + hidden tabs (drag along the vertical axis, snaps into place) ----------

    public sealed class TabEntry : PeakPill.Core.ObservableObject
    {
        private bool _visible = true;
        public string Key { get; init; }
        public string Name { get; init; }
        public string Icon { get; init; }
        public string Note { get; init; }
        public bool Visible { get => _visible; set => Set(ref _visible, value); }
    }

    /// <summary>The built-in order with names and symbols (same as the island).</summary>
    public static readonly (string Key, string Name, string Icon, string Note)[] AllTabs =
    {
        ("Live", "Live", "dot.radiowaves", L.T("nur wenn etwas läuft")), ("Obs", "OBS", "record", L.T("nur wenn verbunden")),
        ("Music", L.T("Musik"), "music", ""), ("Clip", L.T("Zwischenablage"), "clipboard", ""), ("Shelf", L.T("Ablage"), "folder", ""), ("Send", "LocalSend", "share", L.T("Dateien mit Handy und PC teilen")), ("Notes", L.T("Notizen"), "square.pencil", L.T("Schnellnotizen")),
        ("Agent", "Agent", "sparkles", ""), ("Ai", L.T("KI"), "bubble", L.T("Chat mit deinem API-Schlüssel")), ("Games", L.T("Spiele"), "gamecontroller", ""), ("Notify", L.T("Mitteilungen"), "bell.badge", ""),
        (L.T("Timer"), L.T("Timer"), "timer", ""), ("Today", L.T("Heute"), "calendar", L.T("Wetter und Termine")), ("Remind", L.T("Erinnerungen"), "checklist", ""), ("Controls", L.T("Steuerung"), "sliders", "")
    };

    private System.Collections.ObjectModel.ObservableCollection<TabEntry> _tabEntries;
    private AxisReorder _tabReorder;

    private void BuildTabOrder()
    {
        var order = S.TabOrder.Where(k => AllTabs.Any(t => t.Key == k)).ToList();
        foreach (var t in AllTabs) if (!order.Contains(t.Key)) order.Add(t.Key);
        _tabEntries = new(order.Select(k => AllTabs.First(t => t.Key == k))
            .Select(t => new TabEntry { Key = t.Key, Name = t.Name, Icon = t.Icon, Note = t.Note, Visible = !S.HiddenTabs.Contains(t.Key) }));
        TabOrderList.ItemsSource = _tabEntries;
        _tabReorder ??= new AxisReorder(TabOrderList, horizontal: false, (from, to) =>
        {
            _tabEntries.Move(from, to);
            SaveTabOrder();
        });
    }

    private void SaveTabOrder()
    {
        S.TabOrder = _tabEntries.Select(t => t.Key).ToList();
        S.HiddenTabs = _tabEntries.Where(t => !t.Visible).Select(t => t.Key).ToList();
        App.NotifySettingsChanged();
    }

    private void LocalSend_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var ls = App.Hub.LocalSend;
        bool on = OptLocalSend.IsChecked == true, renamed = OptLocalSendName.Text.Trim() != S.LocalSendName;
        S.LocalSendName = OptLocalSendName.Text.Trim();
        if (on != S.LocalSendOn) { S.LocalSendOn = on; if (on) ls.Start(); else ls.Stop(); }
        else if (renamed && on) _ = ls.Announce(); // the others pick up the new name
        App.NotifySettingsChanged();
    }

    // ---------------- "So geht's": setup steps (Tag="Guide") stay folded until you want them ----------------

    private void WireGuides(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>().ToList())
        {
            if (child is TextBlock { Tag: "Guide" } guide && guide.Parent is Panel panel)
            {
                Brush accent;
                try { accent = new SolidColorBrush((Color)ColorConverter.ConvertFromString(S.Accent)); } catch { accent = Brushes.DodgerBlue; }
                string label = L.T("So geht's");
                var link = new TextBlock
                {
                    Text = label + "  ▾", FontSize = 11.5, Foreground = accent, Cursor = Cursors.Hand,
                    Margin = new Thickness(guide.Margin.Left, 3, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
                };
                guide.Visibility = Visibility.Collapsed;
                guide.Margin = new Thickness(guide.Margin.Left, 3, guide.Margin.Right, 2);
                link.MouseLeftButtonUp += (_, ev) =>
                {
                    ev.Handled = true;
                    bool open = guide.Visibility != Visibility.Visible;
                    guide.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                    link.Text = label + (open ? "  ▴" : "  ▾");
                };
                panel.Children.Insert(panel.Children.IndexOf(guide), link);
                continue;
            }
            WireGuides(child);
        }
    }

    // ---------------- Minimal-Modus: which tabs hide while empty ----------------

    private ToggleButton[] MinimalSwitches => new[] { OptMinClip, OptMinAi, OptMinAgent, OptMinSend, OptMinMusic, OptMinNotify };

    private void LoadMinimal()
    {
        OptMinimal.IsChecked = S.MinimalMode;
        foreach (var b in MinimalSwitches) b.IsChecked = S.MinimalTabs.Contains((string)b.Tag);
        ShowMinimalTabs();
    }

    private void ShowMinimalTabs()
    {
        bool on = OptMinimal.IsChecked == true;
        MinimalTabsPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        MinimalMasterRow.Style = (Style)FindResource(on ? "Row" : "LastRow");
    }

    private void Minimal_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.MinimalMode = OptMinimal.IsChecked == true;
        S.MinimalTabs = MinimalSwitches.Where(b => b.IsChecked == true).Select(b => (string)b.Tag).ToList();
        ShowMinimalTabs();
        App.NotifySettingsChanged();
    }

    private void TabVisible_Click(object sender, RoutedEventArgs e)
    {
        // keep at least one page
        if (_tabEntries.All(t => !t.Visible) && (sender as FrameworkElement)?.DataContext is TabEntry t0) t0.Visible = true;
        SaveTabOrder();
    }

    private void ClipLayout_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.ClipLayout = sender == ClipLayoutA ? ClipLayout.Liste : sender == ClipLayoutB ? ClipLayout.Kacheln : ClipLayout.Streifen;
        App.NotifySettingsChanged();
    }

    private void TabStyle_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.TabBar = sender == TabStyleB ? TabBarStyle.Gruppen : sender == TabStyleC ? TabBarStyle.Titel : TabBarStyle.Leise;
        App.NotifySettingsChanged();
    }

    private void BuildThemeOptions()
    {
        // only the styles we agreed on (2026-11-14): Schwarz (A), Tiefe (C) and Glas
        var themes = new (IslandTheme T, string Name)[]
        {
            (IslandTheme.Schwarz, L.T("Schwarz")),
            (IslandTheme.Tiefe, L.T("Tiefe")),
            (IslandTheme.Glas, L.T("Glas")),
        };
        foreach (var (t, name) in themes)
        {
            var saved = S.Theme;
            S.Theme = t;
            ThemeManager.Apply();
            var preview = StylePic.Make((Brush)Application.Current.Resources["IslandBackground"], (Brush)Application.Current.Resources["IslandBorder"]);
            S.Theme = saved;

            var rb = new RadioButton { Content = preview, GroupName = "Theme", Tag = name, IsChecked = S.Theme == t, Margin = new Thickness(0, 0, 10, 4) };
            rb.SetResourceReference(StyleProperty, "OptionRadio");
            var theme = t;
            rb.Checked += (_, _) => { if (_loading) return; S.Theme = theme; App.NotifySettingsChanged(); };
            ThemePanel.Children.Add(rb);
        }
        ThemeManager.Apply();

        foreach (var (name, hex) in ThemeManager.Accents)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var dot = new Ellipse { Width = 16, Height = 16, Fill = new SolidColorBrush(color) };
            var ring = new Ellipse { Width = 22, Height = 22, StrokeThickness = 2, Stroke = Brushes.Transparent };
            var g = new Grid { Width = 22, Height = 22, Background = Brushes.Transparent };
            g.Children.Add(ring);
            g.Children.Add(dot);
            var rb = new RadioButton
            {
                GroupName = "Accent",
                ToolTip = name,
                Content = g,
                Cursor = Cursors.Hand,
                Focusable = false,
                Margin = new Thickness(3, 0, 0, 0),
                Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
                    "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='RadioButton'><ContentPresenter/></ControlTemplate>")
            };
            rb.Checked += (_, _) => ring.Stroke = new SolidColorBrush(color);
            rb.Unchecked += (_, _) => ring.Stroke = Brushes.Transparent;
            rb.IsChecked = string.Equals(S.Accent, hex, StringComparison.OrdinalIgnoreCase);
            if (rb.IsChecked == true) ring.Stroke = new SolidColorBrush(color);
            var h = hex;
            rb.Checked += (_, _) => { if (_loading) return; S.Accent = h; App.NotifySettingsChanged(); };
            AccentPanel.Children.Add(rb);
        }
    }

    // ---------------- shortcuts ----------------

    private void BuildKeyRows()
    {
        var last = HotkeyInfo.All.Last();
        foreach (var def in HotkeyInfo.All)
        {
            var card = new Border();
            card.SetResourceReference(StyleProperty, def == last ? "LastRow" : "Row");
            card.Padding = new Thickness(12, 5, 6, 5);
            var dock = new DockPanel();

            var reset = new Button { Content = "", ToolTip = L.T("Auf Standard zurücksetzen") };
            reset.SetResourceReference(StyleProperty, "IconButton");
            reset.Content = null;
            Ext.SetIcon(reset, "refresh");
            Ext.SetHover(reset, (Brush)FindResource("Orange"));
            DockPanel.SetDock(reset, Dock.Right);
            var clear = new Button { Content = "", ToolTip = L.T("Kürzel entfernen") };
            clear.SetResourceReference(StyleProperty, "IconButton");
            clear.Content = null;
            Ext.SetIcon(clear, "xmark");
            Ext.SetHover(clear, (Brush)FindResource("Danger"));
            DockPanel.SetDock(clear, Dock.Right);

            var box = new TextBox
            {
                IsReadOnly = true,
                IsReadOnlyCaretVisible = false,
                Width = 170,
                Cursor = Cursors.Hand,
                Tag = L.T("Kürzel aufnehmen"),
                Margin = new Thickness(8, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            box.SetResourceReference(StyleProperty, "KeyBox");
            DockPanel.SetDock(box, Dock.Right);

            var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            labels.Children.Add(new TextBlock { Text = def.Label });
            var status = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
            labels.Children.Add(status);

            dock.Children.Add(reset);
            dock.Children.Add(clear);
            dock.Children.Add(box);
            dock.Children.Add(labels);
            card.Child = dock;
            KeysPanel.Children.Add(card);
            _keyRows[def.Action] = (box, status);

            var d = def;
            box.Text = HotkeyGesture.Display(S.Hotkeys.GetValueOrDefault(d.Action.ToString()));
            box.GotKeyboardFocus += (_, _) =>
            {
                App.Hub.Hotkeys.UnregisterAll(); // otherwise the existing shortcut fires instead of being recorded
                box.Text = L.T("Kombination drücken…");
            };
            box.LostKeyboardFocus += (_, _) =>
            {
                box.Text = HotkeyGesture.Display(S.Hotkeys.GetValueOrDefault(d.Action.ToString()));
                RefreshKeyStatus(App.ApplyHotkeys());
            };
            box.PreviewKeyDown += (_, e) => RecordKey(d, box, e);
            clear.Click += (_, _) => { S.Hotkeys[d.Action.ToString()] = ""; App.SaveSettings(); box.Text = ""; RefreshKeyStatus(App.ApplyHotkeys()); };
            reset.Click += (_, _) =>
            {
                RemoveDuplicates(d.Action, d.Default);
                S.Hotkeys[d.Action.ToString()] = d.Default;
                App.SaveSettings();
                box.Text = HotkeyGesture.Display(d.Default);
                RefreshKeyStatus(App.ApplyHotkeys());
            };
        }
    }

    private void RecordKey(HotkeyDef def, TextBox box, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.ImeProcessed) key = e.ImeProcessedKey;

        if (key == Key.Escape) { FocusSink(); return; }

        var mods = Keyboard.Modifiers;
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) mods |= ModifierKeys.Windows;

        if (key is Key.Back or Key.Delete && mods == ModifierKeys.None)
        {
            S.Hotkeys[def.Action.ToString()] = "";
            App.SaveSettings();
            FocusSink();
            return;
        }

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            var partial = new List<string>();
            if (mods.HasFlag(ModifierKeys.Windows)) partial.Add("Win");
            if (mods.HasFlag(ModifierKeys.Control)) partial.Add(L.T("Strg"));
            if (mods.HasFlag(ModifierKeys.Alt)) partial.Add("Alt");
            if (mods.HasFlag(ModifierKeys.Shift)) partial.Add("Shift");
            box.Text = string.Join(" + ", partial) + " + …";
            return;
        }

        bool fKey = key >= Key.F1 && key <= Key.F24;
        if (mods == ModifierKeys.None && !fKey)
        {
            box.Text = L.T("Bitte mit Win, Strg, Alt oder Shift kombinieren");
            return;
        }

        var g = new HotkeyGesture(mods, key);
        var stored = g.ToStorage();
        var moved = RemoveDuplicates(def.Action, stored);
        S.Hotkeys[def.Action.ToString()] = stored;
        App.SaveSettings();

        var notes = new List<string>();
        if (moved != null) notes.Add(L.T($"Vorher bei „{moved}“ – dort entfernt."));
        if (g.CollidesWithAltGr()) notes.Add(L.T("Achtung: entspricht AltGr + Taste und blockiert das Zeichen beim Tippen."));
        KeysStatus.Text = string.Join(" ", notes);
        FocusSink();
    }

    /// <summary>Clears the same combination from any other action; returns that action's label.</summary>
    private string RemoveDuplicates(HotkeyAction owner, string stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        string moved = null;
        foreach (var def in HotkeyInfo.All)
        {
            if (def.Action == owner) continue;
            if (string.Equals(S.Hotkeys.GetValueOrDefault(def.Action.ToString()), stored, StringComparison.OrdinalIgnoreCase))
            {
                S.Hotkeys[def.Action.ToString()] = "";
                _keyRows[def.Action].Box.Text = "";
                moved = def.Label;
            }
        }
        return moved;
    }

    private void FocusSink()
    {
        Keyboard.ClearFocus();
        FocusManager.SetFocusedElement(this, this);
        Focus();
    }

    private void RefreshKeyStatus(List<HotkeyAction> failed)
    {
        foreach (var (action, (box, status)) in _keyRows)
        {
            box.Text = box.IsKeyboardFocused ? box.Text : HotkeyGesture.Display(S.Hotkeys.GetValueOrDefault(action.ToString()));
            if (failed.Contains(action))
            {
                status.Text = L.T("Von Windows oder einer anderen App belegt – bitte eine andere Kombination wählen.");
                status.Foreground = (Brush)FindResource("Danger");
                status.Visibility = Visibility.Visible;
            }
            else status.Visibility = Visibility.Collapsed;
        }
    }

    private void ResetKeys_Click(object sender, RoutedEventArgs e)
    {
        foreach (var def in HotkeyInfo.All) S.Hotkeys[def.Action.ToString()] = def.Default;
        App.SaveSettings();
        KeysStatus.Text = L.T("Alle Kürzel zurückgesetzt.");
        RefreshKeyStatus(App.ApplyHotkeys());
    }

    // ---------------- screenshot ----------------

    private void BrowseShotFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new WF.FolderBrowserDialog { SelectedPath = S.ScreenshotFolder, Description = L.T("Speicherordner für Screenshots"), UseDescriptionForTitle = true };
        if (dlg.ShowDialog() != WF.DialogResult.OK) return;
        OptShotFolder.Text = dlg.SelectedPath;
        S.ScreenshotFolder = dlg.SelectedPath;
        App.SaveSettings();
    }

    private void ShotFolder_LostFocus(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(OptShotFolder.Text)) return;
        S.ScreenshotFolder = OptShotFolder.Text.Trim();
        App.SaveSettings();
    }

    // ---------------- widgets ----------------

    private void City_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SetCity_Click(sender, e);
    }

    private async void SetCity_Click(object sender, RoutedEventArgs e)
    {
        CityStatus.Text = L.T("Suche…");
        try
        {
            var place = await App.Hub.Weather.SetCityAsync(OptCity.Text);
            CityStatus.Text = string.IsNullOrWhiteSpace(OptCity.Text) ? L.T("Wetter deaktiviert.") :
                place == null ? L.T("Stadt nicht gefunden.") : "Aktuell: " + place;
        }
        catch (Exception ex)
        {
            CityStatus.Text = "Keine Verbindung: " + ex.Message;
        }
    }

    private void BuildWorldClocks()
    {
        var zones = TimeZoneInfo.GetSystemTimeZones()
            .OrderBy(z => z.BaseUtcOffset)
            .Select(z => new ZoneItem(z.Id, $"{Services.ClockModel.CityOf(z)}  ({FormatOffset(z.BaseUtcOffset)})"))
            .ToList();

        var combos = new List<ComboBox>();
        for (int i = 0; i < 3; i++)
        {
            var combo = new ComboBox
            {
                ItemsSource = zones,
                DisplayMemberPath = L.T("Name"),
                SelectedValuePath = "Id",
                Width = 260,
                SelectedValue = i < S.WorldClocks.Count ? S.WorldClocks[i] : null
            };
            combo.SetResourceReference(StyleProperty, "DarkCombo");
            combo.SelectionChanged += (_, _) =>
            {
                if (_loading) return;
                S.WorldClocks = combos.Select(c => c.SelectedValue as string).Where(v => v != null).ToList();
                App.SaveSettings();
                App.Hub.Clock.RebuildWorld();
            };
            combos.Add(combo);

            var row = new Border();
            row.SetResourceReference(StyleProperty, i == 2 ? "LastRow" : "Row");
            var dock = new DockPanel();
            DockPanel.SetDock(combo, Dock.Right);
            dock.Children.Add(combo);
            dock.Children.Add(new TextBlock { Text = L.T($"Uhr {i + 1}"), VerticalAlignment = VerticalAlignment.Center });
            row.Child = dock;
            WorldPanel.Children.Add(row);
        }
    }

    /// <summary>One switch per widget – on = pinned to the desktop.</summary>
    private void BuildDesktopWidgets()
    {
        var names = new Dictionary<string, string>
        {
            ["clock"] = "Uhr", ["world"] = "Weltzeit", ["weather"] = "Wetter", ["stats"] = "PC-Auslastung",
            ["battery"] = "Akku", ["timer"] = "Timer", ["todo"] = "To-do"
        };
        var defs = WidgetRegistry.All;
        for (int i = 0; i < defs.Length; i++)
        {
            var d = defs[i];
            var row = new Border();
            row.SetResourceReference(StyleProperty, i == defs.Length - 1 ? "LastRow" : "Row");
            var dock = new DockPanel();
            var sw = new ToggleButton { IsChecked = DesktopWidgetWindow.IsPinned(d.Kind) };
            sw.SetResourceReference(StyleProperty, "Switch");
            DockPanel.SetDock(sw, Dock.Right);
            sw.Click += (_, _) =>
            {
                if (sw.IsChecked == true)
                {
                    DesktopWidgetWindow.Pin(d.Kind);
                    if (d.Kind == "stats") App.Hub.Stats.WantGpu = true;
                }
                else DesktopWidgetWindow.Unpin(d.Kind);
            };
            var icon = new SfIcon { Kind = IconOf(d.Kind), Size = 15, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(icon, Dock.Left);
            dock.Children.Add(sw);
            dock.Children.Add(icon);
            dock.Children.Add(new TextBlock { Text = names.GetValueOrDefault(d.Kind, d.Title), VerticalAlignment = VerticalAlignment.Center });
            row.Child = dock;
            DesktopWidgetsPanel.Children.Add(row);
        }
    }

    private static string IconOf(string kind) => kind switch
    {
        "clock" => "clock", "world" => "globe", "weather" => "sun", "stats" => "cpu",
        "battery" => "battery", "timer" => "timer", _ => "check"
    };

    /// <summary>Time zone entry; ToString keeps the pop-up button showing the readable name.</summary>
    private sealed record ZoneItem(string Id, string Name)
    {
        public override string ToString() => Name;
    }

    private static string FormatOffset(TimeSpan o) => "UTC" + (o < TimeSpan.Zero ? "−" : "+") + o.ToString(@"hh\:mm");

    // ---------------- about ----------------

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        UpdateStatus.Text = L.T("Frage GitHub …");
        var rel = await App.Hub.Updates.CheckAsync(quiet: true);
        if (rel == null) { UpdateStatus.Text = L.T($"Du hast die neueste Version ({Services.UpdateService.Current.ToString(3)})."); return; }
        UpdateStatus.Text = L.T($"Version {rel.Version.ToString(3)} ist da – die Insel bietet sie zum Installieren an.");
        App.Hub.Updates.Announce(rel);
    }

    private void OpenDataDir_Click(object sender, RoutedEventArgs e) => Process.Start("explorer.exe", $"\"{Storage.Dir}\"");

    private void Quit_Click(object sender, RoutedEventArgs e) => App.Quit();
}
