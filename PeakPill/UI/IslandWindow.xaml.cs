using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Microsoft.Win32;
using PeakPill.Core;
using PeakPill.Services;
using Shape = System.Windows.Shapes.Shape;
using Ellipse = System.Windows.Shapes.Ellipse;
using WF = System.Windows.Forms;

namespace PeakPill.UI;

public partial class IslandWindow : Window
{
    private enum State { Collapsed, Hint, Peek, Expanded }
    private enum Act { Live, Agent, Obs, Steam, Timer, Music, MicMuted, Queue, Recording, Alarm, Device, Print }
    private bool _hotPulse;

    private static readonly string[] TabOrder = { "Live", "Obs", "Music", "Clip", "Shelf", "Send", "Notes", "Agent", "Ai", "Games", "Notify", L.T("Timer"), "Today", "Remind", "Controls" };
    // one fixed size for every tab – the island never jumps when switching tabs
    private const double ExpW = 380, ExpH = 128, PeekW = 340, PillH = 32, IdleW = 176, ExpRadius = 24;

    private State _state = State.Collapsed;
    private string _tab = "Music";
    private bool _sticky, _hiddenByUser, _captureHidden;
    private double _targetW, _targetH;
    private DateTime _cursorOutsideSince = DateTime.MaxValue;
    private PeekInfo _peek;
    private readonly List<PeekInfo> _peeks = new();
    private int _hintGen;
    private bool _hintHasActions;
    private readonly DispatcherTimer _hoverTimer = new();
    private readonly DispatcherTimer _watchTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly DispatcherTimer _peekTimer = new();
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly OutsideClickHook _outside = new();
    private ListCollectionView _clipView;
    private string _clipFilter = "All";
    private bool _eqRunning, _seeking;
    private Point _dragStart;
    private ShelfItem _dragItem;
    private bool _internalDrag;
    private int _wheelAcc;
    private bool _sliderTest;
    private string _recordingText;
    private bool _tucked, _stripShown;
    private readonly DispatcherTimer _nearTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };

    private static Hub H => App.Hub;
    private static AppSettings S => App.Settings;
    private double NotchExtra => S.Shape == IslandShape.Notch ? 22 : 0;
    public bool IsHiddenByUser => _tucked;

    private bool _queueOpen;
    /// <summary>The page's height – plus the room of the "Rückgängig" bar while it shows (2026-11-18).</summary>
    private double TabHeight(string tab) => TabHeightCore(tab) + (_undoRoom ? UndoRoomH : 0);

    private double TabHeightCore(string tab) => tab == "Clip" ? ClipHeight()
        : tab == "Ai" ? AiHeight()
        : tab == "Send" ? SendHeight()
        : tab == "Notes" ? NotesHeight()
        : tab == "Notify" ? NotifyHeight()
        : tab == "Music" && (_queueOpen || _lyricsOpen) ? 300
        : tab == "Live" && _livePick ? LivePickH
        : tab == "Live" && H.Privacy.InUse && AnyDl ? 152 // call + download: a little taller, nothing cut off
        : ExpH;

    /// <summary>A Steam or browser download runs.</summary>
    private static bool AnyDl => H.Steam.Downloading || H.Downloads.Downloading;
    private IDownload _lastDl;
    /// <summary>The download the island shows: Steam first, else the browser; after the end the one that just finished.</summary>
    private IDownload Dl => H.Steam.Downloading ? H.Steam : H.Downloads.Downloading ? H.Downloads : _lastDl ?? H.Steam;

    /// <summary>The Live page's download row binds "Steam.Name" … – this hands it whichever download runs.</summary>
    private sealed class DlHolder { public IDownload Steam { get; init; } }

    public IslandWindow()
    {
        InitializeComponent();
        L.TranslateTree(this); // the chosen language (2026-11-17) – again once loaded, for generated parts
        Loaded += (_, _) => L.TranslateTree(this);
        _activePanel = PillPanel;
        InitRoom();
        InitFlair(); // the animation round of 2026-11-17 (IslandWindow.Flair.cs)
        InitQol();   // and the one of 2026-11-18 (IslandWindow.Qol.cs)
        // game tiles: drag left/right only, they snap into place (user wish 2026-11-14)
        _gamesReorder = new AxisReorder(GamesList, horizontal: true, (from, to) => H.Launcher.Move(H.Launcher.Items[from], to));
        // banners that open something on click get a small × to just dismiss them
        PeekPanel.MouseEnter += (_, _) => PeekClose.Visibility = _peek?.Click != null ? Visibility.Visible : Visibility.Collapsed;
        PeekPanel.MouseLeave += (_, _) => PeekClose.Visibility = Visibility.Collapsed;
        DataContext = H;
        Icon = IconFactory.CreateWindowIcon();

        _clipView = new ListCollectionView(H.Clipboard.Items) { Filter = ClipFilterPredicate, CustomSort = new ClipSorter() };
        ClipList.ItemsSource = _clipView;
        ApplyClipLayout();
        H.Clipboard.Items.CollectionChanged += (_, _) => Dispatcher.BeginInvoke(new Action(RefreshClipView), DispatcherPriority.Background);
        H.Clipboard.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ClipboardService.QueueMode) or nameof(ClipboardService.QueueCount)) UpdateQueueUi();
            if (e.PropertyName == nameof(ClipboardService.QueueMode) && H.Clipboard.QueueMode) OfferRecentCopies();
        };
        H.Clipboard.Notice += msg => ShowHint("stack", msg, (Brush)FindResource("Accent"), key: "queue");
        H.Clipboard.Captured += OnCaptured;
        // moments for "open what just happened"
        H.Clipboard.Captured += (item, byUser) => { if (byUser || item.IsScreenshot) Note("Clip", item.IsScreenshot ? 60 : 45); };
        H.Clipboard.Pasted += () => Ui.Run(() => Note("Clip", 45));
        H.Media.TrackChanged += () => Ui.Run(() => { if (H.Media.IsPlaying) Note("Music", 45); });
        H.Agents.NeedsYou += _ => Note("Agent", 300);
        H.Notifications.Arrived += _ => Ui.Run(() => Note("Notify", 120));
        // Claude's own Windows notices ("fertig", "wartet") are no notifications here – they lead to the Agent page
        H.Notifications.AgentNotice += _ => Ui.Run(() => Note("Agent", 180));
        H.Shelf.Items.CollectionChanged += (_, _) => Ui.Run(UpdateShelfTab);
        // Minimal-Modus: tabs come back as soon as there is something on them
        H.Clipboard.Items.CollectionChanged += (_, _) => Ui.Run(UpdateMinimalTabs);
        H.LocalSend.Devices.CollectionChanged += (_, _) => Ui.Run(UpdateMinimalTabs);
        H.Agents.Sessions.CollectionChanged += (_, _) => Ui.Run(UpdateMinimalTabs);
        H.Media.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MediaService.HasMedia)) Ui.Run(UpdateMinimalTabs); };
        H.Privacy.Started += (_, _) => Ui.Run(() => Note("Live", 60));
        H.Timer.Completed += () => Ui.Run(() => Note(L.T("Timer"), 60));
        H.Reminders.Due += _ => Ui.Run(() => Note("Remind", 120));
        WireAi();
        WireWorkday();
        WireMusicMore();
        WireHelpers();
        WireSystem();
        WireSong();
        WireGames2();
        WireFun();
        WireSelectionMenu();
        ApplyBottom(); // at the top or at the bottom (55)
        WireSend();
        WireNotes();
        WireNotifyDetach();

        // timer wheels start at the last used duration
        var secs = H.Timer.DurationSeconds;
        HourWheel.Value = secs / 3600;
        MinuteWheel.Value = secs / 60 % 60;
        SecondWheel.Value = secs % 60;
        // quick picks under Start: set the wheels (Start then runs it)
        foreach (var m in new[] { 5, 10, 25 })
        {
            var t = new TextBlock { Text = L.T($"{m} Min"), FontSize = 11, Cursor = Cursors.Hand, Margin = new Thickness(m == 5 ? 0 : 12, 0, 0, 0), Background = Brushes.Transparent };
            t.SetResourceReference(TextBlock.ForegroundProperty, "SubText");
            t.MouseEnter += (_, _) => t.SetResourceReference(TextBlock.ForegroundProperty, "Orange");
            t.MouseLeave += (_, _) => t.SetResourceReference(TextBlock.ForegroundProperty, "SubText");
            int min = m;
            t.MouseLeftButtonUp += (_, e) => { e.Handled = true; HourWheel.Value = 0; MinuteWheel.Value = min; SecondWheel.Value = 0; };
            TimerPresets.Children.Add(t);
        }
        // Wecker: quick times (they only set the wheels, "Stellen" sets the alarm)
        foreach (var (h, m) in new[] { (6, 30), (7, 0), (8, 0) })
        {
            var t = new TextBlock { Text = $"{h}:{m:00}", FontSize = 11, Cursor = Cursors.Hand, Margin = new Thickness(h == 6 ? 0 : 12, 0, 0, 0), Background = Brushes.Transparent };
            t.SetResourceReference(TextBlock.ForegroundProperty, "SubText");
            t.MouseEnter += (_, _) => t.SetResourceReference(TextBlock.ForegroundProperty, "Orange");
            t.MouseLeave += (_, _) => t.SetResourceReference(TextBlock.ForegroundProperty, "SubText");
            int hh = h, mm = m;
            t.MouseLeftButtonUp += (_, e) => { e.Handled = true; HourWheel.Value = hh; MinuteWheel.Value = mm; UpdateTimerUi(); };
            AlarmPresets.Children.Add(t);
        }
        // turning a wheel on the Wecker side: "Aus" becomes "Stellen" again
        HourWheel.ValueCommitted += _ => { if (_alarmMode) UpdateTimerUi(); };
        MinuteWheel.ValueCommitted += _ => { if (_alarmMode) UpdateTimerUi(); };
        H.Alarm.PropertyChanged += (_, _) =>
        {
            if (_alarmMode && _tab == "Timer" && _state == State.Expanded) UpdateTimerUi();
            UpdatePill();
        };
        H.Alarm.Rang += OnAlarmRang;
        H.Updates.Available += OnUpdateAvailable;
        H.Updates.Start();
        InitDeviceMoments();

        H.Media.PropertyChanged += (_, _) => Ui.Run(() => { UpdateMusicUi(); UpdatePill(); UpdateGlow(); });
        H.Media.TrackChanged += OnTrackChanged;
        H.Timer.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(FocusTimerService.Active)) { UpdatePill(); UpdateTimerUi(); } };
        H.Timer.Completed += OnTimerCompleted;
        H.Audio.PropertyChanged += (_, _) => { UpdatePill(); UpdateControlsUi(); UpdateLiveUi(); };
        H.Privacy.PropertyChanged += (_, _) => { if (!WatchCallEnd()) UpdatePill(); UpdateLiveUi(); };
        H.Privacy.Started += OnLiveStarted;
        H.Agents.PropertyChanged += (_, _) =>
        {
            UpdatePill(); UpdateAgentUi(); UpdateTabBadges();
            // answered in Claude: the "needs your answer" banner goes away by itself
            if (!H.Agents.AnyWaiting)
            {
                _peeks.RemoveAll(p => p.Key is "agentwait" or "agentask");
                if (_peek?.Key is "agentwait" or "agentask") EndPeek();
            }
        };
        H.Agents.Finished += OnAgentFinished;
        H.Agents.NeedsYou += OnAgentNeedsYou;
        H.Agents.PermissionAsked += OnPermissionAsked;
        H.Agents.PermissionGone += _ =>
        {
            foreach (var k in _askOf.Where(x => !x.Value.Open).Select(x => x.Key).ToList()) _askOf.Remove(k);
            _peeks.RemoveAll(p => p.Key == "agentask" && !_askOf.ContainsKey(p));
            if (_peek?.Key == "agentask" && !_askOf.ContainsKey(_peek)) EndPeek();
        };
        H.Notifications.Arrived += OnNotificationArrived;
        H.Notifications.Items.CollectionChanged += (_, _) => UpdateNotifyUi();
        H.Prices.Fired += OnPriceAlarm;
        H.Prices.Items.CollectionChanged += (_, _) => UpdateNotifyUi();
        H.Suggestions.Suggested += OnSuggested;
        H.Discord.PropertyChanged += (_, _) => UpdateDiscordState();
        H.Discord.Members.CollectionChanged += (_, _) => UpdateLiveUi();
        H.Obs.PropertyChanged += (_, e) => { UpdateObsUi(); if (e.PropertyName is not nameof(ObsService.StreamTime) || !H.Obs.Recording) UpdatePill(); };
        H.Obs.Scenes.CollectionChanged += (_, _) => UpdateObsUi();
        H.Steam.PropertyChanged += (_, e) =>
        {
            if (H.Steam.Downloading) _steamLastProgress = H.Steam.Progress; // the ring keeps it for the "done" moment
            UpdatePill();
            if (e.PropertyName is nameof(SteamService.Downloading)) UpdateLiveUi();
        };
        H.Steam.Finished += OnSteamFinished;
        H.Downloads.PropertyChanged += (_, e) =>
        {
            if (H.Downloads.Downloading) { _lastDl = H.Downloads; if (!H.Steam.Downloading) _steamLastProgress = H.Downloads.HasTotal ? H.Downloads.Progress : 0.3; }
            UpdatePill();
            if (e.PropertyName is nameof(BrowserDownloadService.Downloading)) UpdateLiveUi();
        };
        H.Downloads.Finished += (name, path, size) => Ui.Run(() => OnFileDownloaded(name, path, size));
        H.Downloads.Stuck += OnDownloadStuck;
        H.Feeds.Moment += OnFeedMoment;
        H.Feeds.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(LiveFeeds.Match) && _tab == "Today") UpdateTodayUi(); };
        H.Room.CodeWanted += OnCodeWanted;
        H.Spotify.PropertyChanged += (_, _) => Ui.Run(UpdateMusicUi);
        H.Media.TrackChanged += () => Ui.Run(() => { if (_queueOpen && !_queueSkipping) _ = RefreshQueueNow(); if (IsSpotify && H.Spotify.Connected) _ = H.Spotify.TrackChangedAsync(); });
        H.Spotify.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(SpotifyService.Liked) or nameof(SpotifyService.Connected)) UpdateMusicExtras(); };
        H.Suggestions.Items.CollectionChanged += (_, _) => UpdateNotifyUi();
        H.Game.PropertyChanged += (_, _) => { H.Stats.WantGpu = H.Game.IsFullscreen || DesktopWidgetWindow.IsPinned("stats"); UpdatePill(true); };
        H.Radios.PropertyChanged += (_, _) => UpdateControlsUi();
        H.Brightness.PropertyChanged += (_, _) => UpdateControlsUi();
        H.Reminders.Due += OnReminderDue;
        H.Reminders.Items.CollectionChanged += (_, _) => RemindEmpty.Visibility = H.Reminders.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RemindEmpty.Visibility = H.Reminders.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        H.Shelf.Items.CollectionChanged += (_, _) => UpdateShelfUi();
        LiveApp.MicBrush = MicBrush;
        App.SettingsChanged += () => { LiveApp.MicBrush = MicBrush; ApplyBottom(); ApplyShape(); UpdatePill(true); UpdateLiveUi(); UpdateGlow(); ApplyTabStyle(); ApplyClipLayout(); UpdateAiUi(); };

        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer.Stop();
            // over the ring stack hovering only fans it out – a click opens (2026-11-18)
            if (IsCursorOverIsland(0) && !RingStack.IsMouseOver && (_state == State.Collapsed || _state == State.Hint && !_hintHasActions)) Expand(null, false);
        };
        _watchTimer.Tick += (_, _) => WatchCursor();
        _peekTimer.Tick += (_, _) =>
        {
            if (IsCursorOverIsland(4)) return;
            EndPeek();
        };
        _uiTimer.Tick += (_, _) => PeriodicUi();
        _barTimer.Tick += (_, _) => BarTick();
        _uiTimer.Start();
        _outside.OutsideClick += () => Dispatcher.BeginInvoke(new Action(() =>
        {
            // closes instantly – even while the opening animation is still running
            if (_state == State.Expanded) Collapse();
        }));

        Island.MouseEnter += (_, _) => { StartHover(); OpenTicker(); StopCardCountdown(); };
        Island.MouseLeave += (_, _) => { _hoverTimer.Stop(); StartCardCountdown(); };
        Island.MouseLeftButtonUp += (_, e) =>
        {
            if (_recordingText != null && _state == State.Collapsed) { GifRecorder.Stop(); e.Handled = true; return; }
            // a click on a notice closes it right away – the normal island comes back (its buttons still work)
            if (_state == State.Hint) { e.Handled = true; _hintGen++; EndHint(); return; }
            if (_state == State.Peek)
            {
                e.Handled = true;
                // a banner with something behind it (a chat, Claude) opens it; others just go away
                if (_peek?.Click is { } open) { EndPeek(); open(); } else DismissPeek();
                return;
            }
            if (_state == State.Collapsed) { Expand(null, true); e.Handled = true; }
        };
        // bubbles beside the island do nothing on hover (user wish 2026-11-17) – a click opens their page
        // (the call bubble: a click mutes you, as before)
        Bubble.MouseLeftButtonUp += (_, _) => BubbleClicked(Bubble);
        Bubble2.MouseLeftButtonUp += (_, _) => BubbleClicked(Bubble2);
        // the bubble rings turn only when StyleRing asks for it; the agent spinner only while it is on screen
        Calm.Loop(AgentSpinner, AgentSpin, RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever });
        Island.SizeChanged += (_, _) => UpdateClip();
        Island.DragEnter += Island_DragEnter;
        Island.DragOver += Island_DragOver;
        Island.Drop += Island_Drop;

        PreviewKeyDown += Window_PreviewKeyDown;
        Deactivated += (_, _) =>
        {
            if (_state == State.Expanded && _sticky && !IsCursorOverIsland(0)) Collapse();
        };

        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(new Action(Reposition));
        DpiChanged += (_, _) => Reposition();

        ApplyShape();
        if (!TabOrder.Contains(S.LastTab) || S.LastTab == "Live") S.LastTab = "Music"; // e.g. the removed "Widgets" tab
        ApplyTabStyle();
        SelectTab(S.LastTab, false, false);
        ExpandedPanel.MouseMove += (_, _) => UpdateHeaderFade();
        QueueToggle.IsChecked = H.Clipboard.QueueMode;
        UpdateQueueUi();
        UpdateMusicUi();
        UpdateShelfUi();
        RefreshClipView();
        TuckStrip.MouseLeftButtonUp += (_, e) => { e.Handled = true; Untuck(); };
        InitMoveMode();
        MusicArtist.Cursor = Cursors.Hand;
        MusicArtist.MouseLeftButtonUp += MusicSource_MouseUp;
        MusicProgress.ValueChanged += (_, _) => { if (_seeking) UpdateMusicPosition(); }; // live time while scrubbing
        Ext.SliderDragStarted += OnSliderDragStarted;
        Ext.SliderDragEnded += OnSliderDragEnded;
        _nearTimer.Tick += (_, _) => CheckNearStrip();
        Loaded += (_, _) =>
        {
            Reposition();
            UpdatePill(true);
            UpdateLiveUi();
            if (S.IslandTucked) Tuck();
        };
        Closed += (_, _) => _outside.Dispose();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Native.MakeToolWindow(new WindowInteropHelper(this).Handle);
    }

    private void StartHover()
    {
        if (!S.OpenOnHover || _moveMode) return;
        _hoverTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(10, S.HoverDelayMs));
        _hoverTimer.Start();
    }

    // =====================================================================
    //  State machine
    // =====================================================================

    public void Expand(string tab = null, bool activate = false)
    {
        _hiddenByUser = false;
        _hintGen++;
        string auto = null;
        if (tab == null && _state != State.Expanded)
        {
            tab = ContextTab();
            if (_autoTab == tab) auto = tab;
        }
        tab ??= _tab;
        // a page opened only because something just happened does not become "your" page – afterwards the island
        // goes back to the one you used (e.g. Claude done → Agent once, later Musik again)
        _autoOpening = auto != null;
        SelectTab(tab, auto == null, _state == State.Expanded);
        _autoOpening = false;
        if (auto != null) _autoShownAt = DateTime.Now;
        if (activate) _sticky = true;

        if (_state != State.Expanded)
        {
            bool fromPillCover = _state == State.Collapsed && LeadCover.IsVisible;
            DropEyesNow(); // no eyes inside the opening island, not for a frame
            _state = State.Expanded;
            _peekTimer.Stop();
            if (_peek != null) { _peeks.Insert(0, _peek); _peek = null; }
            ShowPanel(ExpandedPanel);
            AnimateSize(ExpW, TabHeight(_tab) + NotchExtra, ExpRadius, true);
            if (fromPillCover && _tab == "Music") StartCoverFlight(LeadCover, MusicCoverBox);
            UpdateBubble(null);
            OnTabShown();
            _outside.Install();
        }
        UpdateVisibility();
        UpdateShelfTab();
        _cursorOutsideSince = DateTime.MaxValue;
        _watchTimer.Start();
        UpdateHeaderFade(true);
        PlaceTabIndicator();

        if (activate)
        {
            Activate();
            Dispatcher.BeginInvoke(new Action(FocusForTab), DispatcherPriority.Input);
        }
    }

    public void Collapse()
    {
        if (_state == State.Collapsed) return;
        FlyHomeOnCollapse(); // the timer's digits / the download bar fly into the pill (needs the page still there)
        if (_state == State.Expanded)
        {
            _closedAt = DateTime.Now;
            _closedTab = _tab;
            // what opened this page has been seen now (≥ 1 s open) → used up; for a few seconds the same page still
            // comes back (closing rule), after that the usual page again
            if (_autoTab != null && _tab == _autoTab && DateTime.Now - _autoShownAt >= TimeSpan.FromSeconds(1)) ForgetMoment(_autoTab);
        }
        _sticky = false;
        _livePick = false; // a half-made screen choice is not kept
        _watchTimer.Stop();
        _outside.Uninstall();
        _hintGen++;
        if (_state == State.Expanded && _peeks.Count > 0) { _state = State.Collapsed; ShowNextPeek(); return; }
        bool fromBigCover = _state == State.Expanded && _tab == "Music" && MusicCoverBox.IsVisible && !_tucked; // tucking: it shrinks into the strip instead
        _state = State.Collapsed;
        _peek = null;
        ShowPanel(PillPanel);
        UpdatePill(true);
        if (fromBigCover && LeadCover.Visibility == Visibility.Visible) StartCoverFlight(MusicCoverBox, LeadCover);
        Keyboard.ClearFocus();
        ClipConfirm.BeginAnimation(OpacityProperty, null);
        ClipConfirm.Visibility = Visibility.Collapsed; // an open "delete everything?" question never survives closing
        ExitVolMode(); // next time the music page shows the progress again
        UpdateShelfTab();
    }

    /// <summary>
    /// Opening (click, hover, bubble) always shows the page you were on last – it never jumps to another page by
    /// itself. Only a hidden page (Live / OBS with nothing running) falls back to Music.
    /// </summary>
    private string ContextTab()
    {
        if (S.SmartTab && RecentTab() is { } recent) return recent;
        var t = string.IsNullOrEmpty(S.LastTab) || !TabOrder.Contains(S.LastTab) ? "Music" : S.LastTab;
        if (t == "Live" && !H.Privacy.InUse && !AnyDl || t == "Obs" && !H.Obs.Connected) t = "Music";
        // a page that was just emptied (everything deleted) is not worth opening again – something alive instead
        if (TabEmpty(t) && (S.SmartTab || t == "Shelf")) t = FallbackTab() ?? "Music"; // an empty Ablage has no icon at all

        if (IsTabHidden(t) || MinimalAway(t))
            t = VisibleTabButtons().Select(r => (string)r.Tag).FirstOrDefault(x => !MinimalAway(x)) ?? t;
        return t;
    }

    /// <summary>Zwischenablage / Mitteilungen with nothing in them (user wish 2026-11-14: don't land on an emptied page).</summary>
    private static bool TabEmpty(string tab) => tab switch
    {
        "Clip" => H.Clipboard.Items.Count == 0 && H.Clipboard.Queue.Count == 0,
        "Notify" => H.Notifications.Items.Count == 0 && H.Suggestions.Items.Count == 0,
        "Shelf" => H.Shelf.Items.Count == 0,
        _ => false
    };

    /// <summary>Instead of an empty page: music if something plays, else a call / download, else Spiele, else Timer.</summary>
    private static string FallbackTab()
    {
        foreach (var t in new[] { "Music", "Live", "Games", L.T("Timer") })
            if (TabAvailable(t) && !(t == "Games" && H.Launcher.Items.Count == 0)) return t;
        return null;
    }

    // ---------- "open what just happened" (user wish 2026-11-14): time-based, no formula, nothing learned ----------
    //  The most recent fresh event wins: a new song → Musik for a short while, just copied / pasted / a screenshot →
    //  Zwischenablage, Claude done → Agent … When nothing happened lately, an ongoing call brings you back to Live;
    //  otherwise the last used page as before. Switching away by hand cancels that event.

    private sealed record Moment(string Tab, DateTime At, TimeSpan Fresh);
    private readonly List<Moment> _moments = new();
    private string _autoTab;
    private bool _autoOpening;
    private DateTime _autoShownAt;
    /// <summary>Closing counts as a moment for this long (user: "a bit more than 5 s later → the usual page").</summary>
    private static readonly TimeSpan CloseMoment = TimeSpan.FromSeconds(5);

    private void Note(string tab, double seconds)
    {
        _moments.RemoveAll(m => m.Tab == tab || DateTime.Now - m.At > m.Fresh);
        _moments.Add(new Moment(tab, DateTime.Now, TimeSpan.FromSeconds(seconds)));
    }

    private DateTime _closedAt = DateTime.MinValue;
    private string _closedTab;

    private string RecentTab()
    {
        var now = DateTime.Now;
        _moments.RemoveAll(m => now - m.At > m.Fresh);
        // closing counts as a moment too: left it for a few seconds (e.g. pasted something) → the same page again –
        // unless something newer happened since then (a notification that just arrived wins)
        var all = _moments.ToList();
        if (_closedTab != null && now - _closedAt < CloseMoment) all.Add(new Moment(_closedTab, _closedAt, CloseMoment));
        foreach (var m in all.OrderByDescending(m => m.At))
            if (TabAvailable(m.Tab) && !TabEmpty(m.Tab)) return _autoTab = m.Tab;
        // closed for a minute or longer (user wish 2026-11-17): the last page is forgotten – what runs right now wins
        if (now - _closedAt >= ForgetLastTab && RunningTab() is { } running) return _autoTab = running;
        // nothing fresh: whatever is still going on
        if (H.Agents.AnyWaiting) return _autoTab = "Agent";
        if (H.Privacy.InUse) return _autoTab = "Live";
        _autoTab = null;
        return null;
    }

    /// <summary>After this long closed the island no longer goes back to the last page by itself.</summary>
    private static readonly TimeSpan ForgetLastTab = TimeSpan.FromMinutes(1);

    /// <summary>
    /// What is going on right now, most important first: the KI (chat answering, Claude working or waiting) ranks
    /// above everything (user: "die KI hat eine höhere Priorität"), then a call, music that plays, a running timer,
    /// a download. Null when nothing runs – then the last page as before.
    /// </summary>
    private string RunningTab()
    {
        var order = new (bool On, string Tab)[]
        {
            (H.Ai.Busy, "Ai"),
            (H.Agents.AnyRunning || H.Agents.AnyWaiting, "Agent"),
            (H.Privacy.InUse, "Live"),
            (H.Media.IsPlaying, "Music"),
            (H.Timer.Running, L.T("Timer")),
            (AnyDl, "Live"),
        };
        foreach (var (on, tab) in order)
            if (on && TabAvailable(tab) && !MinimalAway(tab)) return tab;
        return null;
    }

    private static bool TabAvailable(string tab) => !IsTabHidden(tab) && tab switch
    {
        "Live" => H.Privacy.InUse || AnyDl,
        "Obs" => H.Obs.Connected,
        "Music" => H.Media.HasMedia,
        _ => true
    };

#if DEBUG
    /// <summary>The new banners, one picture each (sample texts, nothing real is opened).</summary>
    /// <summary>Film strips of the small moments: Claude done (light → check) and the timer end (rings + wiggle).</summary>
    public async Task MomentTestAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        H.Clipboard.Persist = false;
        H.Agents.ForTest();
        if (_tucked) Untuck();
        var frames = new List<System.Windows.Media.Imaging.BitmapSource>();
        void Frame()
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(ActualWidth * dpi.DpiScaleX), (int)(80 * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            var bg = new DrawingVisual();
            using (var dc = bg.RenderOpen()) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x3D, 0x4A, 0x63)), null, new Rect(0, 0, ActualWidth, 80));
            rtb.Render(bg); rtb.Render(Root); rtb.Freeze(); frames.Add(rtb);
        }
        void SaveStrip(string name)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) for (int i = 0; i < frames.Count; i++) dc.DrawImage(frames[i], new Rect(0, i * 80, ActualWidth, 80));
            var dpi = VisualTreeHelper.GetDpi(this);
            var all = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(ActualWidth * dpi.DpiScaleX), (int)(frames.Count * 80 * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            all.Render(dv);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(all));
            using var fs = File.Create(Path.Combine(dir, name + ".png"));
            enc.Save(fs);
            frames.Clear();
        }
        var anim = S.Animations;
        S.Animations = AnimationLevel.An;
        await Task.Delay(1500);
        // a notification: equalizer steps aside, the bell rings, then the island opens downwards
        OnNotificationArrived(new IslandNotification { Id = 0, AppName = "WhatsApp", Title = "Anna", Text = L.T("Kommst du heute noch vorbei?"), Time = DateTime.Now });
        for (int i = 0; i < 16; i++) { Frame(); await Task.Delay(75); }
        SaveStrip("00_notification");
        if (Environment.GetEnvironmentVariable("ISLAND_NOTIF_ONLY") == "1") { S.Animations = anim; App.Quit(); return; }
        await Task.Delay(5000);
        // Claude working (bubble next to the music) → done
        H.Agents.Demo(L.T("Spotify-Lautstärke einbauen"), "Bearbeitet IslandWindow.xaml.cs", 3, 5, 600, AgentState.Running);
        // the bubble is born: symbol small in the island → drop → island keeps the group centred
        for (int i = 0; i < 20; i++) { Frame(); await Task.Delay(60); }
        SaveStrip("0_bubble_birth");
        if (Environment.GetEnvironmentVariable("ISLAND_BIRTH_ONLY") == "1") { S.Animations = anim; App.Quit(); return; }
        var s = H.Agents.Sessions.FirstOrDefault();
        if (s != null) { s.State = AgentState.Done; s.FinishedAt = DateTime.Now; s.StartedAt = DateTime.Now.AddMinutes(-10); OnAgentFinished(s); }
        for (int i = 0; i < 10; i++) { Frame(); await Task.Delay(130); }
        SaveStrip("1_claude_done");
        await Task.Delay(5000);
        // timer end: rings + wiggle (the notice itself is not needed here)
        Ripples(((SolidColorBrush)FindResource("Orange")).Color);
        Shake();
        for (int i = 0; i < 6; i++) { Frame(); await Task.Delay(120); }
        SaveStrip("2_timer");
        await Task.Delay(1000);

        // copied: squash + the check slides in
        OnCaptured(new ClipItem { Kind = ClipKind.Text, Text = L.T("Test") }, true);
        for (int i = 0; i < 5; i++) { Frame(); await Task.Delay(80); }
        SaveStrip("3_copied");
        await Task.Delay(2500);

        // muted: dark red wash
        ShowHint("mic.slash", "Stumm", (Brush)FindResource("Danger"), key: "mic");
        MuteMoment();
        for (int i = 0; i < 5; i++) { Frame(); await Task.Delay(180); }
        SaveStrip("4_muted");
        await Task.Delay(2500);

        // new song: covers swap
        if (PillCover.ImageSource is { } cur)
        {
            CoverSwap(cur);
            Tint(Color.FromRgb(0x3B, 0x6E, 0xA8), 0.28);
            for (int i = 0; i < 6; i++) { Frame(); await Task.Delay(90); }
            SaveStrip("5_song");
        }
        await Task.Delay(1200);

        // screenshot: island opens downwards with the picture, then takes it in
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) { dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x3B, 0x6E, 0xA8)), null, new Rect(0, 0, 320, 180)); dc.DrawRoundedRectangle(Brushes.LightSkyBlue, null, new Rect(40, 40, 140, 60), 8, 8); }
        var shotImg = new System.Windows.Media.Imaging.RenderTargetBitmap(320, 180, 96, 96, PixelFormats.Pbgra32);
        shotImg.Render(dv); shotImg.Freeze();
        ShowPeek(new PeekInfo { Key = "shot", Shot = shotImg, TimeoutMs = 0 });
        for (int i = 0; i < 9; i++) { Frame(); await Task.Delay(190); }
        SaveStrip("6_screenshot");
        await Task.Delay(1500);

        // download done: light runs to the ring, ring turns green
        H.Steam.Demo("ELDEN RING", 0.82, "48 MB/s");
        await Task.Delay(1500);
        H.Steam.EndDemo();
        OnSteamFinished("ELDEN RING", "1245620");
        for (int i = 0; i < 10; i++) { Frame(); await Task.Delay(140); }
        SaveStrip("7_download");
        S.Animations = anim;
        App.Quit();
    }

    /// <summary>Song changes as players really send them: new title with the old picture, the new picture later; and a title without a new picture.</summary>
    public async Task SongTestAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        H.Clipboard.Persist = false;
        if (_tucked) Untuck();
        bool peek = S.TrackPeek;
        S.TrackPeek = false;
        var anim = S.Animations;
        S.Animations = AnimationLevel.An;
        System.Windows.Media.Imaging.BitmapSource Pic(Color a, Color b)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) dc.DrawRectangle(new LinearGradientBrush(a, b, 45), null, new Rect(0, 0, 64, 64));
            var r = new System.Windows.Media.Imaging.RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
            r.Render(dv); r.Freeze();
            return r;
        }
        var red = Pic(Color.FromRgb(0xFF, 0x9F, 0x0A), Color.FromRgb(0xFF, 0x37, 0x5F));
        var blue = Pic(Color.FromRgb(0x64, 0xD2, 0xFF), Color.FromRgb(0x30, 0xD1, 0x58));
        var frames = new List<System.Windows.Media.Imaging.BitmapSource>();
        void Frame()
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(ActualWidth * dpi.DpiScaleX), (int)(60 * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            var bg = new DrawingVisual();
            using (var dc = bg.RenderOpen()) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x3D, 0x4A, 0x63)), null, new Rect(0, 0, ActualWidth, 60));
            rtb.Render(bg); rtb.Render(Root); rtb.Freeze(); frames.Add(rtb);
        }
        void Save(string name)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) for (int i = 0; i < frames.Count; i++) dc.DrawImage(frames[i], new Rect(0, i * 60, ActualWidth, 60));
            var dpi = VisualTreeHelper.GetDpi(this);
            var all = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(ActualWidth * dpi.DpiScaleX), (int)(frames.Count * 60 * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            all.Render(dv);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(all));
            using var fs = File.Create(Path.Combine(dir, name + ".png"));
            enc.Save(fs);
            frames.Clear();
        }
        var log = new System.Text.StringBuilder();
        H.Media.Fake("Song A", L.T("Test"), red, true);
        await Task.Delay(1500);
        // 1) Spotify-like: new title with the OLD picture (a new object), the new one 300 ms later
        H.Media.Fake("Song B", L.T("Test"), Pic(Color.FromRgb(0xFF, 0x9F, 0x0A), Color.FromRgb(0xFF, 0x37, 0x5F)), true);
        log.AppendLine($"after title: pending={_songPending}");
        for (int i = 0; i < 4; i++) { Frame(); await Task.Delay(75); }
        H.Media.Fake("Song B", L.T("Test"), blue, false);
        for (int i = 0; i < 12; i++) { Frame(); await Task.Delay(75); }
        log.AppendLine($"after picture: pending={_songPending} cover is blue={ReferenceEquals(PillCover.ImageSource, blue)}");
        Save("1_title_then_picture");
        await Task.Delay(1500);
        // 2) browser-like: a new title, the picture never changes
        H.Media.Fake("Song C", L.T("Test"), blue, true);
        for (int i = 0; i < 18; i++) { Frame(); await Task.Delay(75); }
        log.AppendLine($"same picture: pending={_songPending}");
        Save("2_same_picture");
        File.WriteAllText(Path.Combine(dir, "log.txt"), log.ToString());
        S.TrackPeek = peek;
        S.Animations = anim;
        App.Quit();
    }

    /// <summary>Timer | Wecker: the milled switch in the page name (title line and quiet bar), switching, setting an alarm (cleared again).</summary>
    public async Task AlarmTestAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        H.Clipboard.Persist = false;
        if (_tucked) Untuck();
        var style = S.TabBar;
        bool autoHide = S.TabBarAutoHide;
        S.TabBarAutoHide = false;
        var hadAlarm = H.Alarm.At;
        void Snap(FrameworkElement e, string name, double scale = 1, double h = 0)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            double w = e.ActualWidth, hh = h > 0 ? h : e.ActualHeight, sx = dpi.DpiScaleX * scale, sy = dpi.DpiScaleY * scale;
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(w * sx), (int)(hh * sy), 96 * sx, 96 * sy, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x3D, 0x4A, 0x63)), null, new Rect(0, 0, w, hh));
                dc.DrawRectangle(new VisualBrush(e) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, w, e.ActualHeight));
            }
            rtb.Render(dv);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var fs = File.Create(Path.Combine(dir, name + ".png"));
            enc.Save(fs);
        }
        foreach (var st in new[] { TabBarStyle.Titel, TabBarStyle.Leise })
        {
            S.TabBar = st;
            ApplyTabStyle();
            SetAlarmMode(false);
            Expand(L.T("Timer"), true);
            _sticky = true;
            await Task.Delay(1200);
            Snap(Root, $"{st}_1_timer", 1, 190);
            Snap(TabHeaderBar, $"{st}_zoom_timer", 3);
            _timerSwitch.Second = true;
            SetAlarmMode(true);
            await Task.Delay(120);
            Snap(Root, $"{st}_2_switching", 1, 190);
            await Task.Delay(700);
            Snap(Root, $"{st}_3_wecker", 1, 190);
            Snap(TabHeaderBar, $"{st}_zoom_wecker", 3);
            HourWheel.Value = 7; MinuteWheel.Value = 30;
            TimerStart_Click(null, null);
            await Task.Delay(500);
            Snap(Root, $"{st}_4_gestellt", 1, 190);
            H.Alarm.Clear();
            _sticky = false;
            Collapse();
            await Task.Delay(800);
        }
        if (hadAlarm is { } a) H.Alarm.SetTime(a.Hour, a.Minute);
        S.TabBar = style;
        S.TabBarAutoHide = autoHide;
        App.Quit();
    }

    /// <summary>Skipping in the open Music page: Weiter (left → right) and Zurück (right → left), with fake songs – the real player is not touched.</summary>
    public async Task SkipTestAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        H.Clipboard.Persist = false;
        if (_tucked) Untuck();
        var anim = S.Animations;
        S.Animations = AnimationLevel.An;
        System.Windows.Media.Imaging.BitmapSource Pic(Color a, Color b)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) dc.DrawRectangle(new LinearGradientBrush(a, b, 45), null, new Rect(0, 0, 64, 64));
            var r = new System.Windows.Media.Imaging.RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
            r.Render(dv); r.Freeze();
            return r;
        }
        var frames = new List<System.Windows.Media.Imaging.BitmapSource>();
        void Frame()
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(ActualWidth * dpi.DpiScaleX), (int)(170 * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            var bg = new DrawingVisual();
            using (var dc = bg.RenderOpen()) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x3D, 0x4A, 0x63)), null, new Rect(0, 0, ActualWidth, 170));
            rtb.Render(bg); rtb.Render(Root); rtb.Freeze(); frames.Add(rtb);
        }
        void Save(string name)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) for (int i = 0; i < frames.Count; i++) dc.DrawImage(frames[i], new Rect(0, i * 170, ActualWidth, 170));
            var dpi = VisualTreeHelper.GetDpi(this);
            var all = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(ActualWidth * dpi.DpiScaleX), (int)(frames.Count * 170 * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            all.Render(dv);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(all));
            using var fs = File.Create(Path.Combine(dir, name + ".png"));
            enc.Save(fs);
            frames.Clear();
        }
        H.Media.Fake(L.T("Nachtlicht"), "Lena Ost", Pic(Color.FromRgb(0xFF, 0x9F, 0x0A), Color.FromRgb(0xFF, 0x37, 0x5F)), true);
        Expand("Music", true);
        _sticky = true;
        await Task.Delay(1500);
        MusicProgress.Value = 0.45;
        BeginSkip(+1);
        await Task.Delay(150);
        H.Media.Fake("Morgengrau", "Kai Feld", Pic(Color.FromRgb(0x64, 0xD2, 0xFF), Color.FromRgb(0x30, 0xD1, 0x58)), true);
        for (int i = 0; i < 12; i++) { Frame(); await Task.Delay(70); }
        Save("1_weiter");
        await Task.Delay(1200);
        if (_state != State.Expanded) { Expand("Music", true); await Task.Delay(900); }
        BeginSkip(-1);
        await Task.Delay(420);
        H.Media.Fake(L.T("Nachtlicht"), "Lena Ost", Pic(Color.FromRgb(0xFF, 0x9F, 0x0A), Color.FromRgb(0xFF, 0x37, 0x5F)), true);
        for (int i = 0; i < 16; i++) { Frame(); await Task.Delay(70); }
        Save("2_zurueck");
        S.Animations = anim;
        App.Quit();
    }

    /// <summary>Message: bell → ticker → (pointer) card → a second message; then "Claude hört zu".</summary>
    public async Task TickerTestAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        H.Clipboard.Persist = false;
        if (_tucked) Untuck();
        var anim = S.Animations;
        S.Animations = AnimationLevel.An;
        var frames = new List<System.Windows.Media.Imaging.BitmapSource>();
        void Frame()
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(ActualWidth * dpi.DpiScaleX), (int)(140 * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            var bg = new DrawingVisual();
            using (var dc = bg.RenderOpen()) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x3D, 0x4A, 0x63)), null, new Rect(0, 0, ActualWidth, 140));
            rtb.Render(bg); rtb.Render(Root); rtb.Freeze(); frames.Add(rtb);
        }
        void Save(string name)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) for (int i = 0; i < frames.Count; i++) dc.DrawImage(frames[i], new Rect(0, i * 140, ActualWidth, 140));
            var dpi = VisualTreeHelper.GetDpi(this);
            var all = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(ActualWidth * dpi.DpiScaleX), (int)(frames.Count * 140 * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            all.Render(dv);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(all));
            using var fs = File.Create(Path.Combine(dir, name + ".png"));
            enc.Save(fs);
            frames.Clear();
        }
        await Task.Delay(1500);
        OnNotificationArrived(new IslandNotification { Id = 0, AppName = "WhatsApp", Title = "Anna", Text = L.T("Kommst du heute noch vorbei? Ich bring Pizza mit und den Film von letzter Woche"), Time = DateTime.Now });
        for (int i = 0; i < 14; i++) { Frame(); await Task.Delay(i < 8 ? 110 : 350); }
        Save("1_bell_ticker");
        OpenTicker();
        for (int i = 0; i < 6; i++) { Frame(); await Task.Delay(110); }
        ShowNotificationBanner(new IslandNotification { Id = 0, AppName = "WhatsApp", Title = "Anna", Text = L.T("Und bring bitte Getränke mit"), Time = DateTime.Now });
        for (int i = 0; i < 4; i++) { Frame(); await Task.Delay(150); }
        Save("2_card");
        EndPeek();
        await Task.Delay(1500);
        ShowListening(new LiveApp("claude", null, null) { Name = "Claude" });
        for (int i = 0; i < 12; i++) { Frame(); await Task.Delay(150); }
        Save("3_listening");
        await Task.Delay(1500);
        // a plain notice (timer end) – narrow first, the card on hover; stays because it rings
        ShowPeek(new PeekInfo { Key = "timer", Important = true, Icon = "timer", GlyphBrush = (Brush)FindResource("Orange"), Title = L.T("Timer abgelaufen"), Text = "25 Min · Fokus", TimeoutMs = 0 }
            .Action(L.T("Nochmal"), null).Action("OK", null, true));
        for (int i = 0; i < 8; i++) { Frame(); await Task.Delay(150); }
        OpenTicker();
        for (int i = 0; i < 5; i++) { Frame(); await Task.Delay(150); }
        Save("4_timer");
        EndPeek();
        await Task.Delay(800);
        ShowPeek(new PeekInfo { Key = "price:1", Important = true, Icon = "cart", GlyphBrush = (Brush)FindResource("Green"), Title = "AirPods Pro", Text = "jetzt 189 € · vorher 229 €", TimeoutMs = 9000 }
            .Action(L.T("Öffnen"), null, true));
        for (int i = 0; i < 5; i++) { Frame(); await Task.Delay(150); }
        OpenTicker();
        for (int i = 0; i < 5; i++) { Frame(); await Task.Delay(150); }
        Save("5_price");
        S.Animations = anim;
        App.Quit();
    }

    public async Task BannerTestAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        H.Clipboard.Persist = false;
        if (_tucked) Untuck();
        async Task Snap(string name)
        {
            await Task.Delay(900);
            var dpi = VisualTreeHelper.GetDpi(this);
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(ActualWidth * dpi.DpiScaleX), (int)(100 * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            var bg = new DrawingVisual();
            using (var dc = bg.RenderOpen()) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x3D, 0x4A, 0x63)), null, new Rect(0, 0, ActualWidth, 100));
            rtb.Render(bg);
            rtb.Render(Root);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var fs = File.Create(Path.Combine(dir, name + ".png"));
            enc.Save(fs);
            EndPeek();
            await Task.Delay(500);
        }
        await Task.Delay(1500);
        var green = (Brush)FindResource("Green");
        ShowPeek(new PeekInfo { Key = "t1", Ring = 1, Icon = "check", GlyphBrush = green, Source = "Claude Code · dynamic island", SourceIcon = "asterisk", SourceBrush = AgentBrush,
            Title = L.T("Fertig: Spotify-Lautstärke einbauen"), Text = L.T("5 von 5 Schritten · 12:35"), TimeoutMs = 0, Click = () => { } }
            .Button("arrow.up.right", "Claude öffnen", null, (Brush)FindResource("Blue")));
        await Snap("1_claude_done");
        ShowPeek(new PeekInfo { Key = "t2", Ring = 0.6, Icon = "asterisk", GlyphBrush = AgentBrush, Source = "Claude Code · dynamic island", SourceIcon = "asterisk", SourceBrush = AgentBrush,
            Title = L.T("Claude braucht deine Antwort"), Text = L.T("Benachrichtigungen bauen · 3 / 5"), TimeoutMs = 0, Click = () => { } }
            .Button("arrow.up.right", L.T("Claude öffnen"), null, AgentBrush, darkGlyph: true));
        await Snap("2_claude_wait");
        ShowPeek(new PeekInfo { Key = "t3", Icon = "mic", GlyphBrush = (Brush)FindResource("Orange"), Source = "Datenschutz", SourceIcon = "shield",
            Title = L.T("Zoom nutzt dein Mikrofon"), Text = L.T("zum ersten Mal"), TimeoutMs = 0 }
            .Button("mic.slash", L.T("Stumm"), null).Button("open", L.T("Öffnen"), null));
        await Snap("3_privacy");
        ShowPeek(new PeekInfo { Key = "t4", Icon = "bell.badge", Source = L.T("Discord · jetzt"), Avatar = "A", AvatarBrush = AvatarColor("Mia"),
            Title = "Mia", Text = L.T("Hast du heute Abend kurz Zeit?"), TimeoutMs = 0, Click = () => { } });
        await Snap("4_message");
        ShowPeek(new PeekInfo { Key = "t5", Icon = "download", GlyphBrush = SteamBrush, Source = "Steam", SourceIcon = "download", SourceBrush = SteamBrush,
            Title = L.T("ELDEN RING ist bereit"), Text = L.T("34,2 GB geladen in 38 Min"), TimeoutMs = 0 }
            .Button("play.fill", "Spielen", null, (Brush)FindResource("Green"), darkGlyph: true));
        await Snap("5_steam");
        App.Quit();
    }

    /// <summary>Make room, visually: normal → address bar "focused" (island up, small bar) → back.</summary>
    public async Task RoomTestAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        H.Clipboard.Persist = false;
        async Task Snap(string name)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(ActualWidth * dpi.DpiScaleX), (int)(90 * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            var bg = new DrawingVisual();
            using (var dc = bg.RenderOpen()) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x3D, 0x4A, 0x63)), null, new Rect(0, 0, ActualWidth, 90));
            rtb.Render(bg);
            rtb.Render(Root);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var fs = File.Create(Path.Combine(dir, name + ".png"));
            enc.Save(fs);
            await Task.CompletedTask;
        }
        await Task.Delay(2000);
        await Snap("1_normal");
        H.Room.ForceForTest(true);
        await Task.Delay(900);
        await Snap("2_away");
        File.WriteAllText(Path.Combine(dir, "state.txt"), $"away={_roomAway} shift={_roomShift.Y:0} strip={RoomStrip.Opacity:0.0} cursorNearTop={IsCursorNearTop(false)} overIsland={IsCursorOverIsland(10)} state={_state} active={H.Room.Active} tucked={_tucked}");
        H.Room.ForceForTest(false);
        await Task.Delay(900);
        await Snap("3_back");
        App.Quit();
    }

    /// <summary>Checks the "what just happened" choice without touching anything real.</summary>
    public void SmartTabTest(string file)
    {
        var sb = new System.Text.StringBuilder();
        var (lastTab, smart) = (S.LastTab, S.SmartTab);
        void Check(string what, string expect)
        {
            var got = ContextTab();
            sb.AppendLine($"{(got == expect ? "OK " : "FEHLER")} {what}: {got} (erwartet {expect})");
        }
        S.SmartTab = true;
        S.LastTab = L.T("Timer");
        _moments.Clear(); _closedTab = null;
        // a copy / a notification always leaves an entry behind – the test window starts with none (nothing is saved)
        H.Clipboard.Persist = false;
        var sample = new ClipItem { Kind = ClipKind.Text, Text = L.T("Test") };
        var note = new IslandNotification { Id = 0, AppName = L.T("Test"), Title = L.T("Test"), Time = DateTime.Now };
        H.Clipboard.Items.Add(sample);
        H.Notifications.Items.Add(note);
        Check("nichts passiert", H.Privacy.InUse ? "Live" : L.T("Timer"));
        Note("Clip", 45); Check(L.T("eben kopiert"), "Clip");
        Note("Agent", 180); Check("danach Claude fertig", "Agent");
        _moments.Add(new Moment("Notify", DateTime.Now.AddMinutes(-5), TimeSpan.FromSeconds(120))); Check("alte Mitteilung zählt nicht", "Agent");
        _closedTab = "Remind"; _closedAt = DateTime.Now; Check("danach auf Erinnerungen gewesen und eben geschlossen", "Remind");
        _closedAt = DateTime.Now.AddSeconds(-20); Check("vor 20 s geschlossen", "Agent");
        ForgetMoment("Agent"); Check(L.T("Agent von Hand verlassen"), "Clip");
        // the user's case: a notification opens Mitteilungen, they scroll to Musik, close, open again
        _moments.Clear(); _closedTab = null;
        Note("Notify", 120); Check(L.T("Mitteilung eben angekommen"), "Notify");
        ForgetMoment("Notify"); _closedTab = "Music"; _closedAt = DateTime.Now.AddSeconds(-3); Check("zu Musik gescrollt, zu, gleich wieder auf", "Music");
        _closedAt = DateTime.Now.AddSeconds(-30); Check("zu Musik gescrollt, nach 30 s wieder auf (zuletzt benutzt)", H.Privacy.InUse ? "Live" : S.LastTab);
        _closedAt = DateTime.Now.AddSeconds(-4); Note("Notify", 120); Check("nach dem Schließen kommt eine neue Mitteilung", "Notify");
        _moments.Clear(); _closedTab = null;
        Check("alles abgelaufen", H.Privacy.InUse ? "Live" : L.T("Timer"));
        // the user's case: Claude done → Agent once; looked, closed, > 5 s later → the usual page (Musik) again
        _moments.Clear(); _closedTab = null;
        S.LastTab = "Music";
        Note("Agent", 180);
        string usual = H.Privacy.InUse ? "Live" : H.Media.HasMedia ? "Music" : "Music";
        Expand(null, false);
        sb.AppendLine($"{(_tab == "Agent" && S.LastTab == "Music" ? "OK " : "FEHLER")} Claude fertig öffnet Agent, gemerkte Seite bleibt Musik: {_tab} / {S.LastTab}");
        _autoShownAt = DateTime.Now.AddSeconds(-2);
        Collapse();
        Check("angeschaut, zu, sofort wieder auf", "Agent");
        _closedAt = DateTime.Now.AddSeconds(-6); Check("angeschaut, zu, nach 6 s wieder auf", usual);
        _closedTab = null;
        // Ablage: no icon while empty, never opened by itself
        if (H.Shelf.Items.Count == 0)
        {
            sb.AppendLine($"{(ShelfTabBtn.Visibility != Visibility.Visible ? "OK " : "FEHLER")} leere Ablage hat kein Symbol");
            S.LastTab = "Shelf"; Check("leere Ablage, zuletzt dort", H.Media.HasMedia ? "Music" : H.Privacy.InUse || H.Steam.Downloading ? "Live" : H.Launcher.Items.Count > 0 ? "Games" : L.T("Timer"));
        }
        // an emptied page is skipped (test copy only – the real history is put back, nothing is saved)
        H.Clipboard.Items.Remove(sample);
        H.Notifications.Items.Remove(note);
        var keep = H.Clipboard.Items.ToList();
        var keepQ = H.Clipboard.Queue.ToList();
        H.Clipboard.Items.Clear(); H.Clipboard.Queue.Clear();
        string alive = H.Media.HasMedia ? "Music" : H.Privacy.InUse || H.Steam.Downloading ? "Live" : H.Launcher.Items.Count > 0 ? "Games" : L.T("Timer");
        S.LastTab = "Clip"; Check(L.T("Zwischenablage geleert, zuletzt dort"), alive);
        _closedTab = "Clip"; _closedAt = DateTime.Now.AddSeconds(-2); Check(L.T("Zwischenablage geleert, eben geschlossen"), H.Privacy.InUse ? "Live" : alive);
        _closedTab = null;
        H.Clipboard.Items.Add(new ClipItem { Kind = ClipKind.Text, Text = "x" });
        Check(L.T("Zwischenablage mit Eintrag"), H.Privacy.InUse ? "Live" : "Clip");
        H.Clipboard.Items.Clear();
        foreach (var i in keep) H.Clipboard.Items.Add(i);
        foreach (var i in keepQ) H.Clipboard.Queue.Add(i);
        File.WriteAllText(file, sb.ToString());
        (S.LastTab, S.SmartTab) = (lastTab, smart); // the test never changes the user's settings
        App.Quit();
    }
#endif

    /// <summary>Chose another page by hand right after opening: the moment that led there is used up.</summary>
    private void ForgetMoment(string tab) => _moments.RemoveAll(m => m.Tab == tab);

    /// <summary>Click on an alert: it goes away at once (a ringing timer is silenced), queued minor alerts too.</summary>
    private void DismissPeek()
    {
        if (_peek?.Key is "timer" or "alarm") H.Sounds.Stop();
        _peeks.RemoveAll(p => !p.Important);
        EndPeek();
    }

    private DateTime _holdOpenUntil;

    private void WatchCursor()
    {
        if (_state != State.Expanded) { _watchTimer.Stop(); return; }
        UpdateHeaderFade();
        if (IsCursorOverIsland(14)) { _cursorOutsideSince = DateTime.MaxValue; return; }
        if (_sticky) return;
        if (Mouse.Captured != null || Ext.IsDraggingSlider) return; // e.g. still dragging a slider just outside the island
        if (DateTime.Now < _holdOpenUntil) return; // a swipe just skipped: let its animation run
        if (IsActive && Keyboard.FocusedElement is TextBox) return;
        if (_cursorOutsideSince == DateTime.MaxValue) { _cursorOutsideSince = DateTime.Now; return; }
        if ((DateTime.Now - _cursorOutsideSince).TotalMilliseconds > 380) Collapse();
    }

    private bool IsCursorOverIsland(double tolerance)
    {
        if (!Island.IsVisible) return false;
        try
        {
            Native.GetCursorPos(out var p);
            var r = ScreenRect(Island); // (mirrored when the island sits at the bottom, 55)
            double t = tolerance * PxX;
            return p.X >= r.Left - t && p.X <= r.Right + t && p.Y >= r.Top - t - 2 && p.Y <= r.Bottom + t;
        }
        catch { return false; }
    }

    // =====================================================================
    //  Spring motion (approved mockup 2026-11-14): size + corners on one spring, the content morphs
    //  (fades with blur and a slight scale), bubbles pull out of the island with a sticky neck.
    //  Runs on CompositionTarget.Rendering only while something moves.
    // =====================================================================

    private readonly Spring _sw = new(IdleW), _sh = new(PillH), _sr = new(PillH / 2);
    private double _w0 = IdleW, _h0 = PillH;
    private FrameworkElement _activePanel;
    private bool _activeShown;
    private readonly Dictionary<FrameworkElement, Spring> _panelFade = new();
    private readonly Dictionary<FrameworkElement, (Thickness Margin, VerticalAlignment V, HorizontalAlignment Hz)> _panelLayout = new();
    private readonly HashSet<FrameworkElement> _pinned = new();
    private readonly BubbleMotion _bm1 = new(), _bm2 = new();
    private bool _moving;
    private TimeSpan _lastFrame;
    private const double BubbleGap = 6;

    private sealed class BubbleMotion
    {
        public readonly Spring Gap = new(-38, Spring.IslandResponse * 0.96, Spring.IslandDamping * 0.85); // tighter (2026-11-15 polish): was × 1.15
        public readonly Spring Fade = new(0, Spring.IslandResponse * 0.6, 1, 0.005);
        /// <summary>Size of the symbol inside the bubble: starts small, pops to full size as the drop leaves.</summary>
        public readonly Spring Size = new(1, 0.36, 0.55, 0.003); // was 0.42
        /// <summary>Seconds since this bubble was born (-1 = not being born).</summary>
        public double Birth = -1;
        /// <summary>The drop has torn off its neck (at the widest swing) – from then on it behaves like a resting bubble.</summary>
        public bool Released;
        public bool Holding => Birth >= 0 && !Released;
        public bool Want;
    }

    // birth of a bubble (approved 2026-11-14, mix of mockup C + A): the symbol first shows small INSIDE the island
    // while the island bulges, then it drips out as a drop – the neck stretches and snaps at the widest swing
    private const double BirthHold = 0.22, BulgeOut = 10, BirthBreak = 8, RestBreak = 5.5;

    /// <summary>Island and bubbles slide left together so the whole group stays centred on the screen.</summary>
    private readonly TranslateTransform _centreShift = new();
    // same spring as the shape (polish 2026-11-16): a faster centring sloshed sideways on its own when you changed your mind mid-move
    private readonly Spring _centre = new(0, Spring.IslandResponse, 1, 0.05);
    private readonly Dictionary<Border, Grid> _bubbleInner = new();
    private readonly Dictionary<Border, ScaleTransform> _innerScale = new();
    private System.Windows.Shapes.Rectangle _ghost;
    private VisualBrush _ghostBrush;

    private Grid Inner(Border b) => _bubbleInner.TryGetValue(b, out var g) ? g : (Grid)b.Child;

    private FrameworkElement[] Panels => new FrameworkElement[] { PillPanel, HintPanel, PeekPanel, TickerPanel, ExpandedPanel, AskPanel, MomentPanel, QueryPanel };

    private static Spring NewFade(double from) => new(from, Spring.IslandResponse * 0.55, 1, 0.005);

    private void ShowPanel(FrameworkElement panel)
    {
        foreach (var p in Panels)
        {
            p.BeginAnimation(OpacityProperty, null);
            if (p == panel || p.Visibility != Visibility.Visible) continue;
            // the old content stays where it is and fades out (blurred) while the island morphs
            if (!_pinned.Contains(p)) Freeze(p);
            p.IsHitTestVisible = false;
            if (!_panelFade.ContainsKey(p)) _panelFade[p] = NewFade(p.Opacity);
        }
        if (panel == _activePanel && panel.Visibility == Visibility.Visible) { StartMotion(); return; }

        Unpin(panel); // fresh layout – callers may measure it right away (alerts)
        panel.IsHitTestVisible = true;
        if (!_panelFade.ContainsKey(panel))
        {
            panel.Opacity = 0;
            _panelFade[panel] = NewFade(0);
        }
        panel.Visibility = Visibility.Visible;
        _activePanel = panel;
        _activeShown = false;
        StartMotion();
    }

    /// <param name="bouncy">kept for the callers – every change now uses the same approved spring</param>
    private void AnimateSize(double w, double h, double radius, bool bouncy)
    {
        _targetW = w; _targetH = h;
        _w0 = _sw.Value; _h0 = _sh.Value;
        UpdateAura(); // every change of state comes through here (open, close, card)
        _sw.Target = w; _sh.Target = h; _sr.Target = radius;
        // the closed pill only changing its width (a ring added, the stack fanning out): its content grows WITH the island –
        // laid out at the final width it stuck out and the cover was cut at the left for a moment (2026-11-18)
        bool pillOnly = _activePanel == PillPanel && Math.Abs(_sh.Value - h) < 1 && Math.Abs(h - (PillH + NotchExtra)) < 1;
        if (pillOnly) Unpin(PillPanel);
        else Pin(_activePanel);
        // bubbles centred next to a slim island (pill, hint); next to a tall alert they stay at the top
        double visible = h - NotchExtra;
        double top = (S.Shape == IslandShape.Notch ? 0 : 8) + (visible <= 46 ? Math.Max(0, (visible - 32) / 2) : 0);
        Bubble.Margin = Bubble2.Margin = new Thickness(0, top, 0, 0);
        UpdateGlow();
        StartMotion();
    }

    // ---------- style "Tiefe": the cover colour glows softly from inside the island ----------

    private bool _glowOn;
    private Color _glowColor;

    /// <summary>On for the music cover in the pill and on the Music page; follows the cover's (lifted) colour.</summary>
    private void UpdateGlow()
    {
        bool music = _state == State.Expanded ? _tab == "Music" : _state == State.Collapsed && LeadCover.Visibility == Visibility.Visible;
        bool want = S.Theme == IslandTheme.Tiefe && music && H.Media.Cover != null && H.Media.CoverColor.A > 0;
        if (want && MusicTint() is SolidColorBrush tint && tint.Color != _glowColor)
        {
            _glowColor = tint.Color;
            var b = new RadialGradientBrush();
            b.GradientStops.Add(new GradientStop(Color.FromArgb(0x4A, _glowColor.R, _glowColor.G, _glowColor.B), 0));
            b.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, _glowColor.R, _glowColor.G, _glowColor.B), 0.7));
            b.Freeze();
            CoverGlow.Background = b;
        }
        PlaceGlow();
        if (want == _glowOn) return;
        _glowOn = want;
        CoverGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(want ? 1 : 0, TimeSpan.FromMilliseconds(want ? 380 : 200)));
    }

    /// <summary>Behind the small cover in the pill, behind the big one when open – in between it follows the spring.</summary>
    private void PlaceGlow()
    {
        double t = Math.Clamp((Island.Height - NotchExtra - PillH) / (ExpH - PillH), 0, 1);
        double w = 140 + (380 - 140) * t, h = 88 + (240 - 88) * t;
        double cx = 16 + (31 - 16) * t, cy = (Island.Height - NotchExtra) / 2 * (1 - t) + 60 * t;
        CoverGlow.Width = w;
        CoverGlow.Height = h;
        CoverGlow.Margin = new Thickness(cx - w / 2, cy - h / 2, 0, 0);
    }

    private void UpdateClip()
    {
        double w = double.IsNaN(Island.Width) ? Island.ActualWidth : Island.Width;
        double h = double.IsNaN(Island.Height) ? Island.ActualHeight : Island.Height;
        double r = Island.CornerRadius.TopLeft;
        // continuous corners like Apple's (the pill stays a capsule); the fine rim follows the same shape
        Island.Clip = SmoothCorners.Make(w, h, r);
        IslandRim.Data = SmoothCorners.Make(w, h, r, inset: 0.25);
        IslandRim.Margin = new Thickness(0, -IslandContent.Margin.Top, 0, 0); // notch: the content sits lower, the rim does not
        if (_timerRim?.Visibility == Visibility.Visible) UpdateTimerRim(); // the line follows the moving shape
    }

    private void Remember(FrameworkElement p)
    {
        if (!_panelLayout.ContainsKey(p)) _panelLayout[p] = (p.Margin, p.VerticalAlignment, p.HorizontalAlignment);
    }

    /// <summary>
    /// Lays the incoming content out at its FINAL size, anchored at the top centre – the growing island only
    /// uncovers it (no re-flowing text while the shape morphs).
    /// </summary>
    private void Pin(FrameworkElement p)
    {
        if (p == null || p.Visibility != Visibility.Visible) return;
        Remember(p);
        var o = _panelLayout[p];
        double w = Math.Max(0, _targetW - o.Margin.Left - o.Margin.Right);
        double avail = Math.Max(0, _targetH - NotchExtra - o.Margin.Top - o.Margin.Bottom);
        p.Margin = o.Margin;
        p.Width = w;
        p.Height = double.NaN;
        p.HorizontalAlignment = HorizontalAlignment.Center;
        double top = o.Margin.Top;
        if (o.V == VerticalAlignment.Center)
        {
            p.Measure(new Size(w + o.Margin.Left + o.Margin.Right, double.PositiveInfinity));
            double ch = Math.Max(0, p.DesiredSize.Height - o.Margin.Top - o.Margin.Bottom);
            top += (avail - ch) / 2;
        }
        else if (o.V == VerticalAlignment.Stretch) p.Height = avail;
        bool bottom = o.V == VerticalAlignment.Bottom;
        p.VerticalAlignment = bottom ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        p.Margin = new Thickness(o.Margin.Left, bottom ? 0 : top, o.Margin.Right, bottom ? o.Margin.Bottom : 0);
        if (p == PillPanel) PadPill();
        _pinned.Add(p);
    }

    /// <summary>Outgoing content keeps exactly its current place and size while it fades out.</summary>
    private void Freeze(FrameworkElement p)
    {
        Remember(p);
        var o = _panelLayout[p];
        double y;
        try { y = p.TranslatePoint(new Point(0, 0), IslandContent).Y; } catch { y = o.Margin.Top; }
        p.Width = p.ActualWidth;
        p.Height = p.ActualHeight;
        p.HorizontalAlignment = HorizontalAlignment.Center;
        p.VerticalAlignment = VerticalAlignment.Top;
        p.Margin = new Thickness(o.Margin.Left, y, o.Margin.Right, 0);
        if (p == PillPanel) PadPill();
        _pinned.Add(p);
    }

    private void Unpin(FrameworkElement p)
    {
        if (p == null || !_pinned.Remove(p)) return;
        var o = _panelLayout[p];
        p.Width = p.Height = double.NaN;
        p.Margin = o.Margin;
        if (p == PillPanel) PadPill();
        p.VerticalAlignment = o.V;
        p.HorizontalAlignment = o.Hz;
    }

    private static void ApplyMorph(FrameworkElement p, double o)
    {
        p.Opacity = o;
        if (p.RenderTransform is not ScaleTransform st)
        {
            st = new ScaleTransform();
            p.RenderTransform = st;
            p.RenderTransformOrigin = new Point(0.5, 0);
        }
        st.ScaleX = st.ScaleY = 0.9 + 0.1 * o;
        double blur = (1 - o) * 7;
        if (blur > 0.3)
        {
            if (p.Effect is not BlurEffect be) p.Effect = be = new BlurEffect { RenderingBias = RenderingBias.Performance };
            be.Radius = blur;
        }
        else p.Effect = null;
    }

    private static void ClearMorph(FrameworkElement p)
    {
        p.Effect = null;
        p.RenderTransform = Transform.Identity;
    }

    private static double Progress(double v, double from, double to) =>
        Math.Abs(to - from) < 1 ? 1 : Math.Clamp(1 - Math.Abs(v - to) / Math.Abs(to - from), 0, 1);

    private void StartMotion()
    {
        if (_moving) return;
        _moving = true;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnMotionFrame;
    }

    private void OnMotionFrame(object sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == _lastFrame) return; // Rendering may fire more than once per frame
        double dt = _lastFrame == TimeSpan.Zero ? 1 / 60.0 : Math.Clamp((now - _lastFrame).TotalSeconds, 0, 0.05);
        _lastFrame = now;

        // shape: width, height and corner radius on the same spring
        _sw.Step(dt); _sh.Step(dt); _sr.Step(dt);
        StepEyes(dt); // the eyes widen the island to the left (2026-11-16)
        double w = Math.Max(8, _sw.Value) + EyesExt, h = Math.Max(8, _sh.Value);
        Island.Width = w;
        Island.Height = h;
        Island.CornerRadius = new CornerRadius(Math.Clamp(_sr.Value, 0, Math.Min(w, h) / 2));
        UpdateClip();
        if (_glowOn || CoverGlow.Opacity > 0) PlaceGlow();

        // content: the new page appears once the shape is ~40 % there, the old one fades out at once
        double p = Math.Min(Progress(_sw.Value, _w0, _sw.Target), Progress(_sh.Value, _h0, _sh.Target));
        foreach (var (panel, fade) in _panelFade.ToList())
        {
            bool active = panel == _activePanel;
            if (active && p > 0.4) _activeShown = true;
            fade.Target = active && _activeShown ? 1 : 0;
            fade.Step(dt);
            double o = Math.Clamp(fade.Value, 0, 1);
            ApplyMorph(panel, o);
            if (!fade.Settled) continue;
            if (!active)
            {
                panel.Visibility = Visibility.Collapsed;
                panel.Opacity = 0;
                panel.IsHitTestVisible = true;
                ClearMorph(panel);
                Unpin(panel);
                _panelFade.Remove(panel);
            }
            else if (o > 0.99)
            {
                panel.Opacity = 1;
                ClearMorph(panel);
                _panelFade.Remove(panel);
            }
        }

        StepSlide(dt); // the page slide rides the same spring as the shape
        StepBubble(Bubble, _bm1, dt);
        StepBubble(Bubble2, _bm2, dt);
        PlaceBubbles();
        _centre.Target = -BubbleExtent() / 2;
        _centre.Step(dt);
        _centreShift.X = _centre.Value - EyesExt / 2; // grows to the left only: the right edge stays
        UpdateBridges();
        UpdateGhost();
        StepCoverFlight(dt);

        bool done = !_flying && !_sliding && EyesSettled && _sw.Settled && _sh.Settled && _sr.Settled && _panelFade.Count == 0 && _centre.Settled
                    && _bm1.Gap.Settled && _bm1.Fade.Settled && _bm1.Size.Settled && _bm1.Birth < 0
                    && _bm2.Gap.Settled && _bm2.Fade.Settled && _bm2.Size.Settled && _bm2.Birth < 0;
        if (!done) return;
        CompositionTarget.Rendering -= OnMotionFrame;
        _moving = false;
        Unpin(_activePanel); // at rest the normal layout is identical – and follows later content changes again
        BubbleBridge.Visibility = Bubble2Bridge.Visibility = Visibility.Collapsed; // a neck only exists while moving
    }

    private void StepBubble(Border b, BubbleMotion m, double dt)
    {
        if (!m.Want) m.Birth = -1;
        else if (m.Birth >= 0) m.Birth += dt;
        bool holding = m.Birth >= 0 && m.Birth < BirthHold;
        // while holding the bubble only peeks out a little: the island bulges with the small symbol in it
        m.Gap.Target = !m.Want ? -(b.Width + 2) : holding ? -(b.Width - BulgeOut) : BubbleGap;
        m.Fade.Target = m.Want ? 1 : 0;
        m.Size.Target = holding ? 0.62 : 1;
        m.Gap.Step(dt);
        m.Fade.Step(dt);
        m.Size.Step(dt);
        var inner = Inner(b);
        inner.Opacity = Math.Clamp(m.Fade.Value, 0, 1);
        if (_innerScale.TryGetValue(b, out var isc)) isc.ScaleX = isc.ScaleY = Math.Max(0.05, m.Size.Value);
        // the neck tears at the widest swing (or once it is stretched to the limit)
        if (m.Birth >= BirthHold && !m.Released && m.Gap.Value > 0 && (m.Gap.Velocity <= 0 || m.Gap.Value >= BirthBreak - 1.3)) m.Released = true;
        if (m.Birth >= BirthHold && m.Gap.Settled && m.Size.Settled) m.Birth = -1;
        // fully swallowed by the island: gone
        if (!m.Want && m.Gap.Settled && b.Visibility == Visibility.Visible) b.Visibility = Visibility.Collapsed;
    }

    /// <summary>Bubbles sit right of the island, 6 px apart – a capsule bubble is wider. They follow the live island width.</summary>
    private void PlaceBubbles()
    {
        double w = Island.Width;
        BubbleShift.X = w / 2 + _bm1.Gap.Value + Bubble.Width / 2;
        double base2 = w / 2 + (Bubble.Visibility == Visibility.Visible ? Math.Max(0, _bm1.Gap.Value + Bubble.Width) : 0);
        Bubble2Shift.X = base2 + _bm2.Gap.Value + Bubble2.Width / 2;
    }

    private void SetBubbleWant(Border b, bool want)
    {
        var m = b == Bubble ? _bm1 : _bm2;
        if (want && b.Visibility != Visibility.Visible)
        {
            // a new bubble is born inside the island and pulls itself out – with full animations as a drop
            bool drop = S.Animations == AnimationLevel.An;
            m.Gap.Snap(-(b.Width + 2));
            m.Fade.Snap(0);
            m.Size.Snap(drop ? 0.3 : 1);
            m.Birth = drop ? 0 : -1;
            m.Released = false;
            Inner(b).Opacity = 0;
            if (_innerScale.TryGetValue(b, out var isc)) isc.ScaleX = isc.ScaleY = m.Size.Value;
            b.Visibility = Visibility.Visible;
        }
        m.Want = want;
        StartMotion();
    }

    private void HideBubbleNow(Border b)
    {
        var m = b == Bubble ? _bm1 : _bm2;
        m.Want = false;
        m.Gap.Snap(-(b.Width + 2));
        m.Fade.Snap(0);
        m.Size.Snap(1);
        m.Birth = -1;
        b.Visibility = Visibility.Collapsed;
        BubbleBridge.Visibility = Bubble2Bridge.Visibility = Visibility.Collapsed;
        if (_ghost != null) _ghost.Visibility = Visibility.Collapsed;
        StartMotion(); // the island glides back to the middle
    }

    /// <summary>How far the bubbles currently stick out right of the island (follows their springs).</summary>
    private double BubbleExtent()
    {
        double e1 = Bubble.Visibility == Visibility.Visible ? Math.Max(0, _bm1.Gap.Value + Bubble.Width) : 0;
        double e2 = Bubble2.Visibility == Visibility.Visible ? Math.Max(0, _bm2.Gap.Value + Bubble2.Width) : 0;
        return e1 + e2;
    }

    /// <summary>
    /// While the first bubble is still inside the island its symbol is drawn ON the island (a live copy, clipped to the
    /// island's shape) – so it shows small in the island first; outside the island the real bubble takes over seamlessly.
    /// </summary>
    private void UpdateGhost()
    {
        if (_ghost == null) return;
        bool show = _bm1.Birth >= 0 && Bubble.Visibility == Visibility.Visible && Island.Visibility == Visibility.Visible
                    && _bm1.Gap.Value < 0 && Bubble.Child is FrameworkElement body && body.ActualWidth > 0;
        if (!show)
        {
            if (_ghost.Visibility == Visibility.Visible) _ghost.Visibility = Visibility.Collapsed;
            return;
        }
        var fe = (FrameworkElement)Bubble.Child;
        double gw = fe.ActualWidth, gh = fe.ActualHeight, bt = Bubble.BorderThickness.Top;
        _ghost.Width = gw;
        _ghost.Height = gh;
        _ghost.Margin = new Thickness(0, Bubble.Margin.Top + bt, 0, 0);
        _ghostBrush.Viewbox = _ghostBrush.Viewport = new Rect(0, 0, gw, gh);
        double w = Island.Width, h = Island.Height, r = Island.CornerRadius.BottomRight;
        var clip = new RectangleGeometry(new Rect(gw / 2 - w / 2 - BubbleShift.X, Island.Margin.Top - _ghost.Margin.Top, w, h), r, r);
        clip.Freeze();
        _ghost.Clip = clip;
        _ghost.Visibility = Visibility.Visible;
    }

    /// <summary>The sticky neck: while a bubble is closer than ~7 px it stays connected, thinning until it snaps.</summary>
    private void UpdateBridges()
    {
        double cx = Root.ActualWidth / 2, w = Island.Width;
        double cy = Bubble.Margin.Top + Bubble.Height / 2;
        bool island = Island.Visibility == Visibility.Visible;
        bool b1 = island && Bubble.Visibility == Visibility.Visible;
        double b1Left = cx + BubbleShift.X - Bubble.Width / 2;
        DrawBridge(BubbleBridge, b1, cx + w / 2, b1Left, cy, _bm1.Holding ? BirthBreak : RestBreak);
        double b2Left = cx + Bubble2Shift.X - Bubble2.Width / 2;
        DrawBridge(Bubble2Bridge, b1 && Bubble2.Visibility == Visibility.Visible, b1Left + Bubble.Width, b2Left, cy,
                   _bm2.Holding ? BirthBreak : RestBreak);
    }

    private static void DrawBridge(System.Windows.Shapes.Path path, bool show, double left, double right, double cy, double brk)
    {
        // at rest it snaps before the resting gap (6 px), so a bubble never keeps a thread to the island; while a bubble
        // is born it holds a little longer (8 px) and snaps right at the widest swing of the drop
        const double Half = 12.5, Inset = 7;
        double gap = right - left;
        double pinch = show && gap < brk ? Half * Math.Pow(1 - Math.Max(0, gap) / brk, 0.9) : 0;
        double x0 = left - Inset, x1 = right + Inset;
        if (pinch < 1.2 || x1 <= x0 + 1) { path.Visibility = Visibility.Collapsed; return; }
        double mid = (x0 + x1) / 2;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(x0, cy - Half), true, true);
            c.QuadraticBezierTo(new Point(mid, cy - 2 * pinch + Half), new Point(x1, cy - Half), true, false);
            c.LineTo(new Point(x1, cy + Half), true, false);
            c.QuadraticBezierTo(new Point(mid, cy + 2 * pinch - Half), new Point(x0, cy + Half), true, false);
        }
        g.Freeze();
        path.Data = g;
        path.Visibility = Visibility.Visible;
    }

    private void ApplyShape()
    {
        bool notch = S.Shape == IslandShape.Notch;
        Island.Margin = notch ? new Thickness(0, -NotchExtra, 0, 0) : new Thickness(0, 8, 0, 0);
        IslandContent.Margin = notch ? new Thickness(0, NotchExtra, 0, 0) : new Thickness(0);
        IslandShadow.Opacity = notch ? 0.25 : 0.4;
        Bubble.Margin = notch ? new Thickness(0) : new Thickness(0, 8, 0, 0);
        Bubble2.Margin = Bubble.Margin;
        if (_state == State.Expanded) AnimateSize(ExpW, TabHeight(_tab) + NotchExtra, ExpRadius, false);
    }

    // =====================================================================
    //  Make room (approved 2026-11-14): browser address bar focused or a YouTube video in fullscreen on the main
    //  screen → the island slides up out of the picture, a small bar shows it is only hidden. Pointer up to the
    //  top → it comes down (and opens as usual); pointer away → up again. Notices still come down.
    // =====================================================================

    private AxisReorder _gamesReorder;
    private readonly TranslateTransform _roomShift = new();
    private readonly DispatcherTimer _roomTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private bool _roomAway;

    private readonly ScaleTransform _islandScale = new();
    private readonly Dictionary<Border, ScaleTransform> _bubbleScale = new();

    private static TransformGroup Group(params Transform[] parts)
    {
        var g = new TransformGroup();
        foreach (var t in parts) g.Children.Add(t);
        return g;
    }

    private void InitRoom()
    {
        // the island: a scale for the small "squash" moments, then the make-room shift
        var ig = new TransformGroup();
        ig.Children.Add(_islandScale);
        ig.Children.Add(_roomShift);
        Island.RenderTransform = ig;
        Island.RenderTransformOrigin = new Point(0.5, 0.5);
        ig.Children.Add(_centreShift);
        ShadowLayer.RenderTransform = Group(_roomShift, _centreShift);
        BridgeLayer.RenderTransform = Group(_roomShift, _centreShift);
        Ripple1.RenderTransform = Group(Ripple1Scale, _centreShift);
        Ripple2.RenderTransform = Group(Ripple2Scale, _centreShift);
        foreach (var (b, shift) in new[] { (Bubble, BubbleShift), (Bubble2, Bubble2Shift) })
        {
            var sc = new ScaleTransform();
            _bubbleScale[b] = sc;
            b.RenderTransform = Group(sc, shift, _roomShift, _centreShift);
            b.RenderTransformOrigin = new Point(0.5, 0.5);

            // the bubble's content gets its own wrapper: the symbol can grow inside it, and the island's copy (ghost) mirrors it
            var inner = (Grid)b.Child;
            b.Child = null;
            var body = new Grid();
            body.Children.Add(inner);
            b.Child = body;
            _bubbleInner[b] = inner;
            var isc = new ScaleTransform();
            inner.RenderTransform = isc;
            inner.RenderTransformOrigin = new Point(0.5, 0.5);
            _innerScale[b] = isc;
        }

        _ghostBrush = new VisualBrush
        {
            Visual = Bubble.Child, Stretch = Stretch.Fill,
            ViewboxUnits = BrushMappingMode.Absolute, ViewportUnits = BrushMappingMode.Absolute
        };
        _ghost = new System.Windows.Shapes.Rectangle
        {
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false, Visibility = Visibility.Collapsed, Fill = _ghostBrush,
            RenderTransform = Group(_bubbleScale[Bubble], BubbleShift, _roomShift, _centreShift),
            RenderTransformOrigin = new Point(0.5, 0.5)
        };
        Root.Children.Insert(Root.Children.IndexOf(Island) + 1, _ghost);
        H.Room.PropertyChanged += (_, _) => { UpdatePill(true); UpdateVisibility(); UpdateRoom(); };
        _roomTimer.Tick += (_, _) => UpdateRoom();
    }

    private void UpdateRoom()
    {
        if (!H.Room.Active || _tucked) { _roomTimer.Stop(); SetRoomAway(false); return; }
        if (!_roomTimer.IsEnabled) _roomTimer.Start();
        // comes down only when the pointer really touches the top edge; once down it stays while the pointer is on it
        bool near = _roomAway ? IsCursorNearTop(edgeOnly: true) : IsCursorNearTop(edgeOnly: false) || IsCursorOverIsland(10);
        SetRoomAway(_state == State.Collapsed && !near);
    }

    /// <summary>
    /// Pointer at the top centre of the main screen. <paramref name="edgeOnly"/>: just the outermost pixels (to call the
    /// hidden island down – user wish: only at the very edge); otherwise the band where the island sits.
    /// </summary>
    private bool IsCursorNearTop(bool edgeOnly)
    {
        Native.GetCursorPos(out var p);
        var b = IslandScreen().Bounds;
        var dpi = VisualTreeHelper.GetDpi(this);
        double half = 170 * PxX, depth = (edgeOnly ? 3 * dpi.DpiScaleY : 46 * PxY);
        double cx; try { cx = Island.PointToScreen(new Point(Island.ActualWidth / 2, 0)).X; } catch { cx = b.Left + b.Width / 2.0; } // the island may sit off-centre (move mode)
        return p.Y >= b.Top && p.Y <= b.Top + depth && Math.Abs(p.X - cx) <= half;
    }

    private void SetRoomAway(bool away)
    {
        if (away == _roomAway) return;
        _roomAway = away;
        double up = -(Island.ActualHeight + Math.Max(0, Island.Margin.Top) + 24);
        _roomShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(away ? up : 0, TimeSpan.FromMilliseconds(away ? 380 : 420))
            { EasingFunction = away ? new CubicEase { EasingMode = EasingMode.EaseIn } : new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut } });
        RoomStrip.BeginAnimation(OpacityProperty, new DoubleAnimation(away ? 1 : 0, TimeSpan.FromMilliseconds(away ? 300 : 150))
            { BeginTime = TimeSpan.FromMilliseconds(away ? 200 : 0) });
    }

    public void ToggleHidden()
    {
        if (_tucked) Untuck(); else Tuck();
    }

    // =====================================================================
    //  Tucked away: nothing visible until the cursor comes near the top centre,
    //  then a thin strip appears – clicking it brings the island back
    // =====================================================================

    private void Tuck_Click(object sender, RoutedEventArgs e) => Tuck();

    private void Tuck()
    {
        _tucked = true;
        if (!S.IslandTucked) { S.IslandTucked = true; App.SaveSettings(); }
        bool shrink = S.Animations != AnimationLevel.Aus && !_captureHidden && Island.IsVisible;
        _tuckShrinking = shrink; // closing must not hide it yet – it shrinks into the strip first
        Collapse();
        _stripShown = false;
        TuckStrip.BeginAnimation(OpacityProperty, null);
        TuckStrip.Opacity = 0;
        TuckStrip.Visibility = Visibility.Visible;
        _nearTimer.Start();
        if (shrink)
        {
            // polish 2026-11-15: the island first shrinks into the strip, then it is gone
            HideBubbleNow(Bubble); HideBubbleNow(Bubble2);
            AnimateSize(64, 6 + NotchExtra, 3, true);
            RollUp(); // it rolls up into the top edge
            After(240, () => { _tuckShrinking = false; UpdateVisibility(); });
            After(140, TuckIntro);
            return;
        }
        UpdateVisibility();
        TuckIntro();
    }

    private bool _tuckShrinking;

    private void Untuck()
    {
        _tucked = false;
        S.IslandTucked = false;
        App.SaveSettings();
        _nearTimer.Stop();
        TuckStrip.Visibility = Visibility.Collapsed;
        UpdateVisibility();
        Expand(null, true);
        RollDown(); // and unrolls from it
    }

    private void CheckNearStrip()
    {
        if (!_tucked) { _nearTimer.Stop(); return; }
        if (DateTime.Now < _tuckIntroUntil) return; // the strip is just showing where the island went
        bool near = false;
        try
        {
            Native.GetCursorPos(out var p);
            var dpi = VisualTreeHelper.GetDpi(this);
            var r = ScreenRect(TuckStrip);
            double cx = (r.Left + r.Right) / 2;
            near = Math.Abs(p.X - cx) <= 150 * PxX && (S.IslandBottom ? p.Y >= r.Bottom - 40 * PxY && p.Y <= r.Bottom + 4 : p.Y <= r.Top + 40 * PxY && p.Y >= r.Top - 4);
        }
        catch { }
        // only while nothing else of the island is showing
        if (_state != State.Collapsed) near = false;
        if (near == _stripShown) return;
        _stripShown = near;
        TuckStrip.BeginAnimation(OpacityProperty, new DoubleAnimation(near ? 1 : 0, TimeSpan.FromMilliseconds(near ? 140 : 260)));
    }

    /// <summary>Hides the island while a screenshot is being taken.</summary>
    public void SetCaptureHidden(bool hidden)
    {
        _captureHidden = hidden;
        if (hidden) Collapse();
        Opacity = hidden ? 0 : 1;
    }

    private void UpdateVisibility()
    {
        // a fullscreen YouTube video is no game: the island just makes room there (see UpdateRoom)
        bool gameHide = H.Game.IsFullscreen && !H.Room.BrowserInFront && S.Fullscreen == FullscreenMode.Hide && _state == State.Collapsed;
        var v = (_hiddenByUser || _tucked) && _state == State.Collapsed && !_tuckShrinking || gameHide ? Visibility.Hidden : Visibility.Visible;
        Island.Visibility = v;
        ShadowLayer.Visibility = v;
        if (v != Visibility.Visible) { HideBubbleNow(Bubble); HideBubbleNow(Bubble2); }
    }

    // =====================================================================
    //  Compact pill: live activities
    // =====================================================================

    /// <summary>Shown while a GIF recording runs (null = not recording).</summary>
    public void SetRecording(string elapsed)
    {
        _recordingText = elapsed;
        UpdatePill();
    }

    private void UpdatePill(bool force = false)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => UpdatePill(force))); return; }

        bool game = H.Game.IsFullscreen && !H.Room.BrowserInFront && S.Fullscreen == FullscreenMode.StatsBar && _recordingText == null;
        bool music = H.Media.HasMedia && !string.IsNullOrEmpty(H.Media.Title);
        bool playing = music && H.Media.IsPlaying;

        var acts = new List<Act>();
        _timerLead = false;
        bool alarmSoon = H.Alarm.At is { } alarmAt && alarmAt - DateTime.Now < TimeSpan.FromHours(1);
        // music keeps the island only while it PLAYS (user wish 2026-11-17): paused – no bars – it steps back as soon as
        // anything else is going on, and that fills the island instead of becoming a bubble beside a still cover
        bool othersGoingOn = _recordingText != null || H.Timer.Active || H.Audio.MicMuted || H.Privacy.InUse && !JustHungUp || H.Obs.Active
                             || H.Agents.AnyRunning || H.Agents.AnyWaiting || DateTime.Now < _agentDoneUntil || AnyDl || DateTime.Now < _steamDoneUntil || alarmSoon;
        bool showMusic = music && (playing || !othersGoingOn);
        if (_recordingText != null) acts.Add(Act.Recording);
        // playing music keeps the island (user rule) – a running timer then becomes a bubble with its ring
        if (H.Timer.Active && !showMusic) acts.Add(Act.Timer);
        if (showMusic) acts.Add(Act.Music);
        if (H.Audio.MicMuted) acts.Add(Act.MicMuted);
        // queue mode shows no lasting status (user wish 2026-11-14) – only a short notice when something was pasted
        // mic / camera / screen sharing and Claude: in their own little bubbles next to the island when something
        // else is showing (music keeps the island – like the separate mic dot on the iPhone); alone they fill it
        var side = new List<Act>();
        // just hung up: Windows still reports the microphone for a moment – the call must not pop out again
        if (H.Privacy.InUse && !JustHungUp) side.Add(Act.Live);
        if (H.Timer.Active && showMusic) side.Add(Act.Timer);
        if (H.Obs.Active) side.Add(Act.Obs);
        if (H.Agents.AnyRunning || H.Agents.AnyWaiting || DateTime.Now < _agentDoneUntil) side.Add(Act.Agent);
        if (AnyDl || DateTime.Now < _steamDoneUntil) side.Add(Act.Steam); // Steam and browser downloads
        // the alarm shows itself in its last hour (a small bell with the time) – not all night long
        if (alarmSoon) side.Add(Act.Alarm);
        if (H.Sys.LowDevice != null) side.Add(Act.Device); // a controller / headphones at 20 % (23a)
        if (H.Sys.Printing) side.Add(Act.Print);          // the printer prints (27)
        acts.InsertRange(Math.Min(1, acts.Count), side);
        // ring stack (default, 2026-11-18): a call / the muted mic never takes the left – it sits on the right with the rings
        if (!S.SideBubbles && acts.Count > 1)
            foreach (var right in acts.Where(a => a is Act.Live or Act.MicMuted).ToList()) { acts.Remove(right); acts.Add(right); }
        // Claude waits for your answer (C1, 2026-11-18): it takes the island with "Claude wartet auf dich"
        if (H.Agents.AnyWaiting && acts.Remove(Act.Agent)) acts.Insert(0, Act.Agent);
        // pause / play, a new lead, Claude done – before the bars are stopped below (IslandWindow.Qol.cs)
        PillMoments(game || acts.Count == 0 ? null : acts[0], playing);

        // what the pill showed before this rebuild (the song moment needs it – after the next line it is always hidden)
        bool coverWasShown = LeadCover.IsVisible, iconWasShown = LeadIcon.IsVisible;
        LeadCover.Visibility = LeadIcon.Visibility = LeadText.Visibility = Visibility.Collapsed;
        Equalizer.Visibility = TrailText.Visibility = TrailLive.Visibility = Visibility.Collapsed;
        FocusBadge.Visibility = SleepBadge.Visibility = Visibility.Collapsed;
        bool clock = !game && acts.Count == 0 && S.ClockInIsland; // nothing going on: the time in the middle (mockup B)
        if (clock && IdleClock.Visibility != Visibility.Visible) { IdleClock.Roll = false; IdleClock.Text = ClockText(); IdleClock.Roll = true; }
        IdleClock.Visibility = clock ? Visibility.Visible : Visibility.Collapsed;
        if (WaterGlass.IsVisible != (clock && S.Water)) After(1, UpdateWaterGlass); // the glass of water beside the clock (51)
        Lead.ToolTip = null;

        var musicColor = MusicTint();
        BarColor(musicColor);

        if (game)
        {
            // with the temperatures when Windows / the graphics driver tell them (28, 2026-11-18) – red from 90 °C
            var hotBrush = (Brush)FindResource("Danger");
            bool cpuHot = H.Sys.CpuTemp >= 90, gpuHot = H.Sys.GpuTemp >= 90;
            LeadText.Text = $"CPU {H.Stats.Cpu:0}%" + (H.Sys.CpuTemp is { } ct ? $"  {ct:0}°" : "");
            LeadText.Foreground = cpuHot ? hotBrush : Brushes.White;
            LeadText.Visibility = Visibility.Visible;
            TrailText.Text = H.Stats.HasGpu ? $"GPU {H.Stats.Gpu:0}%" + (H.Sys.GpuTemp is { } gt ? $" {gt:0}°" : "") + $"  RAM {H.Stats.Ram:0}%" : $"RAM {H.Stats.Ram:0}%";
            TrailText.FontSize = 12;
            TrailText.Foreground = gpuHot ? hotBrush : Brushes.White;
            TrailText.Visibility = Visibility.Visible;
            if (gpuHot != _hotPulse) { _hotPulse = gpuHot; Pulse(TrailText, gpuHot); }
            GamePillExtras(); // FPS in front, the time played at the end (36a / 38)
        }
        else if (acts.Count > 0)
        {
            TrailText.FontSize = 13;
            var a = acts[0];
            var color = ActColor(a);
            switch (a)
            {
                case Act.Music:
                    // a different song while the closed island shows the music: the covers swap places (small moment).
                    // Every title change counts – the new picture is only waited for a moment (players often send the
                    // new title first and the picture later, browsers sometimes never send a new one)
                    if (_songTitle != null && H.Media.Title != _songTitle && !_songPending
                        && _state == State.Collapsed && S.Animations != AnimationLevel.Aus && coverWasShown && PillCover.ImageSource != null)
                        BeginSongSwap();
                    else if (_songTitle != null && H.Media.Title != _songTitle && iconWasShown && S.Animations != AnimationLevel.Aus && _state == State.Collapsed)
                        After(1, () => Pop(LeadIcon)); // no cover at all: the note symbol pops
                    _songTitle = H.Media.Title;
                    if (!_songPending) { PillCover.ImageSource = H.Media.Cover; _shownCoverHash = H.Media.CoverHash; }
                    if (H.Media.Cover != null || _songPending) LeadCover.Visibility = Visibility.Visible;
                    else ShowLeadIcon("music", musicColor);
                    // P1 (2026-11-18): paused, the bars stay as small dots beside the resting cover
                    Equalizer.Visibility = Visibility.Visible;
                    if (SleepActive) { SleepBadgeText.Text = SleepLeftText(); SleepBadge.Visibility = Visibility.Visible; } // sleep timer (19)
                    if (IsVideoNow) { Equalizer.Visibility = Visibility.Collapsed; ShowTrail(VideoLeft(), (Brush)FindResource("SubText")); } // a video: the time left (20)
                    break;
                case Act.Recording:
                    ShowLeadIcon("record", color);
                    ShowTrail(_recordingText, color);
                    Lead.ToolTip = L.T("Aufnahme läuft – klicken zum Beenden");
                    break;
                case Act.Live:
                {
                    var lead = H.Privacy.Lead;
                    if (lead == null) break;
                    // app icon on the left; on the right: red dot while sharing, the mic (white/blue – red and
                    // crossed out when muted in Discord, a click toggles it), a green dot for the camera
                    if (lead.Icon != null) { PillCover.ImageSource = lead.Icon; LeadCover.Visibility = Visibility.Visible; }
                    else ShowLeadIcon(lead.Mic ? "mic" : lead.Screen ? "display" : "camera", MicBrush);
                    var micApp = H.Privacy.Apps.FirstOrDefault(a => a.Mic);
                    DotScreen.Visibility = H.Privacy.AnyScreen ? Visibility.Visible : Visibility.Collapsed;
                    TrailIcon.Visibility = micApp != null ? Visibility.Visible : Visibility.Collapsed;
                    if (micApp != null)
                    {
                        TrailIcon.Kind = micApp.SelfMuted ? "mic.slash" : "mic";
                        TrailIcon.Foreground = micApp.SelfMuted ? (Brush)FindResource("Danger") : MicBrush;
                    }
                    TrailLiveText.Visibility = DotMic.Visibility = Visibility.Collapsed;
                    DotCam.Visibility = H.Privacy.AnyCamera ? Visibility.Visible : Visibility.Collapsed;
                    DotCam.Margin = new Thickness(TrailIcon.Visibility == Visibility.Visible ? 2 : 0, 0, 0, 0);
                    TrailLive.Visibility = Visibility.Visible;
                    var target = DiscordMuteTarget();
                    TrailLive.Cursor = target != null ? Cursors.Hand : null;
                    TrailLive.ToolTip = target == null ? null : target.SelfMuted ? L.T("In Discord stumm – klicken zum Entstummen") : L.T("Klicken: in Discord stummschalten");
                    Lead.ToolTip = string.Join("\n", H.Privacy.Apps.Select(a => $"{a.Name}: {a.Parts} · {a.Elapsed}"));
                    break;
                }
                case Act.Timer:
                    ShowLeadIcon(_focusRun != null ? "moon" : "timer", color);
                    if (_focusRun != null) FocusLead(); // "Fokus" and how many messages wait (1a)
                    ShowTrail(H.Timer.RemainingText, color);
                    _timerLead = true; // the progress runs around the rim (UpdateTimerRim)
                    break;
                case Act.Obs:
                {
                    // ● REC 12:04 (red) · LIVE 1:02:10 (green)
                    var red = (Brush)FindResource("Danger");
                    var green = (Brush)FindResource("Green");
                    ShowLeadIcon("record", H.Obs.Recording ? red : green);
                    LeadText.Text = H.Obs.Recording ? $"REC {H.Obs.RecordTime}" : $"LIVE {H.Obs.StreamTime}";
                    LeadText.Foreground = H.Obs.Recording ? red : green;
                    LeadText.Visibility = Visibility.Visible;
                    if (H.Obs.Recording && H.Obs.Streaming) { ShowTrail($"LIVE {H.Obs.StreamTime}", green); TrailText.FontSize = 11.5; }
                    break;
                }
                case Act.Steam:
                {
                    // the game being downloaded (its library icon) or the browser's file, and how far
                    var dl = Dl;
                    if (dl.Icon != null) { PillCover.ImageSource = dl.Icon; LeadCover.Visibility = Visibility.Visible; }
                    else ShowLeadIcon("download", SteamBrush);
                    var n = dl.Name ?? "";
                    LeadText.Text = n.Length > 16 ? n[..15] + "…" : n;
                    LeadText.Foreground = Brushes.White;
                    LeadText.Visibility = Visibility.Visible;
                    if (S.SideBubbles) ShowTrail(dl.Percent, SteamBrush); // the stack shows its ring instead – no numbers
                    Lead.ToolTip = string.Join(" · ", new[] { dl.Name, dl.Percent, dl.Eta, dl.Speed }.Where(x => !string.IsNullOrEmpty(x)));
                    break;
                }
                case Act.Agent:
                {
                    var ag = H.Agents.Primary;
                    bool done = !H.Agents.AnyRunning && !H.Agents.AnyWaiting;
                    var c = done ? (Brush)FindResource("Green") : color;
                    ShowLeadIcon(done ? "check" : "asterisk", c);
                    if (ag?.IsWaiting == true)
                    {
                        LeadText.Text = L.T("Claude wartet auf dich");
                        LeadText.Foreground = c;
                        LeadText.Visibility = Visibility.Visible;
                    }
                    else if (S.SideBubbles) ShowTrail(done ? L.T("fertig") : ag?.IsWaiting == true ? L.T("wartet") : ag?.HasPlan == true ? ag.Counter : ag?.Elapsed ?? "", c);
                    Lead.ToolTip = ag == null ? null : $"Claude Code · {ag.Project}\n{ag.Task}\n{ag.Step}";
                    break;
                }
                case Act.MicMuted:
                    ShowLeadIcon("mic.slash", color);
                    break;
                case Act.Queue:
                    ShowLeadIcon("stack", color);
                    ShowTrail(H.Clipboard.QueueCount.ToString(), color);
                    break;
                case Act.Alarm:
                    ShowLeadIcon("bell", color);
                    ShowTrail(H.Alarm.At?.ToString("HH:mm") ?? "", color);
                    break;
                case Act.Device when H.Sys.LowDevice is { } dev:
                    ShowLeadIcon(dev.Icon, color);
                    ShowTrail($"{dev.Level * 100:0} %", color);
                    break;
                case Act.Print:
                    ShowLeadIcon("doc", color);
                    ShowTrail(H.Sys.PrintText, color);
                    break;
            }
        }

        bool overlay = !game && PillOverlay(); // the eye break takes the closed island for 20 s (5a)
        if (overlay) playing = false;
        bool eqOn = playing && !game;
        if (eqOn != _eqRunning) { PageEq(eqOn); _eqRunning = eqOn; }
        if (eqOn) { if (!_barTimer.IsEnabled) _barTimer.Start(); }
        else StopBars();

        IslandShadow.Color = S.MusicGlow && eqOn && H.Media.CoverColor.A > 0 ? H.Media.CoverColor : Colors.Black;
        IslandShadow.Opacity = S.MusicGlow && eqOn ? 0.7 : (S.Shape == IslandShape.Notch ? 0.25 : 0.4);

        UpdateVisibility();
        _sideActs = game ? new List<Act>() : side;
        if (_state == State.Expanded) { UpdateBubble(null); return; }
        // a notice in the island: mic / monitor / Claude … stay visible as bubbles beside it
        if (_state != State.Collapsed) { ShowSideBubbles(); return; }

        if (S.SideBubbles)
        {
            UpdateRingStack(null);
            UpdateBubble(!game && acts.Count > 1 ? acts[1] : null, !game && acts.Count > 2 ? acts[2] : null);
        }
        else
        {
            // the ring stack in the island (default): everything after the lead – and the lead's own ring when it shows
            // no text of its own (Claude, a download: no numbers, the ring says how far)
            UpdateBubble(null);
            var rings = new List<Act>();
            if (!game && acts.Count > 0 && (acts[0] is Act.Steam || acts[0] is Act.Agent && !H.Agents.AnyWaiting)) rings.Add(acts[0]);
            if (!game) rings.AddRange(acts.Skip(1).Where(a => a != Act.Music));
            UpdateRingStack(rings);
        }

        UpdateEyes(!game && acts.Count == 0); // the little eyes sit on the left only while nothing else is there
        double w, h = (game ? 26 : PillH) + NotchExtra;
        if (game || acts.Count > 0 || overlay)
        {
            Lead.Measure(new Size(double.PositiveInfinity, 40));
            Trail.Measure(new Size(double.PositiveInfinity, 40));
            RingStack.Measure(new Size(double.PositiveInfinity, 40));
            double stack = RingStack.Visibility == Visibility.Visible ? RingStack.DesiredSize.Width + 8 : 0;
            w = Math.Max(S.Shape == IslandShape.Notch ? 200 : 196, 6 + Lead.DesiredSize.Width + TitleRoom(acts) + Trail.DesiredSize.Width + stack + 11);
            w = Math.Min(w, 380);
        }
        else w = S.Shape == IslandShape.Notch ? 200 : IdleW;

        if (force || Math.Abs(w - _targetW) > 1.5 || Math.Abs(h - _targetH) > 0.5)
            AnimateSize(w, h, (game ? 26 : PillH) / 2.0, force);
        UpdateTimerRim();
    }

    // ---------- bar chart in the closed island: follows the player's real loudness ----------

    private readonly AppVolume _meter = new();
    // 25 frames a second: every frame redraws the whole see-through window, and the bars are smoothed anyway
    private readonly DispatcherTimer _barTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };
    private ScaleTransform[] _eqScales;
    private readonly double[] _bars = { 0.2, 0.2, 0.2, 0.2, 0.2 };
    private static readonly double[] BarGain = { 0.72, 1.0, 0.86, 0.95, 0.66 };
    private readonly Random _barRnd = new();
    private DateTime _meterAt = DateTime.MinValue;
    private string _meterApp;
    private NAudio.CoreAudioApi.MMDeviceEnumerator _meterEnum;

    private static string ProcessForApp(string app) => app switch
    {
        "Spotify" => "Spotify",
        "Google Chrome" => "chrome",
        "Microsoft Edge" => "msedge",
        "Apple Music" => "AppleMusic",
        "Firefox" => "firefox",
        _ => app ?? ""
    };

    private readonly SpectrumAnalyzer _spectrum = new();

    private void BarTick()
    {
        if (_state != State.Collapsed || Equalizer.Visibility != Visibility.Visible) return;
        var scalesNow = _eqScales ??= new[] { Eq1, Eq2, Eq3, Eq4, Eq5 };
        // real frequency bands: bass left … treble right
        if (!_spectrum.Running) _spectrum.Start();
        if (_spectrum.Running)
        {
            var lv = _spectrum.Levels();
            CoverBeat(lv[0]); // the small cover breathes with the bass
            for (int i = 0; i < 5; i++)
            {
                double target = Math.Max(0.14, Math.Pow(lv[i], 1.4));
                _bars[i] += (target - _bars[i]) * (target > _bars[i] ? 0.75 : 0.32); // snappy up, quick but smooth down
                scalesNow[i].ScaleY = Math.Clamp(_bars[i], 0.12, 1);
            }
            return;
        }
        if ((DateTime.Now - _meterAt).TotalSeconds > 3 || _meterApp != H.Media.AppName)
        {
            _meterApp = H.Media.AppName;
            _meter.Attach(ProcessForApp(_meterApp));
            _meterAt = DateTime.Now;
        }
        double peak = 0;
        if (_meter.Available) peak = _meter.Peak;
        else
        {
            // unknown player: the loudness of the default output device
            try
            {
                _meterEnum ??= new NAudio.CoreAudioApi.MMDeviceEnumerator();
                using var dev = _meterEnum.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
                peak = dev.AudioMeterInformation.MasterPeakValue;
            }
            catch { }
        }
        double level = Math.Pow(Math.Clamp(peak, 0, 1), 0.55);
        var scales = scalesNow;
        for (int i = 0; i < 5; i++)
        {
            double target = Math.Max(0.16, level * BarGain[i] * (0.55 + 0.45 * _barRnd.NextDouble()));
            _bars[i] += (target - _bars[i]) * (target > _bars[i] ? 0.6 : 0.22); // quick up, gentle down
            scales[i].ScaleY = Math.Clamp(_bars[i], 0.12, 1);
        }
    }

    /// <summary>The small bars on the Musik page: they move only while that page is on screen.</summary>
    private void PageEq(bool on)
    {
        var bars = new (UIElement Host, ScaleTransform Scale, double From, double To, int Ms)[]
        {
            (MEqBar1, MEq1, 0.3, 1, 420), (MEqBar2, MEq2, 0.95, 0.35, 350), (MEqBar3, MEq3, 0.45, 1, 500), (MEqBar4, MEq4, 1, 0.4, 380)
        };
        foreach (var (host, scale, from, to, ms) in bars)
        {
            if (on) Calm.Loop(host, scale, ScaleTransform.ScaleYProperty, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            else Calm.Stop(host, scale, ScaleTransform.ScaleYProperty);
        }
    }

    private void StopBars()
    {
        if (!_barTimer.IsEnabled) return;
        _barTimer.Stop();
        _spectrum.Stop();
        _meter.Release();
        _meterAt = DateTime.MinValue;
        foreach (var s in new[] { Eq1, Eq2, Eq3, Eq4, Eq5 }) s.ScaleY = 0.2; // paused: low and still
        for (int i = 0; i < 5; i++) _bars[i] = 0.2;
        CoverBeatStop();
    }

    private void ShowLeadIcon(string icon, Brush color)
    {
        LeadIcon.Kind = icon;
        LeadIcon.Foreground = color;
        LeadIcon.Visibility = Visibility.Visible;
    }

    private void ShowTrail(string text, Brush color)
    {
        TrailText.Text = text;
        TrailText.Foreground = color;
        TrailText.Visibility = Visibility.Visible;
    }

    private Brush ActColor(Act a) => a switch
    {
        Act.Live => H.Privacy.Lead?.Accent ?? (Brush)FindResource("Orange"),
        Act.Timer or Act.Alarm => (Brush)FindResource("Orange"),
        Act.Agent => AgentBrush,
        Act.Obs => H.Obs.Recording ? (Brush)FindResource("Danger") : (Brush)FindResource("Green"),
        Act.Steam => SteamBrush,
        Act.MicMuted => (Brush)FindResource("Danger"),
        Act.Recording => (Brush)FindResource("Danger"),
        Act.Queue => (Brush)FindResource("Accent"),
        Act.Device => (Brush)FindResource("Danger"),
        Act.Print => (Brush)FindResource("Blue"),
        _ => MusicTint()
    };

    private Brush MusicTint()
    {
        var c = H.Media.CoverColor;
        if (c.A == 0) return Brushes.White;
        double max = Math.Max(c.R, Math.Max(c.G, c.B));
        double k = max < 1 ? 1 : Math.Min(2.6, 235.0 / max);
        var lit = Color.FromRgb((byte)Math.Min(255, c.R * k + 25), (byte)Math.Min(255, c.G * k + 25), (byte)Math.Min(255, c.B * k + 25));
        var b = new SolidColorBrush(lit);
        b.Freeze();
        return b;
    }

    private static readonly SolidColorBrush AgentBrush = new(Color.FromRgb(0xE8, 0x87, 0x5B));

    /// <summary>Up to two small bubbles to the right of the closed island.</summary>
    private List<Act> _sideActs = new();

    /// <summary>While a notice is shown: the side indicators (mic, monitor, Claude, OBS, Steam) as bubbles next to it.</summary>
    private void ShowSideBubbles()
    {
        if (!S.SideBubbles) { UpdateBubble(null); return; } // ring stack: no circles beside a notice either
        ShowSideBubblesNow();
    }

    private void ShowSideBubblesNow() =>
        UpdateBubble(_sideActs.Count > 0 ? _sideActs[0] : null, _sideActs.Count > 1 ? _sideActs[1] : null);

    /// <summary>Only for the motion test: the bubbles keep what the test put there.</summary>
    private bool _freezeBubbles;

    private void UpdateBubble(Act? a, Act? b = null)
    {
        if (_freezeBubbles) return;
        bool show = _state != State.Expanded && Island.Visibility == Visibility.Visible;
        FillBubble(Bubble, BubbleIcon, BubbleCover, BubbleCoverImg, BubbleRing, BubbleDot, show ? a : null);
        FillBubble(Bubble2, Bubble2Icon, Bubble2Cover, Bubble2CoverImg, Bubble2Ring, Bubble2Dot, show && a != null ? b : null);
        StartMotion(); // a capsule bubble may have changed its width
    }

    /// <summary>White (or blue, see settings) while an app listens; red + crossed out when muted in Discord.</summary>
    private Brush MicBrush => S.MicIndicatorBlue ? (Brush)FindResource("Blue") : Brushes.White;

    /// <summary>The Discord call whose mute a click on the mic toggles (only with the Discord connection).</summary>
    private static LiveApp DiscordMuteTarget() =>
        H.Discord.Connected ? H.Privacy.Apps.FirstOrDefault(a => a.IsDiscord && a.Mic) : null;

    private void TrailLive_Click(object sender, MouseButtonEventArgs e)
    {
        if (_state != State.Collapsed || DiscordMuteTarget() is not { } app) return; // otherwise the click opens the island
        e.Handled = true;
        MuteLiveApp(app);
    }

    private void BubbleClicked(Border bubble)
    {
        // a call (an app on the microphone): the click mutes – it never opens the Live page (user wish 2026-11-17).
        // Discord connected → muted in Discord, else the Windows microphone. The red "muted" bubble unmutes the same way.
        if (bubble.Tag is Act.Live or Act.MicMuted)
        {
            var app = DiscordMuteTarget() ?? H.Privacy.Apps.FirstOrDefault(a => a.Mic);
            if (app != null) { MuteLiveApp(app); UpdateLiveUi(); return; }
            if (bubble.Tag is Act.MicMuted) { ToggleMic(); return; }
        }
        Expand(BubbleTab(bubble), true);
    }

    /// <summary>
    /// The page a bubble beside the island belongs to (user wish 2026-11-17: a click on a bubble opens its own
    /// page – Claude → Agent, an app → Live; hovering does nothing). A new kind of bubble gets its page here;
    /// null = the usual choice of page. A page that is switched off or not there right now falls back to that.
    /// </summary>
    private string BubbleTab(Border bubble)
    {
        string tab = bubble.Tag switch
        {
            Act.Live or Act.Steam => "Live",
            Act.MicMuted => H.Privacy.InUse ? "Live" : "Controls",
            Act.Agent => "Agent",
            Act.Obs => "Obs",
            Act.Timer or Act.Alarm => L.T("Timer"),
            Act.Music => "Music",
            Act.Queue => "Clip",
            Act.Device => "Controls",
            _ => null, // GIF recording: no page of its own
        };
        if (tab == null || IsTabHidden(tab)) return null;
        if (_tabBtns != null && _tabBtns.TryGetValue(tab, out var rb) && rb.Visibility != Visibility.Visible) return null;
        return tab;
    }

    /// <summary>The bubble's ring is Claude's turning spinner or a still progress ring (Steam).</summary>
    private static void StyleRing(Ring ring, bool spin, double value, double size, Brush stroke, Color track)
    {
        ring.Value = value;
        ring.Width = ring.Height = size;
        ring.Thickness = size > 18 ? 2.5 : 2;
        ring.Stroke = stroke;
        ring.Track = new SolidColorBrush(track);
        if (ring.RenderTransform is not RotateTransform rt) return;
        bool spinning = Equals(ring.Tag, "spin");
        if (spin && !spinning)
        {
            Calm.Loop(ring, rt, RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever });
            ring.Tag = "spin";
        }
        else if (!spin && spinning || !spin && ring.Tag == null)
        {
            Calm.Stop(ring, rt, RotateTransform.AngleProperty);
            rt.Angle = 0;
            ring.Tag = "still";
        }
    }

    // ---------- the side bubbles (approved mockup 2026-11-14): one language for everything next to the island ----------
    //  rings = progress (Claude's plan, Steam, timer) · colour = meaning (orange mic in use, red muted / recording,
    //  light blue screen sharing, red LIVE stream, Claude orange, green done)

    private static readonly SolidColorBrush ShareBrush = Frozen(Color.FromRgb(0x64, 0xD2, 0xFF));
    private static readonly SolidColorBrush MutedGround = Frozen(Color.FromRgb(0x2A, 0x0B, 0x0A));
    private DateTime _agentDoneUntil;

    private sealed class BubbleExtras { public TextBlock Text; public Ellipse Badge, Rec; public StackPanel Wave; }

    // ---------- the voice wave (user's pick 2026-11-15, "B · Hört zu"): orange bars that follow the real mic level ----------

    private readonly List<(FrameworkElement Host, ScaleTransform[] Bars)> _waves = new();
    private DispatcherTimer _waveTimer;
    private double _micLevel;

    /// <summary>Fills a panel with small orange bars; they only move while the waves are visible and someone speaks.</summary>
    private StackPanel MakeWave(StackPanel host, int bars, double height, double width)
    {
        var scales = new ScaleTransform[bars];
        for (int i = 0; i < bars; i++)
        {
            scales[i] = new ScaleTransform(1, 0.25);
            var r = new System.Windows.Shapes.Rectangle { Width = width, Height = height, RadiusX = width / 2, RadiusY = width / 2, Margin = new Thickness(i == 0 ? 0 : 1.6, 0, 0, 0),
                                    RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = scales[i], VerticalAlignment = VerticalAlignment.Center };
            r.SetResourceReference(Shape.FillProperty, "Orange");
            host.Children.Add(r);
        }
        _waves.Add((host, scales));
        return host;
    }

    private void StartWaves()
    {
        if (_waveTimer == null)
        {
            _waveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _waveTimer.Tick += (_, _) => WaveTick();
        }
        if (!_waveTimer.IsEnabled) _waveTimer.Start();
    }

    private void WaveTick()
    {
        if (!_waves.Any(w => w.Host.IsVisible)) { _waveTimer.Stop(); return; }
        // rises fast, falls a little slower – like a level meter
        double raw = Math.Clamp(Math.Sqrt(H.Audio.MicLevel()) * 1.4, 0, 1);
        _micLevel = raw > _micLevel ? raw : _micLevel * 0.78 + raw * 0.22;
        double t = Environment.TickCount64 / 1000.0;
        double[] shape = { 0.55, 1, 0.75, 0.9, 0.5 };
        foreach (var (host, bars) in _waves)
        {
            if (!host.IsVisible) continue;
            for (int i = 0; i < bars.Length; i++)
            {
                // a little life per bar so it looks like a voice, flat (0.25) when silent
                double jitter = 0.75 + 0.25 * Math.Sin(t * (7 + i * 2.3) + i);
                bars[i].ScaleY = Math.Clamp(0.25 + _micLevel * shape[i % shape.Length] * jitter, 0.25, 1);
            }
        }
    }
    private readonly Dictionary<Border, BubbleExtras> _bubbleExtras = new();

    /// <summary>A small text (LIVE, timer), a corner dot ("you're up") and a recording dot – added once per bubble.</summary>
    private BubbleExtras Extras(Border b)
    {
        if (_bubbleExtras.TryGetValue(b, out var x)) return x;
        x = new BubbleExtras
        {
            Text = new TextBlock { FontSize = 9, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false },
            Badge = new Ellipse { Width = 9, Height = 9, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 0, 0), Stroke = Brushes.Black, StrokeThickness = 1.6, IsHitTestVisible = false },
            Rec = new Ellipse { Width = 10, Height = 10, IsHitTestVisible = false }
        };
        x.Rec.SetResourceReference(Shape.FillProperty, "Danger");
        x.Wave = MakeWave(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed, IsHitTestVisible = false }, 4, 14, 2.5);
        var g = Inner(b);
        g.Children.Add(x.Wave);
        g.Children.Add(x.Rec);
        g.Children.Add(x.Text);
        g.Children.Add(x.Badge);
        _bubbleExtras[b] = x;
        return x;
    }

    private static Brush Tint(Brush b, double alpha)
    {
        var c = (b as SolidColorBrush)?.Color ?? Colors.White;
        var t = new SolidColorBrush(Color.FromArgb((byte)(alpha * 255), c.R, c.G, c.B));
        t.Freeze();
        return t;
    }

    private static void Pulse(UIElement e, bool on, double min = 0.4)
    {
        if (!on) { Calm.Stop(e, e, OpacityProperty); e.Opacity = 1; return; }
        if (Calm.Running(e, e, OpacityProperty)) return; // already pulsing – the bubble is refilled every half second
        Calm.Loop(e, e, OpacityProperty, new DoubleAnimation(1, min, TimeSpan.FromMilliseconds(650))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
    }

    /// <summary>Claude's star turns slowly while it works.</summary>
    private static void SpinSlow(FrameworkElement e, bool on)
    {
        if (!on && e.RenderTransform is not RotateTransform) return; // nothing turning (and a running pop stays untouched)
        if (e.RenderTransform is not RotateTransform rt)
        {
            rt = new RotateTransform();
            e.RenderTransform = rt;
            e.RenderTransformOrigin = new Point(0.5, 0.5);
        }
        bool spinning = Calm.Running(e, rt, RotateTransform.AngleProperty);
        if (on && !spinning) Calm.Loop(e, rt, RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(3.2)) { RepeatBehavior = RepeatBehavior.Forever });
        else if (!on && spinning) { Calm.Stop(e, rt, RotateTransform.AngleProperty); rt.Angle = 0; }
    }

    private static string ShortRemaining(string t)
    {
        // "04:59" → "4:59", "1:29:59" → "1h29" (fits inside the small ring)
        var p = t.Split(':');
        if (p.Length == 3 && int.TryParse(p[0], out var h)) return $"{h}h{p[1]}";
        return p.Length == 2 && int.TryParse(p[0], out var m) ? $"{m}:{p[1]}" : t;
    }

    private void FillBubble(Border bubble, SfIcon icon, Border cover, ImageBrush coverImg, Ring ring, SfIcon screen, Act? a)
    {
        bubble.Tag = a;
        // nothing to show: the bubble keeps its look while the island swallows it
        if (a == null) { SetBubbleWant(bubble, false); return; }
        var x = Extras(bubble);
        var img = bubble == Bubble ? BubbleImg : Bubble2Img;

        // start from a plain round bubble
        screen.Visibility = img.Visibility = cover.Visibility = ring.Visibility = Visibility.Collapsed;
        x.Text.Visibility = x.Badge.Visibility = x.Rec.Visibility = x.Wave.Visibility = Visibility.Collapsed;
        x.Text.HorizontalAlignment = x.Rec.HorizontalAlignment = HorizontalAlignment.Center;
        x.Text.Margin = x.Rec.Margin = new Thickness(0);
        x.Rec.Width = x.Rec.Height = 10;
        bubble.Width = 32;
        bubble.SetResourceReference(Border.BackgroundProperty, "IslandBubbleBackground");
        bubble.ToolTip = null;
        bubble.Cursor = Cursors.Hand;
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.Margin = new Thickness(0);
        icon.Visibility = Visibility.Visible;
        icon.Size = 15;
        if (!(_phoneTipping && a == Act.Live)) icon.Foreground = ActColor(a.Value); // hanging up: the phone stays red
        Pulse(icon, false);
        SpinSlow(icon, false);
        SetBubbleWant(bubble, true);

        var red = (Brush)FindResource("Danger");
        var green = (Brush)FindResource("Green");
        var orange = (Brush)FindResource("Orange");
        switch (a.Value)
        {
            case Act.Live:
            {
                var mics = H.Privacy.Apps.Where(m => m.Mic).ToList();
                bool muted = mics.Count > 0 && mics.All(m => m.SelfMuted);
                bool call = mics.Any(m => m.IsDiscord) || H.Discord.InVoice;
                if (mics.Count > 0)
                {
                    // muted: red mic on a dark red ground · in a call: a green phone like the iPhone call ·
                    // any other app using the mic: an orange mic like Apple's privacy dot
                    icon.Kind = muted ? "mic.slash" : call ? "phone" : "mic";
                    if (!_phoneTipping) icon.Foreground = muted ? red : call ? green : orange; // hanging up: stays red
                    if (muted) bubble.Background = MutedGround;
                    // an app listens (e.g. Claude's voice mode): the orange wave instead of the mic symbol
                    if (!muted && !call && !H.Privacy.AnyScreen)
                    {
                        icon.Visibility = Visibility.Collapsed;
                        x.Wave.Visibility = Visibility.Visible;
                        StartWaves();
                    }
                }
                else if (H.Privacy.AnyCamera) { icon.Kind = "camera"; icon.Foreground = green; }
                else icon.Visibility = Visibility.Collapsed;
                if (H.Privacy.AnyScreen)
                {
                    screen.Kind = "screen.share";
                    screen.Foreground = ShareBrush;
                    screen.Visibility = Visibility.Visible;
                    bool both = icon.Visibility == Visibility.Visible;
                    // both → a little capsule: [mic][sharing]
                    bubble.Width = both ? 54 : 32;
                    icon.HorizontalAlignment = both ? HorizontalAlignment.Left : HorizontalAlignment.Center;
                    icon.Margin = both ? new Thickness(10, 0, 0, 0) : new Thickness(0);
                    screen.HorizontalAlignment = both ? HorizontalAlignment.Right : HorizontalAlignment.Center;
                    screen.Margin = both ? new Thickness(0, 0, 10, 0) : new Thickness(0);
                }
                bubble.ToolTip = DiscordMuteTarget() is { } t ? (t.SelfMuted ? L.T("In Discord stumm – klicken zum Entstummen") : L.T("Klicken: in Discord stummschalten")) : null;
                break;
            }
            case Act.Obs:
                icon.Visibility = Visibility.Collapsed;
                x.Rec.Visibility = Visibility.Visible;
                Pulse(x.Rec, true, 0.35);
                if (H.Obs.Streaming)
                {
                    // ● LIVE – unmistakably a stream, not just sharing the screen
                    bubble.Width = 52;
                    x.Rec.Width = x.Rec.Height = 6;
                    x.Rec.HorizontalAlignment = HorizontalAlignment.Left;
                    x.Rec.Margin = new Thickness(10, 0, 0, 0);
                    x.Text.Text = "LIVE";
                    x.Text.FontSize = 9.5;
                    x.Text.Foreground = red;
                    x.Text.HorizontalAlignment = HorizontalAlignment.Right;
                    x.Text.Margin = new Thickness(0, 0, 9, 0);
                    x.Text.Visibility = Visibility.Visible;
                }
                bubble.ToolTip = H.Obs.Streaming ? L.T($"OBS live · {H.Obs.StreamTime}") : L.T($"OBS nimmt auf · {H.Obs.RecordTime}");
                break;
            case Act.Agent:
            {
                var ag = H.Agents.Primary;
                var track = Color.FromArgb(0x40, 0xE8, 0x87, 0x5B);
                ring.Visibility = Visibility.Visible;
                if (!H.Agents.AnyRunning && !H.Agents.AnyWaiting)
                {
                    // done: the ring closes in green with a check, then the bubble disappears into the island
                    StyleRing(ring, spin: false, value: 100, size: 26, stroke: green, track: Color.FromArgb(0x40, 0x30, 0xD1, 0x58));
                    icon.Kind = "check";
                    icon.Foreground = green;
                    icon.Size = 13;
                    bubble.ToolTip = ag == null ? L.T("Claude ist fertig") : L.T($"Claude ist fertig · {ag.Elapsed}");
                    break;
                }
                icon.Kind = "asterisk";
                icon.Foreground = AgentBrush;
                icon.Size = 12;
                if (H.Agents.AnyWaiting)
                {
                    // waiting for you: the same bubble, the star pulses and a dot says "your turn"
                    StyleRing(ring, spin: false, value: 100, size: 26, stroke: AgentBrush, track: track);
                    Pulse(icon, true);
                    x.Badge.Fill = AgentBrush;
                    x.Badge.Visibility = Visibility.Visible;
                }
                else
                {
                    // working: the ring is the plan's progress (3 of 5 …); without a plan it turns
                    bool plan = ag?.HasPlan == true;
                    StyleRing(ring, spin: !plan, value: plan ? Math.Max(4, ag.Progress * 100) : 28, size: 26, stroke: AgentBrush, track: track);
                    SpinSlow(icon, true);
                }
                bubble.ToolTip = ag == null ? null : $"Claude · {ag.Elapsed}{(ag.HasPlan ? $" · {ag.Counter}" : "")}\n{ag.Step}";
                break;
            }
            case Act.Steam when !AnyDl && !_steamFinishing:
                // just finished: the ring is closed in green with a check (for a few seconds)
                StyleRing(ring, spin: false, value: 100, size: 26, stroke: green, track: Color.FromArgb(0x40, 0x30, 0xD1, 0x58));
                ring.Visibility = Visibility.Visible;
                icon.Kind = "check";
                icon.Foreground = green;
                icon.Size = 12;
                bubble.ToolTip = L.T($"{Dl.Name} ist fertig");
                break;
            case Act.Steam:
            {
                var dl = Dl;
                // a browser download without a known size: the ring turns slowly instead of filling
                bool unknown = dl == H.Downloads && dl.Downloading && !H.Downloads.HasTotal;
                StyleRing(ring, spin: unknown, value: unknown ? 28 : dl.Downloading ? dl.Progress * 100 : _steamLastProgress * 100, size: 26, stroke: SteamBrush, track: Color.FromArgb(0x40, 0x66, 0xC0, 0xF4));
                ring.Visibility = Visibility.Visible;
                icon.Kind = "arrow.down";
                icon.Foreground = SteamBrush;
                icon.Size = 11;
                bubble.ToolTip = string.Join(" · ", new[] { dl.Name, dl.Percent, dl.Eta, dl.Speed }.Where(x => !string.IsNullOrEmpty(x)));
                break;
            }
            case Act.Timer:
                // the ring runs down, the time left sits inside
                StyleRing(ring, spin: false, value: H.Timer.RemainingPercent, size: 26, stroke: orange, track: Color.FromArgb(0x40, 0xFF, 0x9F, 0x0A));
                ring.Visibility = Visibility.Visible;
                icon.Visibility = Visibility.Collapsed;
                x.Text.Text = ShortRemaining(H.Timer.RemainingText);
                x.Text.FontSize = 8.5;
                x.Text.Foreground = orange;
                x.Text.Visibility = Visibility.Visible;
                bubble.ToolTip = L.T($"Timer · {H.Timer.RemainingText}");
                break;
            case Act.Recording:
                icon.Visibility = Visibility.Collapsed;
                x.Rec.Visibility = Visibility.Visible;
                Pulse(x.Rec, true, 0.35);
                break;
            case Act.MicMuted:
                icon.Kind = "mic.slash";
                icon.Foreground = red;
                bubble.Background = MutedGround;
                break;
            case Act.Queue:
                icon.Kind = "stack";
                break;
            case Act.Alarm:
                icon.Kind = "bell";
                bubble.ToolTip = L.T($"Wecker {H.Alarm.At:HH:mm} · {H.Alarm.UntilText}");
                break;
            default: // music
                icon.Kind = "music";
                if (H.Media.Cover != null)
                {
                    coverImg.ImageSource = H.Media.Cover;
                    cover.Visibility = Visibility.Visible;
                    icon.Visibility = Visibility.Collapsed;
                }
                break;
        }
    }

    // =====================================================================
    //  Mini hints – the newest one always replaces the current one
    // =====================================================================

    /// <summary>
    /// Small status message inside the pill (icon left, one word right). With <paramref name="check"/> the word
    /// shrinks away into a drawn check mark, which then fades out as well.
    /// </summary>
    public void ShowHint(string icon, string text, Brush color = null, bool check = false, string key = null,
                         IList<(string Icon, string Tip, Brush Hover, Action Act)> actions = null)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => ShowHint(icon, text, color, check, key, actions))); return; }
        if (_captureHidden || _state == State.Peek) return;
        if (_moveMode && key != "move") return; // moving the island: its own notice stays
        if (_tucked && _state != State.Expanded) return; // tucked away: small status hints stay quiet
        if (_state == State.Expanded && (IsActive || IsCursorOverIsland(0))) return;
        if (_state == State.Expanded) Collapse();

        int gen = ++_hintGen;
        _state = State.Hint;
        ShowSideBubbles();
        UpdateVisibility();
        color ??= Brushes.White;

        HintIcon.BeginAnimation(OpacityProperty, null);
        HintIcon.Opacity = 1;
        HintIcon.Visibility = Visibility.Visible;
        HintWave.Visibility = Visibility.Collapsed;
        SongWave.Visibility = Visibility.Collapsed;
        HintLevel.Visibility = Visibility.Collapsed; // only ShowLevel (volume / brightness) turns the bar on
        HintRoll.Visibility = Visibility.Collapsed;  // … and its rolling number
        TypingFadeOff(); // a notice is fully there, even while you type
        LiquidBoltOff();
        HintText.MinWidth = 0;
        HintText.TextAlignment = TextAlignment.Left;
        HintActions.Margin = new Thickness(8, 0, -6, 0);
        HintIcon.Kind = icon;
        HintIcon.Foreground = color;
        HintIconCircle.Background = Tint(color, 0.18);
        SlashMoment(icon, key, color);
        HintText.Text = text;
        // the circle carries the colour; only warnings (red, e.g. "Stumm") keep a coloured word – like Apple's silent switch
        HintText.Foreground = ReferenceEquals(color, FindResource("Danger")) ? color : Brushes.White;
        HintText.BeginAnimation(OpacityProperty, null);
        HintText.Opacity = 1;
        HintTextScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        HintTextScale.ScaleX = 1;
        HintCheck.BeginAnimation(OpacityProperty, null);
        HintCheck.Opacity = 0;
        HintCheck.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
        HintCheck.StrokeDashOffset = 8;

        ShowPanel(HintPanel); // no-op when the hint is already the current content (a fading-out hint comes back)
        var pop = new DoubleAnimation(0.6, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = new ElasticEase { Oscillations = 1, Springiness = 5 } };
        HintIconScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        HintIconScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);

        // optional round glass buttons on the right (e.g. mute / open the app)
        HintActions.Children.Clear();
        bool withActions = actions is { Count: > 0 };
        _hintHasActions = withActions;
        HintActions.Visibility = withActions ? Visibility.Visible : Visibility.Collapsed;
        HintText.FontSize = withActions ? 12 : 13;
        if (withActions) HintText.Foreground = Brushes.White; // only the icon carries the colour
        if (withActions)
        {
            foreach (var (aIcon, tip, hover, act) in actions)
            {
                var b = new Button { ToolTip = tip, Margin = new Thickness(6, 0, 0, 0) };
                if (aIcon == null)
                {
                    // text button (e.g. the blue "Aktivieren")
                    b.SetResourceReference(StyleProperty, "AccentButton");
                    b.Content = tip;
                    b.ToolTip = null;
                    b.Height = 28;
                    b.Padding = new Thickness(12, 0, 12, 0);
                }
                else b.SetResourceReference(StyleProperty, "GlassIcon");
                Ext.SetIcon(b, aIcon);
                if (hover != null) Ext.SetHover(b, hover);
                var a = act;
                b.Click += (_, e) => { e.Handled = true; _hintGen++; EndHint(); a?.Invoke(); };
                HintActions.Children.Add(b);
            }
        }

        HintText.Measure(new Size(double.PositiveInfinity, 40));
        // a short notice keeps the resting width (176) – the island does not jump wider for "Kopiert"
        double w = withActions
            ? Math.Min(10 + 22 + 12 + HintText.DesiredSize.Width + 8 + actions.Sum(a => a.Icon == null ? 92 : 34) + 6, 400)
            : Math.Clamp(10 + 22 + 36 + HintText.DesiredSize.Width + 14, S.Shape == IslandShape.Notch ? 200 : IdleW, 330);
        double h = withActions ? 40 : PillH;
        AnimateSize(w, h + NotchExtra, h / 2, true);

        _ = RunHint(gen, check, withActions);
    }

    private async Task RunHint(int gen, bool check, bool withActions = false)
    {
        int hold = _hintHoldMs; // a moment that wants to stay longer (headphones, charger) – used once
        _hintHoldMs = 0;
        await Task.Delay(check ? 650 : hold > 0 ? hold : withActions ? 5000 : 1500);
        if (gen != _hintGen) return;
        // a hint with buttons stays while the pointer rests on it
        while (withActions && IsCursorOverIsland(4))
        {
            await Task.Delay(300);
            if (gen != _hintGen) return;
        }
        if (check)
        {
            // the word shrinks away into the check mark …
            var dur = TimeSpan.FromMilliseconds(170);
            HintText.BeginAnimation(OpacityProperty, new DoubleAnimation(0, dur));
            HintTextScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.5, dur) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
            AnimateSize(S.Shape == IslandShape.Notch ? 200 : IdleW, PillH + NotchExtra, PillH / 2, true);
            await Task.Delay(140);
            if (gen != _hintGen) return;
            HintCheck.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(80)));
            HintCheck.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(300)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            var spring = new DoubleAnimation(0.5, 0.78, TimeSpan.FromMilliseconds(450)) { EasingFunction = new ElasticEase { Oscillations = 1, Springiness = 4 } };
            HintCheckScale.BeginAnimation(ScaleTransform.ScaleXProperty, spring);
            HintCheckScale.BeginAnimation(ScaleTransform.ScaleYProperty, spring);
            await Task.Delay(750);
            if (gen != _hintGen) return;
            // … and then the check mark disappears as well
            HintCheck.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(200)));
            HintIcon.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(200)));
            await Task.Delay(180);
            if (gen != _hintGen) return;
        }
        EndHint();
    }

    private void EndHint()
    {
        if (_state != State.Hint) return;
        _state = State.Collapsed;
        if (_peeks.Count > 0) { ShowNextPeek(); return; }
        ShowPanel(PillPanel);
        UpdatePill(true);
    }

    private void OnCaptured(ClipItem item, bool byUser)
    {
        // a screenshot: the island opens downwards, shows it and takes it in (small moment, user wish 2026-11-14)
        if (item.IsScreenshot && !byUser && S.Animations != AnimationLevel.Aus && (item.LargeImage ?? item.Thumb) is { } shot)
        {
            ShowPeek(new PeekInfo { Key = "shot", Shot = shot, TimeoutMs = 0 });
            return;
        }
        // the copied words fly from the pointer into the island (2026-11-17)
        if (byUser && item.Kind is ClipKind.Text or ClipKind.Link) FlyCopy(item.Text);
        if (!byUser || !S.ConfirmCopy) return;
        if (H.Clipboard.QueueMode)
            ShowHint("stack", $"{H.Clipboard.QueueCount} im Stapel", (Brush)FindResource("Accent"), key: "queue");
        else
        {
            ShowHint("check", "Kopiert", (Brush)FindResource("Green"), key: "copy");
            // the island gives a little and the check slides in from the left
            if (S.Animations != AnimationLevel.Aus && _state == State.Hint)
            {
                Squash();
                var slide = new TranslateTransform();
                HintIconCircle.RenderTransform = slide;
                slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-18, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut } });
                HintIconCircle.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { FillBehavior = FillBehavior.Stop });
            }
        }
    }

    // =====================================================================
    //  Alerts (reminders, timer, warnings) – compact, max two glass buttons
    // =====================================================================

    /// <summary>
    /// The plain notices (timer, alarm, reminder, price, call, GIF, missing shortcut …) – user's pick 2026-11-15: first
    /// narrow in the island like the message ticker, the pointer on it opens the card in the message look (a source
    /// line, a coloured square, round buttons). The ones with their own look (messages, Claude, Steam, privacy,
    /// screenshot) stay as they are.
    /// </summary>
    private static void Plainify(PeekInfo p)
    {
        if (p.Shot != null || p.Source != null || p.Ring != null || p.Avatar != null || p.Ticker) return;
        p.Ticker = true;
        string src = p.Key switch
        {
            "timer" => L.T("Timer"), "alarm" => L.T("Wecker"), "call" => L.T("Anruf"), "track" => L.T("Musik"), "live" => "Live",
            { } k when k.StartsWith("rem:") => L.T("Erinnerung"),
            { } k when k.StartsWith("price:") => L.T("Preisalarm"),
            _ => p.Icon == "record" ? L.T("Aufnahme") : "PeakPill"
        };
        p.Source = L.T($"{src} · jetzt");
        p.SourceIcon = p.Icon;
        p.SourceBrush = p.GlyphBrush;
        if (p.Key == "track") p.Cover = p.Image != null;
    }

    public void ShowPeek(PeekInfo p)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => ShowPeek(p))); return; }
        Plainify(p);
        if (_tucked && !p.Important && _state != State.Expanded) return; // only important alerts get through while tucked
        if (p.Key != null)
        {
            _peeks.RemoveAll(x => x.Key == p.Key);
            if (_state == State.Peek && _peek?.Key == p.Key) { _peeks.Insert(0, p); _peek = null; ShowNextPeek(); return; }
        }
        _peeks.Add(p);
        if (_state is State.Collapsed or State.Hint) { _hintGen++; ShowNextPeek(); }
    }

    private static readonly string[] RoundLabels = { "Ablehnen", "Annehmen", "Später", "Nochmal", "Erledigt", "OK", "Aus", "Öffnen", "Chat öffnen", "Einstellungen", "Zeigen" };

    private void ShowNextPeek()
    {
        if (_peeks.Count == 0)
        {
            _state = State.Collapsed;
            _peek = null;
            ShowPanel(PillPanel);
            UpdatePill(true);
            return;
        }
        _peek = _peeks[0];
        _peeks.RemoveAt(0);
        DropEyesNow(); // a card opens – the eyes go at once, like when the island opens
        _state = State.Peek;
        UpdateVisibility();
        PeekProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        PeekProgress.Visibility = Visibility.Collapsed;
        if (_peek.Key == "agentask") { ShowAskCard(); return; } // Claude asks for a permission: its own look (C3)
        if (_peek.Key == "query") { ShowQueryCard(); return; } // quick search / calculator (42 / 46)
        if (_peek.Rows.Count > 0) { ShowMomentCard(); return; }  // focus done, good morning, evening (2026-11-18)
        if (_peek.Ticker && !_peek.Open) { ShowTicker(); return; }

        // screenshot moment: the island opens downwards with the picture, then it shrinks into the island
        bool shot = _peek.Shot != null;
        PeekShot.Visibility = shot ? Visibility.Visible : Visibility.Collapsed;
        PeekLead.Visibility = PeekBody.Visibility = shot ? Visibility.Collapsed : Visibility.Visible;
        if (shot)
        {
            PeekActions.Children.Clear();
            PeekActionsRight.Children.Clear();
            PeekActions.Visibility = PeekActionsRight.Visibility = PeekClose.Visibility = Visibility.Collapsed;
            var img = _peek.Shot;
            double iw = Math.Max(1, img.Width), ih = Math.Max(1, img.Height);
            double w = 240, sh = Math.Clamp(w * ih / iw, 70, 150);
            if (w * ih / iw > 150) w = Math.Max(110, 150 * iw / ih);
            PeekShotImg.ImageSource = img;
            PeekShot.Width = w;
            PeekShot.Height = sh;
            PeekShotScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            PeekShotScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            PeekShotScale.ScaleX = PeekShotScale.ScaleY = 1;
            PeekShot.BeginAnimation(OpacityProperty, null);
            PeekShot.Opacity = 1;
            ShowPanel(PeekPanel);
            AnimateSize(Math.Max(PeekW - 60, w + 20), sh + 20 + NotchExtra, 22, true);
            ShowSideBubbles();
            _peekTimer.Stop();
            var me = _peek;
            After(1100, () =>
            {
                if (_peek != me) return;
                // the picture goes "into" the island (shrinks towards the top), then the island closes
                var d = TimeSpan.FromMilliseconds(320);
                var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
                PeekShotScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.15, d) { EasingFunction = ease });
                PeekShotScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.15, d) { EasingFunction = ease });
                PeekShot.BeginAnimation(OpacityProperty, new DoubleAnimation(0, d) { EasingFunction = ease });
                After(300, () => { if (_peek == me) EndPeek(); });
            });
            return;
        }

        PeekTitle.Text = _peek.Title;
        PeekText.Text = _peek.Text;
        // the message card shows up to two lines of the message
        PeekText.TextWrapping = _peek.Ticker ? TextWrapping.Wrap : TextWrapping.NoWrap;
        PeekText.MaxHeight = _peek.Ticker ? 32 : double.PositiveInfinity;
        PeekText.Visibility = string.IsNullOrEmpty(_peek.Text) ? Visibility.Collapsed : Visibility.Visible;
        var glyph = _peek.GlyphBrush ?? Brushes.White;
        PeekIcon.Kind = _peek.Icon ?? "bell";
        // card look (2026-11-15): a strong coloured square with a white symbol, like an app icon – not a pale circle
        PeekIcon.Foreground = _peek.Ticker ? Brushes.White : glyph;
        PeekIconCircle.Background = _peek.Ticker ? SymbolGround(glyph) : Tint(glyph, 0.18);
        PeekIconCircle.CornerRadius = new CornerRadius(_peek.Ticker ? 10 : 17);
        PeekIconCircle.Width = PeekIconCircle.Height = _peek.Ticker ? 36 : 34;
        PeekImage.ImageSource = _peek.Image;

        // the left symbol: ring · person · cover · app icon · tinted circle (first that applies)
        PeekRingBox.Visibility = PeekAvatarBox.Visibility = PeekCover.Visibility = PeekImageBorder.Visibility = PeekIconCircle.Visibility = Visibility.Collapsed;
        if (_peek.Ring is { } ringValue)
        {
            PeekRing.Value = ringValue * 100;
            PeekRing.Stroke = glyph;
            PeekRing.Track = Tint(glyph, 0.25);
            PeekRingIcon.Kind = _peek.Icon ?? "asterisk";
            PeekRingIcon.Foreground = glyph;
            PeekRingBox.Visibility = Visibility.Visible;
        }
        else if (_peek.Avatar != null)
        {
            PeekAvatar.Background = _peek.AvatarBrush ?? Brushes.SlateBlue;
            PeekAvatarText.Text = _peek.Avatar;
            PeekAvatarBadge.ImageSource = _peek.Image;
            PeekAvatarBadgeBox.Visibility = _peek.Image != null ? Visibility.Visible : Visibility.Collapsed;
            PeekAvatarBox.Visibility = Visibility.Visible;
        }
        else if (_peek.Cover && _peek.Image != null)
        {
            PeekCoverImg.ImageSource = _peek.Image;
            PeekCover.Visibility = Visibility.Visible;
        }
        else if (_peek.Image != null) PeekImageBorder.Visibility = Visibility.Visible;
        else PeekIconCircle.Visibility = Visibility.Visible;

        PeekSourceRow.Visibility = string.IsNullOrEmpty(_peek.Source) ? Visibility.Collapsed : Visibility.Visible;
        PeekSource.Text = _peek.Source ?? "";
        PeekSourceIcon.Visibility = _peek.SourceIcon == null ? Visibility.Collapsed : Visibility.Visible;
        PeekSourceIcon.Kind = _peek.SourceIcon ?? "bell";
        PeekSourceIcon.Foreground = _peek.SourceBrush ?? (Brush)FindResource("SubText");
        PeekTime.Visibility = _peek.Source == null && _peek.Key is "notif" or "call" ? Visibility.Visible : Visibility.Collapsed;
        PeekClose.Visibility = Visibility.Collapsed;

        PeekActions.Children.Clear();
        PeekActionsRight.Children.Clear();
        bool right = _peek.Actions.Count <= 2 || _peek.Ticker;
        foreach (var (label, act, accent) in _peek.Actions)
        {
            var b = new Button { Content = label, Margin = right ? new Thickness(6, 0, 0, 0) : new Thickness(0, 0, 6, 4), Padding = new Thickness(11, 5, 11, 5) };
            b.SetResourceReference(StyleProperty, accent ? "AccentButton" : "PillButton");
            // the usual answers become round symbol buttons (snooze, done, again, open); in the card look every
            // answer is round – grey for "later", coloured for the main thing
            // the labels arrive already translated – look the German word up again for the symbol
            var de = RoundLabels.FirstOrDefault(d => d == label || L.T(d) == label) ?? label;
            var round = de switch
            {
                "Ablehnen" => ("xmark", (Brush)null),
                "Annehmen" => ("check", (Brush)FindResource("Green")),
                "Später" => ("clock", (Brush)null),
                "Nochmal" => ("refresh", null),
                "Erledigt" or "OK" or "Aus" => ("check", (Brush)FindResource("Green")),
                "Öffnen" or "Chat öffnen" => ("open", accent && _peek.Ticker ? (Brush)FindResource("Green") : null),
                "Einstellungen" => ("gear", (Brush)null),
                "Zeigen" => ("folder", (Brush)null),
                _ when _peek.Ticker => (accent ? "arrow.up.right" : "chevron.right", accent ? (Brush)FindResource("Green") : null),
                _ => ((string)null, (Brush)null)
            };
            if (right && round.Item1 != null && _peek.Key != "agentask") // (Claude's permission: two clear words, C3)
            {
                b.SetResourceReference(StyleProperty, "GlassIcon");
                b.Content = null;
                b.Padding = new Thickness(0); // the text padding would squeeze the symbol
                b.ToolTip = label;
                Ext.SetIcon(b, round.Item1);
                if (round.Item2 != null) { b.Background = round.Item2; b.Foreground = Brushes.Black; b.BorderBrush = Brushes.Transparent; }
            }
            var a = act;
            b.Click += (_, _) => { EndPeek(); a?.Invoke(); };
            (right ? (Panel)PeekActionsRight : PeekActions).Children.Add(b);
        }
        // round symbol buttons (banner look)
        foreach (var (bIcon, tip, fill, dark, act) in _peek.Buttons)
        {
            var b = new Button { ToolTip = tip, Margin = new Thickness(6, 0, 0, 0), Width = 30, Height = 30, Padding = new Thickness(0) };
            b.SetResourceReference(StyleProperty, "GlassIcon");
            Ext.SetIcon(b, bIcon);
            Ext.SetIconSize(b, 14);
            if (fill != null) { b.Background = fill; b.BorderBrush = Brushes.Transparent; b.Foreground = dark ? Brushes.Black : Brushes.White; }
            var a = act;
            b.Click += (_, e) => { e.Handled = true; EndPeek(); a?.Invoke(); };
            PeekActionsRight.Children.Add(b);
        }
        // messengers: "Antworten" opens a message field in the card (it does not close the card)
        PeekReply.Visibility = Visibility.Collapsed;
        PeekReplyBox.Text = "";
        if (_peek.Reply != null)
        {
            var rb = new Button { ToolTip = L.T("Antworten"), Margin = new Thickness(6, 0, 0, 0), Width = 30, Height = 30, Padding = new Thickness(0) };
            rb.SetResourceReference(StyleProperty, "GlassIcon");
            Ext.SetIcon(rb, "reply");
            Ext.SetIconSize(rb, 14);
            rb.Click += (_, e) => { e.Handled = true; OpenReplyBox(); };
            PeekActionsRight.Children.Insert(0, rb);
        }
        PeekActions.Visibility = !right && _peek.Actions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PeekActionsRight.Visibility = right && _peek.Actions.Count > 0 || _peek.Buttons.Count > 0 || _peek.Reply != null ? Visibility.Visible : Visibility.Collapsed;

        ShowPanel(PeekPanel);
        DropInPeek();
        PeekPanel.Measure(new Size(PeekW - 20, double.PositiveInfinity));
        double h = Math.Clamp(PeekPanel.DesiredSize.Height + 2, 54, 130);
        AnimateSize(PeekW, h + NotchExtra, Math.Min(28, (h + 2) / 2), true);
        ShowSideBubbles();

        _peekTimer.Stop();
        if (_peek.Ticker)
        {
            // the card opened from the ticker: it stays while the pointer is on it; once it leaves, the thin
            // line at the bottom runs down and the card closes
            PeekProgress.Visibility = _peek.TimeoutMs > 0 ? Visibility.Visible : Visibility.Collapsed;
            PeekProgressScale.ScaleX = 1;
            if (!IsCursorOverIsland(4)) StartCardCountdown();
            return;
        }
        if (_peek.TimeoutMs > 0)
        {
            _peekTimer.Interval = TimeSpan.FromMilliseconds(_peek.TimeoutMs);
            _peekTimer.Start();
        }
    }

    private const double TickerW = 330, CardLinger = 3500;

    /// <summary>Message ticker: the island grows only sideways and shows "Name  message" running through once.</summary>
    private void ShowTicker()
    {
        var p = _peek;
        bool person = p.Avatar != null;
        bool cover = !person && p.Cover && p.Image != null;
        bool appImage = !person && !cover && p.Image != null;
        TickerAvatar.Visibility = person ? Visibility.Visible : Visibility.Collapsed;
        TickerCover.Visibility = cover ? Visibility.Visible : Visibility.Collapsed;
        TickerImageBox.Visibility = appImage ? Visibility.Visible : Visibility.Collapsed;
        TickerSymbol.Visibility = !person && !cover && !appImage ? Visibility.Visible : Visibility.Collapsed;
        TickerCoverImg.ImageSource = cover ? p.Image : null;
        TickerSymbolIcon.Kind = p.Icon ?? "bell";
        TickerSymbol.Background = SymbolGround(p.GlyphBrush);
        TickerAvatar.Background = p.AvatarBrush ?? Brushes.SlateBlue;
        TickerAvatarText.Text = p.Avatar ?? "";
        TickerImage.ImageSource = p.Image;
        TickerApp.ImageSource = p.Image;
        TickerAppBox.Visibility = person && p.Image != null ? Visibility.Visible : Visibility.Collapsed;
        TickerName.Text = p.Title ?? "";
        TickerMsg.Text = p.Text ?? "";
        TickerCount.Visibility = p.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        TickerCountText.Text = p.Count.ToString();
        TickerShift.BeginAnimation(TranslateTransform.XProperty, null);
        TickerShift.X = 0;
        ShowPanel(TickerPanel);
        AnimateSize(TickerW, PillH + NotchExtra, PillH / 2, true);
        ShowSideBubbles();
        if (S.Animations != AnimationLevel.Aus) Pop(TickerLead);
        // a long line runs through slowly once, after a short look at its beginning
        After(900, () =>
        {
            if (_peek != p || p.Open || _state != State.Peek) return;
            TickerText.Measure(new Size(double.PositiveInfinity, 40));
            double over = TickerText.DesiredSize.Width - TickerClip.ActualWidth;
            if (over > 4)
                TickerShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, -over - 8, TimeSpan.FromSeconds(Math.Clamp(over / 30, 1.2, 6)))
                    { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        });
        _peekTimer.Stop();
        // what rings (timer, alarm, reminder, call) stays until it is answered – and a ring runs around the island now and then
        if (p.TimeoutMs > 0)
        {
            _peekTimer.Interval = TimeSpan.FromMilliseconds(p.TimeoutMs);
            _peekTimer.Start();
        }
        else if (S.Animations == AnimationLevel.An)
        {
            var c = (p.GlyphBrush as SolidColorBrush)?.Color ?? Colors.White;
            void Again()
            {
                if (_peek != p || p.Open || _state != State.Peek) return;
                Ripples(c);
                After(1800, Again);
            }
            After(700, Again);
        }
    }

    /// <summary>The coloured ground of a notice symbol (a white symbol gets a grey ground).</summary>
    private static Brush SymbolGround(Brush glyph) =>
        glyph is SolidColorBrush s && !(s.Color.R > 240 && s.Color.G > 240 && s.Color.B > 240) ? glyph : Frozen(Color.FromRgb(0x48, 0x48, 0x4A));

    /// <summary>The pointer came onto the ticker: the same message opens as the full card (app, person, text, buttons).</summary>
    private void OpenTicker()
    {
        if (_state != State.Peek || _peek is not { Ticker: true, Open: false } p) return;
        // the card grows out of the line: the person / app symbol flies from the line to its place on the card
        var from = new FrameworkElement[] { TickerAvatar, TickerCover, TickerImageBox, TickerSymbol }.FirstOrDefault(e => e.IsVisible);
        var picture = from != null ? Snapshot(from) : null;
        p.Open = true;
        _peeks.Insert(0, p);
        _peek = null;
        ShowNextPeek();
        var to = new FrameworkElement[] { PeekAvatarBox, PeekCover, PeekImageBorder, PeekIconCircle, PeekRingBox }.FirstOrDefault(e => e.Visibility == Visibility.Visible);
        if (from != null && to != null && picture != null)
            StartFlight(from, to, picture, CornerOf(from), CornerOf(to));
    }

    private static double CornerOf(FrameworkElement e) => e is Border b ? b.CornerRadius.TopLeft : e.ActualWidth / 2;

    private void StartCardCountdown()
    {
        if (_state != State.Peek || _peek is not { Ticker: true, Open: true }) return;
        if (_peek.TimeoutMs == 0) { PeekProgress.Visibility = Visibility.Collapsed; return; } // rings: stays until answered
        PeekProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(CardLinger)));
        _peekTimer.Stop();
        _peekTimer.Interval = TimeSpan.FromMilliseconds(CardLinger);
        _peekTimer.Start();
    }

    private void StopCardCountdown()
    {
        if (_state != State.Peek || _peek is not { Ticker: true, Open: true }) return;
        _peekTimer.Stop();
        PeekProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        PeekProgressScale.ScaleX = 1;
    }

    private void PeekClose_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        DismissPeek();
    }

    private void EndPeek()
    {
        _peekTimer.Stop();
        if (_state != State.Peek) return;
        _peek = null;
        ShowNextPeek();
    }

    // =====================================================================
    //  Shortcuts & keyboard
    // =====================================================================

    public void OnHotkey(HotkeyAction a)
    {
        switch (a)
        {
            case HotkeyAction.ToggleIsland:
                if (_state == State.Expanded) Collapse(); else Expand(null, true);
                break;
            case HotkeyAction.Clipboard: ToggleTab("Clip"); break;
            case HotkeyAction.Shelf: ToggleTab("Shelf"); break;
            case HotkeyAction.Music: ToggleTab("Music"); break;
            case HotkeyAction.Widgets: ToggleTab(L.T("Timer")); break;
            case HotkeyAction.Reminders: ToggleTab("Remind"); break;
            case HotkeyAction.AiChat: ToggleTab("Ai"); break;
            case HotkeyAction.QuickNote: ToggleTab("Notes"); break;
            case HotkeyAction.AiSelection: AskAboutSelection(); break;          // 13a (2026-11-18)
            case HotkeyAction.AiRegion: ScreenshotController.Start(false, explain: true); break; // 14
            case HotkeyAction.Calculator: OpenQuery("="); break;  // 42
            case HotkeyAction.Search: OpenQuery(""); break;        // 46
            case HotkeyAction.ColorPicker: ScreenshotController.Start(false, picker: true); break; // 30
            case HotkeyAction.InstantReplay: _ = InstantReplay(); break; // 39
            case HotkeyAction.VoiceCommand: VoiceCommand(); break; // 57
            case HotkeyAction.SongRecognize: RecognizeSong(); break; // 22
            case HotkeyAction.Controls: ToggleTab("Controls"); break;
            case HotkeyAction.Screenshot: TakeScreenshot(false); break;
            case HotkeyAction.ScreenshotOcr: TakeScreenshot(true); break;
            case HotkeyAction.MicMute: ToggleMic(); break;
            case HotkeyAction.QueueMode: SetQueueMode(!H.Clipboard.QueueMode); break;
            case HotkeyAction.PlayPause: H.Media.PlayPause(); break;
            case HotkeyAction.NextTrack: H.Media.Next(); break;
            case HotkeyAction.PrevTrack: H.Media.Previous(); break;
            case HotkeyAction.FocusTimer:
                H.Timer.Toggle();
                ShowHint("timer", H.Timer.Running ? "Läuft" : "Pause", (Brush)FindResource("Orange"), key: "timer");
                break;
            case HotkeyAction.Settings: SettingsWindow.ShowSingle(); break;
        }
    }

    private void ToggleTab(string tab)
    {
        if (_state == State.Expanded && _tab == tab && IsActive) { Collapse(); return; }
        Expand(tab, true);
    }

    private void ToggleMic()
    {
        bool muted = H.Audio.ToggleMic();
        // replaces an earlier mic hint immediately – no stale "muted" after switching back on
        if (muted) ShowHint("mic.slash", "Stumm", (Brush)FindResource("Danger"), key: "mic");
        else ShowHint("mic", L.T("An"), null, key: "mic");
        UpdatePill();
        UpdateControlsUi();
        if (muted) MuteMoment();
    }

    // =====================================================================
    //  Live: which app uses the microphone / camera or shares the screen
    // =====================================================================

    // ---------- privacy notices (approved 2026-11-14): apps you know only light up their bubble; a banner comes
    //            for an app using the mic / camera for the first time (or after two weeks) and always for screen sharing ----------

    private Dictionary<string, DateTime> _knownLive;

    private void OnLiveStarted(LiveApp app, char kind)
    {
        _knownLive ??= Storage.Load<Dictionary<string, DateTime>>("privacy_known.json") ?? new();
        var key = $"{kind}:{app.Name}";
        bool known = _knownLive.TryGetValue(key, out var last) && DateTime.Now - last < TimeSpan.FromDays(14);
        _knownLive[key] = DateTime.Now;
        Storage.Save("privacy_known.json", _knownLive);

        if (kind != 's' && known)
        {
            // an app you know starts listening (user's pick 2026-11-15, "B · Hört zu"): the island says so itself –
            // "Claude hört zu" with the orange wave – then the wave lives on as the small bubble next to it
            // (a Discord call no longer hides it – that another app listens matters most during a call; 2026-11-16)
            if (kind == 'm' && !app.IsDiscord) { ShowListening(app); return; }
            // a known call / camera: no glow at all (user decision 2026-11-17 – it lit up the whole island's outline)
            return;
        }
        var orange = (Brush)FindResource("Orange");
        var p = new PeekInfo
        {
            Key = "live", Source = L.T("Datenschutz"), SourceIcon = "shield",
            Icon = kind switch { 's' => "screen.share", 'm' => "mic", _ => "camera" },
            GlyphBrush = kind switch { 's' => ShareBrush, 'm' => orange, _ => (Brush)FindResource("Green") },
            Title = kind switch { 's' => L.T($"{app.Name} teilt deinen Bildschirm"), 'm' => L.T($"{app.Name} nutzt dein Mikrofon"), _ => L.T($"{app.Name} nutzt deine Kamera") },
            Text = kind == 's' ? L.T("Mit einem Klick beenden") : last == default ? L.T("zum ersten Mal") : L.T($"zum ersten Mal seit {(int)(DateTime.Now - last).TotalDays} Tagen"),
            TimeoutMs = 6000
        };
        if (kind == 's') p.Button("stop", "Teilen beenden", () => _ = StopSharing(app), (Brush)FindResource("Danger"));
        else if (kind == 'm' && !H.Audio.MicMuted && !app.SelfMuted)
            p.Button("mic.slash", app.IsDiscord && H.Discord.Connected ? L.T("In Discord stummschalten") : L.T("Mikrofon stummschalten"), () => MuteLiveApp(app));
        p.Button("open", L.T($"{app.Name} öffnen"), () => OpenLiveApp(app));
        ShowPeek(p);
    }

    private void ShowListening(LiveApp app)
    {
        var orange = (Brush)FindResource("Orange");
        if (S.Animations == AnimationLevel.Aus) { GlowBubble(Act.Live, orange); return; }
        ShowHint("mic", L.T($"{app.Name} hört zu"), orange, key: "listen");
        if (_state != State.Hint) { GlowBubble(Act.Live, orange); return; } // the island is busy (open, …) – at least it glows
        if (HintWave.Children.Count == 0) MakeWave(HintWave, 5, 13, 2);
        HintIcon.Visibility = Visibility.Collapsed;
        HintWave.Visibility = Visibility.Visible;
        StartWaves();
        HintText.Inlines.Clear();
        HintText.Inlines.Add(new System.Windows.Documents.Run(app.Name) { Foreground = orange });
        HintText.Inlines.Add(new System.Windows.Documents.Run(L.T(" hört zu")) { Foreground = Brushes.White });
    }

    /// <summary>The bubble of an activity glows up for a moment (instead of a banner).</summary>
    private void GlowBubble(Act act, Brush color)
    {
        var c = (color as SolidColorBrush)?.Color ?? Colors.Orange;
        // the bubble of that activity – or, when it is not a bubble right now (e.g. no music: the mic is in the
        // island itself), the island's own shadow. Before, nothing happened in that case.
        var fx = new[] { Bubble, Bubble2 }.Where(b => b.Visibility == Visibility.Visible && Equals(b.Tag, act))
                     .Select(b => b.Effect as System.Windows.Media.Effects.DropShadowEffect).FirstOrDefault(e => e != null)
                 ?? IslandShadow;
        var dur = TimeSpan.FromMilliseconds(1800);
        fx.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.ColorProperty, new ColorAnimation(c, c, dur) { FillBehavior = FillBehavior.Stop });
        fx.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty,
            new DoubleAnimation(1, 0.35, dur) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }, FillBehavior = FillBehavior.Stop });
        fx.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty, new DoubleAnimation(26, 16, dur) { FillBehavior = FillBehavior.Stop });
    }

    private void UpdateLiveUi()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(UpdateLiveUi)); return; }
        bool apps = H.Privacy.InUse, steam = AnyDl;
        if (steam && (SteamRow.DataContext as DlHolder)?.Steam != Dl) SteamRow.DataContext = new DlHolder { Steam = Dl };
        SteamRow.ToolTip = Dl == H.Downloads ? L.T("Browser-Download") : L.T("Steam-Download");
        bool any = apps || steam;
        foreach (var a in H.Privacy.Apps)
        {
            a.SelfMuted = a.IsDiscord && H.Discord.Connected && H.Discord.Muted;
            // without a keybind the island presses Discord's own "share your screen" button
            a.CanStartStream = a.IsDiscord && a.Mic && !a.Screen;
            a.CanHangUp = a.IsDiscord && a.Mic;
        }
        var liveVis = any && !IsTabHidden("Live") ? Visibility.Visible : Visibility.Collapsed;
        if (LiveTabBtn.Visibility != liveVis) { LiveTabBtn.Visibility = liveVis; UpdateTabDots(); }
        UpdateTabBadges();
        if (!any)
        {
            // nothing live any more → leave the (now hidden) tab
            if (_tab == "Live") SelectTab("Music", false);
            return;
        }
        if (_state == State.Expanded && _tab == "Live" && Math.Abs(_targetH - (TabHeight("Live") + NotchExtra)) > 1)
            AnimateSize(ExpW, TabHeight("Live") + NotchExtra, ExpRadius, true);
        // the Steam download sits below the calls (or alone, centred)
        SteamRow.Visibility = steam ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetRow(SteamRow, apps ? 1 : 0);
        SteamRow.VerticalAlignment = apps ? VerticalAlignment.Top : VerticalAlignment.Center;
        if (!apps)
        {
            LiveSingle.Visibility = LiveListScroll.Visibility = Visibility.Collapsed;
            return;
        }
        // one app (even when it streams AND uses the mic) = one big entry, several apps = list
        bool single = H.Privacy.Single;
        LiveSingle.Visibility = single ? Visibility.Visible : Visibility.Collapsed;
        LiveListScroll.Visibility = single ? Visibility.Collapsed : Visibility.Visible;
        var p = H.Privacy.Primary;
        if (single && p != null)
        {
            LiveSingleMute.Visibility = p.Mic ? Visibility.Visible : Visibility.Collapsed;
            LiveSingleStop.Visibility = p.Screen ? Visibility.Visible : Visibility.Collapsed;
            LiveSingleStart.Visibility = p.CanStartStream ? Visibility.Visible : Visibility.Collapsed;
            LiveSingleHangUp.Visibility = p.CanHangUp ? Visibility.Visible : Visibility.Collapsed;
            // a Discord call has many buttons – "Öffnen" moves onto the app icon there
            LiveSingleOpen.Visibility = p.CanHangUp ? Visibility.Collapsed : Visibility.Visible;
            // Discord connected: the call with its people, "Taub", the channel as title
            bool dc = p.IsDiscord && H.Discord.Connected && H.Discord.InVoice;
            LiveMembers.Visibility = dc && H.Discord.Members.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            LiveSingleDeaf.Visibility = dc ? Visibility.Visible : Visibility.Collapsed;
            LiveSingleDeafText.Text = H.Discord.Deafened ? L.T("Hören") : L.T("Taub");
            Ext.SetIcon(LiveSingleDeafBtn, H.Discord.Deafened ? "speaker" : "speaker.slash");
            LiveSingleDeafBtn.Foreground = H.Discord.Deafened ? (Brush)FindResource("Danger") : Brushes.White;
            LiveSingleName.Text = dc && !string.IsNullOrEmpty(H.Discord.Channel) ? H.Discord.Channel : p.Name;
            LiveSingleMuteText.Text = H.Audio.MicMuted || p.SelfMuted ? L.T("An") : L.T("Stumm");
            LiveSingleBadge.Kind = p.Screen ? "display" : p.Mic ? (p.SelfMuted ? "mic.slash" : "mic") : "camera";
            // on a white badge (white mic colour) the symbol is dark
            LiveSingleBadge.Foreground = ReferenceEquals(p.Accent, Brushes.White) ? Brushes.Black : Brushes.White;
        }
        ApplyLivePick();
    }

    private void LiveMute_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is LiveApp app) MuteLiveApp(app);
        else ToggleMic();
        UpdateLiveUi();
    }

    /// <summary>Discord (connected via its RPC): mute yourself IN Discord; everything else: the Windows microphone.</summary>
    private async void MuteLiveApp(LiveApp app)
    {
        if (app.IsDiscord && H.Discord.Connected)
        {
            bool ok = await H.Discord.ToggleMute();
            if (!ok) { ShowHint("xmark", "Discord hat nicht reagiert", (Brush)FindResource("Danger"), key: "mic"); return; }
            if (H.Discord.Muted) { ShowHint("mic.slash", "Stumm in Discord", (Brush)FindResource("Danger"), key: "mic"); MuteMoment(); }
            else ShowHint("mic", L.T("In Discord an"), null, key: "mic");
            return;
        }
        ToggleMic();
    }

    private async void LiveDeaf_Click(object sender, RoutedEventArgs e)
    {
        if (!await H.Discord.ToggleDeaf()) { ShowHint("xmark", "Discord hat nicht reagiert", (Brush)FindResource("Danger"), key: "mic"); return; }
        UpdateLiveUi();
    }

    private void UpdateDiscordState()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(UpdateDiscordState)); return; }
        PrivacyService.DiscordInVoice = H.Discord.Connected && H.Discord.InVoice;
        WatchCallEnd(); // hung up in Discord itself: the same moment as with the island's button
        foreach (var a in H.Privacy.Apps) a.SelfMuted = a.IsDiscord && H.Discord.Connected && H.Discord.Muted;
        UpdatePill();
        UpdateLiveUi();
    }

    private void LiveIcon_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is LiveApp app) { e.Handled = true; OpenLiveApp(app); }
    }

    private void LiveOpen_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is LiveApp app) OpenLiveApp(app);
    }

    private void LiveStop_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is LiveApp app) _ = StopSharing(app);
    }

    private static bool DiscordStreamKey(out HotkeyGesture g) => HotkeyGesture.TryParse(S.DiscordStreamKey, out g);

    /// <summary>Discord's picker is open: only the choice is left.</summary>
    private void StreamPicked() => ShowPeek(new PeekInfo
    {
        Icon = "display",
        Key = "live",
        GlyphBrush = (Brush)FindResource("Blue"),
        Title = L.T("Wähl in Discord, was du teilst"),
        Text = L.T("Fenster oder Bildschirm auswählen, dann „Live gehen“."),
        TimeoutMs = 5000,
    });

    /// <summary>What the Live buttons did (no content of the call) – %AppData%\PeakPill\live-log.txt, last 200 lines.</summary>
    private static void LiveLog(string what)
    {
        try
        {
            var f = Storage.PathOf("live-log.txt");
            var lines = File.Exists(f) ? File.ReadAllLines(f).TakeLast(199).ToList() : new List<string>();
            lines.Add($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {what}");
            File.WriteAllLines(f, lines);
        }
        catch { }
    }

    private static bool IsDiscordInFront()
    {
        Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint pid);
        try { return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName.StartsWith("Discord", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>Red phone: leave the Discord call (RPC when connected, else Discord's own button).</summary>
    private async void LiveHangUp_Click(object sender, RoutedEventArgs e)
    {
        var app = (sender as FrameworkElement)?.Tag as LiveApp ?? H.Privacy.Apps.FirstOrDefault(a => a.IsDiscord);
        if (app == null) return;
        Collapse();
        // hang up at once; the end of the call is noticed by WatchCallEnd – here it waits until the island has
        // closed and the call bubble is out again, then plays the moment itself
        _hangUpMomentAt = DateTime.Now;
        var leave = H.Discord.Connected ? H.Discord.LeaveVoice() : Task.FromResult(false);
        await Task.Delay(380);
        bool ok = await leave;
        if (!ok) ok = await H.Privacy.HangUpAsync(app);
        if (ok)
        {
            _hangUpMomentAt = DateTime.Now;
            _wasInVoice = _wasDiscordMic = false;
            await HangUpMoment();
            return;
        }
        _hangUpMomentAt = DateTime.MinValue;
        H.Privacy.Open(app);
        ShowPeek(new PeekInfo
        {
            Icon = "phone",
            Key = "live",
            GlyphBrush = (Brush)FindResource("Danger"),
            Title = L.T("Discord geöffnet"),
            Text = L.T("Leg dort auf – der Knopf war nicht erreichbar."),
            TimeoutMs = 4500,
        });
    }

    private void OpenLiveApp(LiveApp app)
    {
        Collapse();
        if (!H.Privacy.Open(app)) ShowHint("xmark", $"{app.Name} nicht gefunden", (Brush)FindResource("Danger"), key: "live");
    }

    private async Task StopSharing(LiveApp app)
    {
        // Discord has no accessible "stop" button – its own keybind toggles the stream off
        if (app.IsDiscord)
        {
            // Discord's own "stop streaming" button first; the keybind only with Discord in front (it ignores it otherwise)
            Collapse();
            ShowHint("stop", "Stream wird beendet …", (Brush)FindResource("Danger"), key: "live");
            if (await H.Privacy.StopSharingAsync(app) || await H.Privacy.StopDiscordStreamAsync(app)) { LiveLog("stop: button"); ShowHint("check", "Stream beendet", (Brush)FindResource("Green"), key: "live"); return; }
            if (DiscordStreamKey(out var g))
            {
                H.Privacy.Open(app);
                await Task.Delay(700);
                await PasteHelper.SendComboHeldAsync(g);
                await Task.Delay(3000);
                bool off = !H.Privacy.Apps.Any(a => a.IsDiscord && a.Screen);
                LiveLog($"stop: keybind in Discord, stream off={off}");
                if (off) return;
            }
        }
        ShowHint("stop", "Wird beendet …", (Brush)FindResource("Danger"), key: "live");
        if (await H.Privacy.StopSharingAsync(app))
        {
            ShowHint("check", "Teilen beendet", (Brush)FindResource("Green"), key: "live");
            return;
        }
        // the app has no button we can press → bring it to the front so it can be stopped there
        H.Privacy.Open(app);
        ShowPeek(new PeekInfo
        {
            Icon = "record",
            Key = "live",
            GlyphBrush = (Brush)FindResource("Danger"),
            Title = L.T($"{app.Name} geöffnet"),
            Text = L.T("Beende das Teilen dort – der Knopf war nicht erreichbar."),
            TimeoutMs = 4500
        });
    }

    // =====================================================================
    //  AI agents: Claude Code's progress (from its hooks)
    // =====================================================================

    private bool _indetRunning;

    private void UpdateAgentUi()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(UpdateAgentUi)); return; }
        var s = H.Agents.Primary;
        bool several = H.Agents.Sessions.Count > 1;
        AgentEmpty.Visibility = s == null ? Visibility.Visible : Visibility.Collapsed;
        AgentCard.Visibility = s == null || several ? Visibility.Collapsed : Visibility.Visible;
        AgentListScroll.Visibility = several ? Visibility.Visible : Visibility.Collapsed;
        AgentCard.DataContext = s;
        if (s == null || several) { StopIndet(); return; }

        var green = (Brush)FindResource("Green");
        Brush tint = s.IsDone ? green : AgentBrush;
        AgentTime.Foreground = tint;
        AgentFill.Background = tint;
        AgentSpinner.Visibility = s.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        AgentStateIcon.Visibility = s.IsRunning ? Visibility.Collapsed : Visibility.Visible;
        AgentStateIcon.Kind = s.IsDone ? "check" : "bell";
        AgentStateIcon.Foreground = tint;
        AgentStep.Foreground = s.IsWaiting ? AgentBrush : (Brush)FindResource("SubText");

        // bar: real progress when Claude keeps a task list, otherwise a moving loader while it works
        double track = AgentTrack.ActualWidth > 0 ? AgentTrack.ActualWidth : ExpW - 28;
        double fill = s.IsDone ? 1 : s.Progress;
        bool indet = s.IsRunning && !s.HasPlan;
        AgentFill.Visibility = indet ? Visibility.Collapsed : Visibility.Visible;
        AgentFill.BeginAnimation(WidthProperty, new DoubleAnimation(track * fill, TimeSpan.FromMilliseconds(350)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        if (indet && !_indetRunning)
        {
            AgentIndet.Visibility = Visibility.Visible;
            Calm.Loop(AgentIndet, AgentIndetShift, TranslateTransform.XProperty,
                new DoubleAnimation(-80, track, TimeSpan.FromSeconds(1.3)) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
            _indetRunning = true;
        }
        else if (!indet) StopIndet();
    }

    private void StopIndet()
    {
        if (!_indetRunning) return;
        Calm.Stop(AgentIndet, AgentIndetShift, TranslateTransform.XProperty);
        AgentIndet.Visibility = Visibility.Collapsed;
        _indetRunning = false;
    }

    private void AgentCard_Click(object sender, MouseButtonEventArgs e)
    {
        Collapse();
        AgentService.OpenHost((sender as FrameworkElement)?.DataContext as AgentSession); // that session's own window (8b)
    }

    private void OnAgentFinished(AgentSession s)
    {
        // short answers are no news – only a turn that ran a while gets a "done"
        Note("Agent", 180); // opening the island in the next minutes shows Claude's page
        if (s.FinishedAt is { } f && (f - s.StartedAt).TotalSeconds < 15) return;
        _peeks.RemoveAll(p => p.Key == "agentwait");
        // no banner (user wish 2026-11-14): a soft green light runs across the island to Claude's bubble, there the
        // star turns into a check while the ring closes in green; then the bubble slips into the island
        var green = ((SolidColorBrush)FindResource("Green")).Color;
        bool light = S.Animations == AnimationLevel.An && _state == State.Collapsed;
        if (light) Sweep(green);
        After(light ? 520 : 1, () =>
        {
            var (ring, icon) = Bubble.Tag is Act.Agent ? (BubbleRing, BubbleIcon) : Bubble2.Tag is Act.Agent ? (Bubble2Ring, Bubble2Icon) : (null, null);
            double before = ring?.Value ?? 0;
            _agentDoneUntil = DateTime.Now.AddSeconds(4.5);
            UpdatePill();
            if (S.Animations == AnimationLevel.Aus) return;
            if (ring != null)
            {
                ring.BeginAnimation(Ring.ValueProperty, new DoubleAnimation(Math.Min(before, 99), 100, TimeSpan.FromMilliseconds(420))
                    { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
                Pop(icon);
                GlowBubble(Act.Agent, (Brush)FindResource("Green"));
            }
            else if (LeadIcon.Visibility == Visibility.Visible) Pop(LeadIcon);
        });
        DoneSummary(s); // 11: how many files Claude changed – a click shows which
        After(5200, () => UpdatePill());
    }

    // ---------- small moments on the island (approved 2026-11-14 – subtle; Settings › Erscheinungsbild › Kleine Animationen) ----------

    private void After(int ms, Action a)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(1, ms)) };
        t.Tick += (_, _) => { t.Stop(); a(); };
        t.Start();
    }

    /// <summary>A soft band of light runs once across the island from left to right.</summary>
    private void Sweep(Color c)
    {
        SweepMid.Color = Color.FromArgb(0x5C, c.R, c.G, c.B);
        SweepEdgeA.Color = SweepEdgeB.Color = Color.FromArgb(0, c.R, c.G, c.B);
        IslandSweep.Opacity = 1;
        var run = new DoubleAnimation(-80, Island.ActualWidth + 10, TimeSpan.FromMilliseconds(620)) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseIn } };
        run.Completed += (_, _) => IslandSweep.Opacity = 0;
        IslandSweepShift.BeginAnimation(TranslateTransform.XProperty, run);
    }

    /// <summary>A symbol pops in (small → full size with a light overshoot).</summary>
    private static void Pop(FrameworkElement e)
    {
        if (e == null) return;
        var sc = new ScaleTransform();
        e.RenderTransformOrigin = new Point(0.5, 0.5);
        e.RenderTransform = sc;
        var a = new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(380)) { EasingFunction = new BackEase { Amplitude = 0.45, EasingMode = EasingMode.EaseOut } };
        sc.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        sc.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    /// <summary>Two rings run away from the island and fade (subtle: small growth).</summary>
    private void Ripples(Color c)
    {
        int i = 0;
        foreach (var (b, sc) in new[] { (Ripple1, Ripple1Scale), (Ripple2, Ripple2Scale) })
        {
            b.BorderBrush = new SolidColorBrush(c);
            var begin = TimeSpan.FromMilliseconds(i++ * 220);
            var d = TimeSpan.FromMilliseconds(900);
            IEasingFunction ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            sc.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 1.1, d) { BeginTime = begin, EasingFunction = ease, FillBehavior = FillBehavior.Stop });
            sc.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 1.45, d) { BeginTime = begin, EasingFunction = ease, FillBehavior = FillBehavior.Stop });
            b.BeginAnimation(OpacityProperty, new DoubleAnimation(0.75, 0, d) { BeginTime = begin, FillBehavior = FillBehavior.Stop });
        }
    }

    /// <summary>The island gives a little: wider and flatter for a moment, then springs back (copied).</summary>
    private void Squash()
    {
        var d = TimeSpan.FromMilliseconds(360);
        IEasingFunction e = new SineEase { EasingMode = EasingMode.EaseInOut };
        _islandScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimationUsingKeyFrames { Duration = d, FillBehavior = FillBehavior.Stop, KeyFrames = { new EasingDoubleKeyFrame(1, KeyTime.FromPercent(0)), new EasingDoubleKeyFrame(1.035, KeyTime.FromPercent(0.35), e), new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1), new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut }) } });
        _islandScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimationUsingKeyFrames { Duration = d, FillBehavior = FillBehavior.Stop, KeyFrames = { new EasingDoubleKeyFrame(1, KeyTime.FromPercent(0)), new EasingDoubleKeyFrame(0.9, KeyTime.FromPercent(0.35), e), new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1), new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut }) } });
    }

    /// <summary>A bubble swells once (download done, muted).</summary>
    private void Swell(Act act, double to = 1.15)
    {
        foreach (var (b, sc) in _bubbleScale)
        {
            if (!Equals(b.Tag, act) || b.Visibility != Visibility.Visible) continue;
            var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(420), FillBehavior = FillBehavior.Stop,
                KeyFrames = { new EasingDoubleKeyFrame(1, KeyTime.FromPercent(0)), new EasingDoubleKeyFrame(to, KeyTime.FromPercent(0.45), new SineEase()), new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1), new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut }) } };
            sc.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            sc.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }
    }

    /// <summary>A short colour wash over the whole island (fades in and out again).</summary>
    private void Tint(Color c, double strength, int ms = 900)
    {
        IslandTint.Background = new SolidColorBrush(c);
        IslandTint.BeginAnimation(OpacityProperty, new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(ms), FillBehavior = FillBehavior.Stop,
            KeyFrames = { new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0)), new EasingDoubleKeyFrame(strength, KeyTime.FromPercent(0.25), new SineEase()), new EasingDoubleKeyFrame(strength, KeyTime.FromPercent(0.6)), new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1), new SineEase()) } });
    }


    // ---------- new song: the covers swap places ----------

    private string _songTitle;
    private long _shownCoverHash;
    private bool _songPending;
    private ImageSource _songOld;
    private DispatcherTimer _songTimer;
    private DateTime _songDeadline;
    /// <summary>The "new title" notice waits until the covers have swapped, otherwise it would hide the moment.</summary>
    private DateTime _trackPeekNotBefore;

    /// <summary>
    /// A new title is there: keep showing the old picture and wait up to 0.9 s for a really new one (fingerprint),
    /// then swap – with the new picture, or with the same one when the player sends none (it still moves).
    /// </summary>
    private void BeginSongSwap()
    {
        _songPending = true;
        _songOld = PillCover.ImageSource;
        _songDeadline = DateTime.Now.AddMilliseconds(1100); // (the new picture is asked for every 250 ms, MediaService.ChaseCover)
        _trackPeekNotBefore = DateTime.Now.AddMilliseconds(1700);
        if (_songTimer == null)
        {
            _songTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            _songTimer.Tick += (_, _) => SongTick();
        }
        _songTimer.Start();
    }

    private void SongTick()
    {
        bool still = _state == State.Collapsed && LeadCover.IsVisible;
        bool newPicture = H.Media.Cover != null && H.Media.CoverHash != _shownCoverHash;
        if (still && !newPicture && DateTime.Now < _songDeadline) return;
        _songTimer.Stop();
        _songPending = false;
        var old = _songOld;
        _songOld = null;
        PillCover.ImageSource = H.Media.Cover ?? old;
        _shownCoverHash = H.Media.CoverHash;
        if (still && old != null) { if (TitleMomentComing) CoverFlip(old); else CoverSwap(old); }
        else _trackPeekNotBefore = DateTime.Now;
    }

    // the user's values from the tuning mockup (2026-11-15): 0.70 s per swing, damping 0.60, lands 1.30× big,
    // 16 % tint, 30 px way – the new cover rides a spring like the island itself
    private const double SongResponse = 0.70, SongDamping = 0.60, SongPop = 1.30, SongTint = 0.16, SongWay = 30;
    private EventHandler _songFrame;

    private void CoverSwap(ImageSource old)
    {
        _trackPeekNotBefore = DateTime.Now.AddMilliseconds(1100);
        if (_songFrame != null) CompositionTarget.Rendering -= _songFrame;
        // (older key-frame animations would hold their values over the spring)
        LeadCoverScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        LeadCoverScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        LeadCoverShift.BeginAnimation(TranslateTransform.XProperty, null);
        LeadCoverGhostShift.BeginAnimation(TranslateTransform.XProperty, null);
        LeadCover.BeginAnimation(OpacityProperty, null);
        LeadCoverGhost.BeginAnimation(OpacityProperty, null);

        PillCoverGhost.ImageSource = old;
        LeadCoverGhost.Visibility = Visibility.Visible;
        const double start = 0.09;                                  // the new one follows the old one a moment later
        double outDur = Math.Max(0.15, SongResponse * 0.75);        // the old one slides out, speeding up
        double fadeIn = SongResponse * 0.6;
        var x = new Spring(SongWay, SongResponse, SongDamping, 0.05);
        x.Target = 0;
        var scale = new Spring(1, SongResponse * 0.8, SongDamping, 0.002);
        bool landed = false;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        double last = 0;
        void Apply(double t)
        {
            double k = Math.Min(1, t / outDur);
            LeadCoverGhostShift.X = -SongWay * k * k * k;
            LeadCoverGhost.Opacity = 1 - k;
            // polish 2026-11-15: the old cover also sinks back (smaller, softer) – depth instead of a flat slide
            LeadCoverGhostScale.ScaleX = LeadCoverGhostScale.ScaleY = 1 - 0.28 * k;
            if (S.Animations == AnimationLevel.An && k > 0.05)
            {
                if (LeadCoverGhost.Effect is not BlurEffect gb) LeadCoverGhost.Effect = gb = new BlurEffect { RenderingBias = RenderingBias.Performance };
                gb.Radius = 3.5 * k;
            }
            LeadCoverShift.X = t < start ? SongWay : x.Value;
            LeadCover.Opacity = t < start ? 0 : Math.Min(1, (t - start) / fadeIn);
            LeadCoverScale.ScaleX = LeadCoverScale.ScaleY = scale.Value;
        }
        Apply(0);
        _songFrame = (_, _) =>
        {
            double t = clock.Elapsed.TotalSeconds, dt = Math.Min(0.05, t - last);
            last = t;
            if (t >= start)
            {
                x.Step(dt);
                // close to its place the cover swells once and settles (an impulse – the spring brings it back)
                if (!landed && x.Value < SongWay * 0.35) { landed = true; scale.Velocity = (SongPop - 1) * 2 * Math.PI / SongResponse * 1.6; }
                scale.Step(dt);
            }
            Apply(t);
            if (t > Math.Max(outDur, start + fadeIn) && landed && x.Settled && scale.Settled || t > 3)
            {
                CompositionTarget.Rendering -= _songFrame;
                _songFrame = null;
                LeadCoverShift.X = 0; LeadCoverScale.ScaleX = LeadCoverScale.ScaleY = 1; LeadCover.Opacity = 1; _pausedLook = false; CoverPause.Opacity = 0;
                LeadCoverGhost.Visibility = Visibility.Collapsed; LeadCoverGhost.Opacity = 1; LeadCoverGhostShift.X = 0;
                LeadCoverGhostScale.ScaleX = LeadCoverGhostScale.ScaleY = 1; LeadCoverGhost.Effect = null;
            }
        };
        CompositionTarget.Rendering += _songFrame;
        if (S.Animations != AnimationLevel.Aus)
            Tint(H.Media.CoverColor.A > 0 ? H.Media.CoverColor : Color.FromRgb(0x3B, 0x6E, 0xA8), S.Animations == AnimationLevel.An ? SongTint : SongTint / 2);
    }

    /// <summary>The island wiggles once, briefly (island and bubbles together).</summary>
    private void Shake()
    {
        var k = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(380), FillBehavior = FillBehavior.Stop };
        foreach (var (x, ms) in new[] { (0.0, 0), (-3.0, 70), (3.0, 150), (-2.0, 230), (1.0, 300), (0.0, 380) })
            k.KeyFrames.Add(new EasingDoubleKeyFrame(x, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms))));
        _roomShift.BeginAnimation(TranslateTransform.XProperty, k);
    }

    /// <summary>
    /// Settings › Animationen › "Auf der Insel ansehen": the real island shows one moment with sample content.
    /// Nothing is saved, copied or opened – it is only the look.
    /// </summary>
    private static ImageSource DemoCover(Color a, Color b)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) dc.DrawRectangle(new LinearGradientBrush(a, b, 45), null, new Rect(0, 0, 64, 64));
        var r = new System.Windows.Media.Imaging.RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
        r.Render(dv);
        r.Freeze();
        return r;
    }

    public void DemoMoment(string key)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => DemoMoment(key))); return; }
        if (_tucked) Untuck();
        var green = ((SolidColorBrush)FindResource("Green")).Color;
        switch (key)
        {
            case "spring":
                Expand(null);
                After(1500, () => { if (_state == State.Expanded) Collapse(); });
                return;
        }
        if (_state == State.Expanded) Collapse();
        // the moments play on the closed island, so wait until it has closed
        After(_state == State.Collapsed ? 1 : 650, () =>
        {
            switch (key)
            {
                case "bell":
                    if (S.Animations != AnimationLevel.Aus) RingBell();
                    After(S.Animations != AnimationLevel.Aus ? 760 : 1, () => ShowPeek(new PeekInfo
                    {
                        Key = "notif", Icon = "bell.badge", Source = L.T("WhatsApp · jetzt"), Avatar = "A", AvatarBrush = AvatarColor("Anna"),
                        Title = "Anna", Text = L.T("Kommst du heute noch vorbei?"), TimeoutMs = 3500, Click = () => { }
                    }));
                    break;
                case "copy":
                    ShowHint("check", "Kopiert", (Brush)FindResource("Green"), key: "copy");
                    if (S.Animations != AnimationLevel.Aus) After(30, () =>
                    {
                        Squash();
                        var slide = new TranslateTransform();
                        HintIconCircle.RenderTransform = slide;
                        slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-18, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut } });
                    });
                    break;
                case "song":
                    // made-up covers, not the song that plays (nothing personal in the demos)
                    if (LeadCover.IsVisible)
                    {
                        var real = PillCover.ImageSource;
                        PillCover.ImageSource = DemoCover(Color.FromRgb(0x64, 0xD2, 0xFF), Color.FromRgb(0x30, 0xD1, 0x58));
                        CoverSwap(DemoCover(Color.FromRgb(0xFF, 0x9F, 0x0A), Color.FromRgb(0xFF, 0x37, 0x5F)));
                        After(2600, () => PillCover.ImageSource = H.Media.Cover ?? real);
                    }
                    else Tint(Color.FromRgb(0x3B, 0x6E, 0xA8), 0.3);
                    break;
                case "mute":
                    ShowHint("mic.slash", "Stumm", (Brush)FindResource("Danger"), key: "mic");
                    MuteMoment();
                    break;
                case "shot":
                {
                    var dv = new DrawingVisual();
                    using (var dc = dv.RenderOpen())
                    {
                        dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x2A, 0x4A, 0x7A), Color.FromRgb(0x6A, 0x3A, 0x6A), 45), null, new Rect(0, 0, 320, 180));
                        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)), null, new Rect(28, 32, 150, 10), 5, 5);
                        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)), null, new Rect(28, 52, 100, 10), 5, 5);
                    }
                    var img = new System.Windows.Media.Imaging.RenderTargetBitmap(320, 180, 96, 96, PixelFormats.Pbgra32);
                    img.Render(dv);
                    img.Freeze();
                    ShowPeek(new PeekInfo { Key = "shot", Shot = img, TimeoutMs = 0 });
                    break;
                }
                case "dl":
                    if (S.Animations == AnimationLevel.An) Sweep(Colors.White);
                    After(S.Animations == AnimationLevel.An ? 520 : 1, () => ShowHint("check", "Download fertig", (Brush)FindResource("Green"), key: "dl"));
                    break;
                case "claude":
                    if (S.Animations == AnimationLevel.An) Sweep(green);
                    After(S.Animations == AnimationLevel.An ? 520 : 1, () => ShowHint("check", "Claude ist fertig", (Brush)FindResource("Green"), key: "agentdone"));
                    break;
                case "timer":
                    if (S.Animations == AnimationLevel.An) Ripples(((SolidColorBrush)FindResource("Orange")).Color);
                    if (S.Animations != AnimationLevel.Aus) Wobble(); // alarm-clock wobble (2026-11-18)
                    break;
            }
        });
    }

    // =====================================================================
    //  Mitteilungen: Windows notifications · price alarms · suggestions
    // =====================================================================

    /// <summary>Unread notifications (blue dot) – they become read after ~1.5 s in view on the Mitteilungen page.</summary>
    private int _unreadNotif => H.Notifications.UnreadCount;
    private DispatcherTimer _readTimer;

    private bool LookingAtNotifications => _state == State.Expanded && _tab == "Notify" && SegNotif.IsChecked == true;

    private void ScheduleMarkRead()
    {
        _readTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _readTimer.Stop();
        _readTimer.Tick -= ReadTick;
        _readTimer.Tick += ReadTick;
        if (LookingAtNotifications && _unreadNotif > 0) _readTimer.Start();
    }

    private void ReadTick(object sender, EventArgs e)
    {
        _readTimer.Stop();
        if (!LookingAtNotifications) return;
        H.Notifications.MarkAllRead();
        UpdateNotifyUi();
    }

    /// <summary>
    /// Like the clipboard (user wish 2026-11-14): only as tall as what is there – 300 px at most, nothing there =
    /// the normal page height with the empty notice.
    /// </summary>
    private double NotifyHeight()
    {
        const double Chrome = 7 + 24 + 6 + 10, Words = 25;
        double h = Chrome + Words;
        if (SegAlarm?.IsChecked == true)
            h += 34 + (AlarmError?.Visibility == Visibility.Visible ? 18 : 0) + (H.Prices.Items.Count == 0 ? 40 : H.Prices.Items.Count * 50 + 4);
        else if (SegSuggest?.IsChecked == true)
            h += H.Suggestions.Items.Count == 0 ? 50 : H.Suggestions.Items.Count * 52 + 6;
        else
            h += H.Notifications.Items.Count == 0 ? 50 : H.Notifications.Items.Count * 44 + 8;
        return Math.Clamp(Math.Ceiling(h), ExpH, 300);
    }

    private void UpdateNotifyHeight()
    {
        if (_state != State.Expanded || _tab != "Notify") return;
        double h = TabHeight("Notify") + NotchExtra;
        if (Math.Abs(h - _targetH) > 0.5) AnimateSize(ExpW, h, ExpRadius, false);
    }

    private void UpdateNotifyUi()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(UpdateNotifyUi)); return; }
        UpdateNotifyHeight();
        NotifBadge.Visibility = _unreadNotif > 0 ? Visibility.Visible : Visibility.Collapsed;
        NotifClearWord.Text = _unreadNotif > 0 ? L.T("Alle gelesen") : L.T("Alle löschen");
        ScheduleMarkRead();
        UpdateTabBadges();
        int sug = H.Suggestions.Items.Count;
        SuggestBadge.Visibility = sug > 0 ? Visibility.Visible : Visibility.Collapsed;
        SuggestBadgeText.Text = sug.ToString();
        NotifEmpty.Visibility = H.Notifications.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AlarmEmpty.Visibility = H.Prices.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SuggestEmpty.Visibility = sug == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void NotifySeg_Checked(object sender, RoutedEventArgs e)
    {
        if (NotifPanel == null) return; // during XAML load
        var seg = (string)((FrameworkElement)sender).Tag;
        NotifPanel.Visibility = seg == "notif" ? Visibility.Visible : Visibility.Collapsed;
        AlarmPanel.Visibility = seg == "alarm" ? Visibility.Visible : Visibility.Collapsed;
        SuggestPanel.Visibility = seg == "suggest" ? Visibility.Visible : Visibility.Collapsed;
        NotifClearWord.Visibility = seg == "notif" ? Visibility.Visible : Visibility.Collapsed;
        Dispatcher.BeginInvoke(new Action(UpdateNotifyHeight), DispatcherPriority.Background); // each part has its own height
        if (seg == "alarm") Dispatcher.BeginInvoke(new Action(() => AlarmInput.Focus()), DispatcherPriority.Input);
        UpdateNotifyUi();
    }

    private void NotifClearWord_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        // first "Alle gelesen", once everything is read "Alle löschen"
        if (_unreadNotif > 0) { H.Notifications.MarkAllRead(); UpdateNotifyUi(); }
        else NotifClearAll_Click(sender, null);
    }

    /// <summary>Render test (the animation gallery): real notifications stay out of the film.</summary>
    private bool _demoOnly;

    private void OnNotificationArrived(IslandNotification n)
    {
        if (_demoOnly && n.Id > 0) return;
        if (_focusRun != null && !n.IsCall) { HoldForFocus(n); return; } // focus: collected quietly, shown at the end (1a)
        EyesNotice(); // the eyes look over to the bell
        UpdateNotifyUi(); // unread dot; read after a moment if the page is open
        if (n.IsCall)
        {
            // incoming call: important, stays a while, shows through a tucked island; answering happens in the app
            ShowPeek(new PeekInfo { Key = "call", Important = true, Image = n.Icon, Icon = "mic", GlyphBrush = (Brush)FindResource("Green"),
                                    Title = L.T($"Anruf · {(string.IsNullOrEmpty(n.Title) ? n.AppName : n.Title)}"), Text = n.AppName, TimeoutMs = 20000 }
                .Action(L.T("Öffnen"), () => H.Notifications.Open(n), true));
            return;
        }
        if (_state == State.Expanded) return; // it is in the list – no alert over the open island
        // user wish 2026-11-14: first the closed island reacts – the equalizer steps aside, a bell rings left/right –
        // then the island opens downwards with the message
        if (S.Animations != AnimationLevel.Aus && _state == State.Collapsed && Island.IsVisible && !_roomAway && !_tucked && !_bellRinging)
        {
            RingBell();
            After(760, () => ShowNotificationBanner(n));
            return;
        }
        ShowNotificationBanner(n);
    }

    private bool _bellRinging;

    /// <summary>The pill's right side (equalizer / text) fades away, a small bell pops in and swings left–right.</summary>
    private void RingBell()
    {
        _bellRinging = true;
        var trail = new FrameworkElement[] { Equalizer, TrailText, TrailLive };
        var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        foreach (var t in trail) t.BeginAnimation(OpacityProperty, fadeOut);
        TrailBell.Visibility = Visibility.Visible;
        TrailBell.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)) { BeginTime = TimeSpan.FromMilliseconds(70) });
        var pop = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(260) };
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(0.5, KeyTime.FromPercent(0)));
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(0.5, KeyTime.FromPercent(0.27)));
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1), new BackEase { Amplitude = 0.6, EasingMode = EasingMode.EaseOut }));
        TrailBellScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        TrailBellScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        // swinging from its top like a real bell, dying out
        var swing = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(760) };
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        foreach (var (a, ms) in new[] { (0.0, 0), (0.0, 190), (-18.0, 260), (16.0, 350), (-12.0, 440), (8.0, 530), (-4.0, 610), (0.0, 690) })
            swing.KeyFrames.Add(new EasingDoubleKeyFrame(a, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)), ease));
        TrailBellTurn.BeginAnimation(RotateTransform.AngleProperty, swing);
        // the banner takes over at ~760 ms; once the pill is gone, everything goes back to normal for later
        After(1300, () =>
        {
            _bellRinging = false;
            TrailBell.BeginAnimation(OpacityProperty, null);
            TrailBell.Opacity = 0;
            TrailBell.Visibility = Visibility.Collapsed;
            TrailBellScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            TrailBellScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            TrailBellTurn.BeginAnimation(RotateTransform.AngleProperty, null);
            foreach (var t in trail) { t.BeginAnimation(OpacityProperty, null); t.Opacity = 1; }
        });
    }

    /// <summary>
    /// A page in the browser asks for a verification code (user wish 2026-11-14): the mail tab comes up (an open one
    /// is selected, otherwise the mail address opens), so the code is right there.
    /// </summary>
    private void OnCodeWanted(IntPtr browser, string title)
    {
        if (!S.CodeMail) return;
        MakeRoomService.OpenMail(browser, S.MailUrl);
        ShowHint("envelope", "E-Mails geöffnet", (Brush)FindResource("Blue"), key: "codemail");
    }

    /// <summary>A notification that carries a code: big code, one click copies it.</summary>
    private void ShowCodeBanner(IslandNotification n, string code)
    {
        void Copy()
        {
            try { Clipboard.SetText(code); } catch { }
            n.Unread = false;
            ShowHint("check", "Code kopiert", (Brush)FindResource("Green"), key: "copy");
        }
        var shown = code.Length == 6 ? $"{code[..3]} {code[3..]}" : code;
        ShowPeek(new PeekInfo
            {
                Key = "notif", Image = n.Icon, Icon = "lock", Source = L.T($"{n.AppName} · Code"), SourceIcon = "lock",
                Title = shown, Text = string.IsNullOrWhiteSpace(n.Title) ? n.AppName : n.Title, TimeoutMs = 15000, Click = Copy
            }
            .Button("copy", "Kopieren", Copy, (Brush)FindResource("Green"), darkGlyph: true));
    }

    private void ShowNotificationBanner(IslandNotification n)
    {
        if (_state == State.Expanded) return; // opened meanwhile – it is in the list
        if (S.CodeMail && NotificationService.FindCode(n.Title, n.Text) is { } code) { ShowCodeBanner(n, code); return; }
        bool messenger = Regex.IsMatch(n.AppName ?? "", "WhatsApp|Telegram|Signal|Discord|Messenger|Teams|Slack", RegexOptions.IgnoreCase);
        bool person = messenger && !string.IsNullOrWhiteSpace(n.Title);
        // user wish 2026-11-15: after the bell a one-line ticker (the island only grows sideways); the pointer on it
        // opens the full card – app on top, the person, two lines, "Gelesen" / "Öffnen", a line that runs down.
        // A newer message replaces it and counts up; when the card is already open the new one stays a card.
        _peekNotif = n; // going into its app lets the card drop away (IslandWindow.NotifyDetach.cs)
        bool open = _state == State.Peek && _peek is { Key: "notif", Ticker: true, Open: true };
        int count = _state == State.Peek && _peek is { Key: "notif" } cur ? cur.Count + 1 : 1;
        ShowPeek(new PeekInfo
            {
                Key = "notif", Image = n.Icon, Icon = "bell.badge", Ticker = true, Open = open, Count = count,
                Source = count > 1 ? L.T($"{n.AppName} · jetzt · {count} neue") : L.T($"{n.AppName} · jetzt"),
                Avatar = person ? n.Title.Trim()[..1].ToUpperInvariant() : null, AvatarBrush = person ? AvatarColor(n.Title) : null,
                Title = person ? n.Title : string.IsNullOrWhiteSpace(n.Title) ? n.AppName : n.Title,
                Text = n.Text, TimeoutMs = messenger ? 6000 : 4500,
                Click = () => { H.Notifications.Open(n); n.Unread = false; },
                Reply = person ? text => { n.Unread = false; _ = ReplyService.SendAsync(n, text, S.ReplyAutoSend); } : null
            }
            .Button("check", L.T("Gelesen"), () => { SuckPeekUp(); n.Unread = false; UpdateNotifyUi(); })
            .Button("open", "Öffnen", () => { H.Notifications.Open(n); n.Unread = false; }, (Brush)FindResource("Green"), darkGlyph: true));
    }

    /// <summary>A steady colour per person (like iOS contact circles without a photo).</summary>
    private static Brush AvatarColor(string name)
    {
        var palette = new[] { 0x5865F2, 0x30B0C7, 0x34C759, 0xFF9500, 0xAF52DE, 0xFF2D55, 0x5AC8FA, 0xA2845E };
        int h = 0;
        foreach (var ch in name ?? "") h = h * 31 + ch;
        int c = palette[Math.Abs(h % palette.Length)];
        var b = new SolidColorBrush(Color.FromRgb((byte)(c >> 16), (byte)(c >> 8), (byte)c));
        b.Freeze();
        return b;
    }

    private void NotifItem_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not IslandNotification n) return;
        Collapse();
        H.Notifications.Open(n);
    }

    private void NotifDismiss_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is IslandNotification n) DismissNotification(n, sender);
    }

    private void NotifClearAll_Click(object sender, RoutedEventArgs e) => H.Notifications.ClearAll();

    // ---------- price alarms ----------

    private void AlarmInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; AlarmAdd_Click(null, null); }
    }

    private async void AlarmAdd_Click(object sender, RoutedEventArgs e)
    {
        AlarmError.Visibility = Visibility.Collapsed;
        AlarmAddBtn.IsEnabled = false;
        AlarmAddBtn.Opacity = 0.4;
        try
        {
            var (alert, error) = await H.Prices.AddAsync(AlarmInput.Text, AlarmRule.Text);
            if (alert == null)
            {
                AlarmError.Text = error;
                AlarmError.Visibility = Visibility.Visible;
                return;
            }
            AlarmInput.Text = AlarmRule.Text = "";
        }
        catch (Exception ex)
        {
            App.Log(ex);
            AlarmError.Text = "Das hat nicht geklappt – versuch es nochmal";
            AlarmError.Visibility = Visibility.Visible;
        }
        finally
        {
            AlarmAddBtn.IsEnabled = true;
            AlarmAddBtn.Opacity = 1;
        }
    }

    private void AlarmItem_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PriceAlert a || string.IsNullOrEmpty(a.Url)) return;
        Collapse();
        OpenUrl(a.Url);
    }

    private void AlarmDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PriceAlert a) H.Prices.Remove(a);
    }

    private void OnPriceAlarm(PriceAlert a, string msg)
    {
        var green = (Brush)FindResource("Green");
        H.Notifications.AddOwn(a.Name, msg, a.IconKind, green, a.Url);
        ShowPeek(new PeekInfo { Key = "price:" + a.Id, Important = true, Icon = a.IconKind, GlyphBrush = green, Title = a.Name, Text = msg, TimeoutMs = 9000 }
            .Action(L.T("Öffnen"), () => OpenUrl(a.Url), true));
    }

    private static void OpenUrl(string url)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception ex) { App.Log(ex); }
    }

    // ---------- suggestions ----------

    private void OnSuggested(Suggestion s)
    {
        UpdateNotifyUi();
        ShowHint(s.IconKind, s.Text, (Brush)FindResource("Blue"), key: "suggest",
                 actions: new List<(string, string, Brush, Action)>
                 {
                     (null, L.T("Aktivieren"), null, () => _ = ActivateSuggestion(s)),
                     ("xmark", "Nein danke", (Brush)FindResource("Danger"), () => H.Suggestions.Remove(s))
                 });
    }

    private async Task ActivateSuggestion(Suggestion s)
    {
        H.Suggestions.Remove(s);
        var (alert, error) = await H.Prices.AddAsync(s.Input, s.Rule);
        if (alert != null) ShowHint("check", "Alarm gesetzt", (Brush)FindResource("Green"), check: true, key: "suggest");
        else ShowHint("xmark", error ?? "Nicht geklappt", (Brush)FindResource("Danger"), key: "suggest");
    }

    private void SuggestActivate_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Suggestion s) _ = ActivateSuggestion(s);
    }

    private void SuggestDismiss_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Suggestion s) H.Suggestions.Remove(s);
    }

    private void OpenSpotify_Click(object sender, RoutedEventArgs e)
    {
        Collapse();
        try { Process.Start(new ProcessStartInfo("spotify:") { UseShellExecute = true }); }
        catch { OpenUrl("https://open.spotify.com"); } // app not installed → the web player
    }

    // =====================================================================
    //  OBS · Steam · Spotify queue
    // =====================================================================

    private static readonly SolidColorBrush SteamBrush = new(Color.FromRgb(0x66, 0xC0, 0xF4));

    private void UpdateObsUi()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(UpdateObsUi)); return; }
        var o = H.Obs;
        var obsVis = o.Connected && !IsTabHidden("Obs") ? Visibility.Visible : Visibility.Collapsed;
        if (ObsTabBtn.Visibility != obsVis) { ObsTabBtn.Visibility = obsVis; UpdateTabDots(); }
        UpdateTabBadges();
        if (!o.Connected && _tab == "Obs") SelectTab("Music", false);
        var parts = new List<string>();
        if (o.Recording) parts.Add(L.T($"● Aufnahme {o.RecordTime}"));
        if (o.Streaming) parts.Add(L.T($"Stream {o.StreamTime}"));
        ObsStatus.Text = parts.Count > 0 ? string.Join(" · ", parts) : o.Connected ? L.T("Bereit") : o.Status;
        ObsStatus.Foreground = o.Recording ? (Brush)FindResource("Danger") : o.Streaming ? (Brush)FindResource("Green") : (Brush)FindResource("SubText");
        ObsRecBtn.Content = o.Recording ? "Stopp" : L.T("Aufnahme");
        Ext.SetIcon(ObsRecBtn, o.Recording ? "stop" : "record");
        if (o.Recording) ObsRecBtn.Background = (Brush)FindResource("Danger"); else ObsRecBtn.ClearValue(BackgroundProperty);
        ObsStreamBtn.Content = o.Streaming ? L.T("Live beenden") : L.T("Live gehen");
        if (o.Streaming) ObsStreamBtn.Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x30, 0xD1, 0x58)); else ObsStreamBtn.ClearValue(BackgroundProperty);

        // scene chips – the active one white
        if (ObsScenePanel.Children.Count != o.Scenes.Count || ObsScenePanel.Children.OfType<Button>().Select(b => (string)b.Tag).Where((t, i) => t != o.Scenes[i]).Any())
        {
            ObsScenePanel.Children.Clear();
            foreach (var s in o.Scenes)
            {
                var b = new Button { Content = s, Tag = s, Height = 22, Padding = new Thickness(9, 0, 9, 0), Margin = new Thickness(0, 0, 6, 6), FontSize = 11 };
                b.SetResourceReference(StyleProperty, "PillButton");
                b.Click += async (_, _) => await H.Obs.SetScene(s);
                ObsScenePanel.Children.Add(b);
            }
        }
        foreach (Button b in ObsScenePanel.Children)
        {
            bool on = (string)b.Tag == o.Scene;
            if (on) { b.Background = Brushes.White; b.Foreground = Brushes.Black; }
            else { b.ClearValue(BackgroundProperty); b.ClearValue(ForegroundProperty); }
        }
    }

    private async void ObsRec_Click(object sender, RoutedEventArgs e) => await H.Obs.ToggleRecord();
    private async void ObsStream_Click(object sender, RoutedEventArgs e) => await H.Obs.ToggleStream();

    private DateTime _steamDoneUntil;
    private bool _steamFinishing;
    private double _steamLastProgress;

    /// <summary>
    /// Download done (user wish 2026-11-14): the island takes part – a light shoots across it to the download ring,
    /// there the ring closes in green, the bubble swells once and shows a check; then the "ready" banner.
    /// </summary>
    private void OnSteamFinished(string name, string appId)
    {
        _lastDl = H.Steam;
        OnDownloadDone(() => ShowSteamReady(name, appId));
    }

    /// <summary>A browser download is done: the same moment as Steam's, then a banner to open the file or its folder.</summary>
    private void OnFileDownloaded(string name, string path, long size)
    {
        DropToTaskbar(path); // its icon drops out of the island towards the taskbar
        _lastDl = H.Downloads;
        _steamLastProgress = 1;
        OnDownloadDone(() => ShowPeek(new PeekInfo
            {
                Key = "filedl", Icon = "download", GlyphBrush = (Brush)FindResource("Green"), Image = ShellIcon.Get(path),
                Source = "Download", SourceIcon = "download", SourceBrush = SteamBrush,
                Title = name, Text = L.T($"{BrowserDownloadService.Bytes(size)} · fertig"), TimeoutMs = 6000,
                Click = () => OpenPath(path)
            }
            .Button("folder", "Im Ordner zeigen", () => { try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { } })
            .Button("open", "Öffnen", () => OpenPath(path), (Brush)FindResource("Green"), darkGlyph: true)));
    }

    private static void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception ex) { App.Log(ex); }
    }

    private void OnDownloadDone(Action banner)
    {
        if (S.Animations == AnimationLevel.Aus || _state != State.Collapsed) { banner(); return; }
        _steamDoneUntil = DateTime.Now.AddSeconds(3.5);
        _steamFinishing = true;
        UpdatePill(); // keeps the (still blue) ring next to the island for the moment
        bool light = S.Animations == AnimationLevel.An;
        if (light) Sweep(((SolidColorBrush)SteamBrush).Color);
        After(light ? 520 : 1, () =>
        {
            var (ring, icon) = Bubble.Tag is Act.Steam ? (BubbleRing, BubbleIcon) : Bubble2.Tag is Act.Steam ? (Bubble2Ring, Bubble2Icon) : (null, null);
            double before = ring?.Value ?? 0;
            _steamFinishing = false;
            UpdatePill(); // → green ring with a check
            if (ring != null)
            {
                ring.BeginAnimation(Ring.ValueProperty, new DoubleAnimation(Math.Min(before, 99), 100, TimeSpan.FromMilliseconds(380))
                    { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
                Pop(icon);
                Swell(Act.Steam, 1.18);
                GlowBubble(Act.Steam, (Brush)FindResource("Green"));
            }
        });
        After(1400, banner);
        After(3700, () => UpdatePill());
    }

    private void ShowSteamReady(string name, string appId)
    {
        // banner with the game's cover, size and how long it took; green play button (approved 2026-11-14)
        ImageSource cover = null;
        try
        {
            if (H.Steam.CoverFile(appId) is { } file)
            {
                var bi = new System.Windows.Media.Imaging.BitmapImage();
                bi.BeginInit(); bi.UriSource = new Uri(file); bi.DecodePixelWidth = 96; bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bi.EndInit(); bi.Freeze();
                cover = bi;
            }
        }
        catch { }
        var parts = new List<string>();
        if (H.Steam.LastSizeBytes > 0) parts.Add(L.T($"{FormatBytes(H.Steam.LastSizeBytes)} geladen"));
        var dur = H.Steam.LastDuration;
        if (dur.TotalMinutes >= 1) parts.Add(dur.TotalHours >= 1 ? L.T($"in {(int)dur.TotalHours} Std {dur.Minutes} Min") : L.T($"in {(int)dur.TotalMinutes} Min"));
        ShowPeek(new PeekInfo
            {
                Key = "steam", Image = cover, Cover = cover != null, Icon = "download", GlyphBrush = SteamBrush,
                Source = "Steam", SourceIcon = "download", SourceBrush = SteamBrush,
                Title = L.T($"{name} ist bereit"), Text = parts.Count > 0 ? string.Join(" ", parts) : L.T("Fertig geladen"), TimeoutMs = 7000
            }
            .Button("play.fill", "Spielen", () => SteamService.Play(appId), (Brush)FindResource("Green"), darkGlyph: true));
    }

    private async void MusicQueue_Click(object sender, RoutedEventArgs e)
    {
        if (!H.Spotify.Connected)
        {
            ShowPeek(new PeekInfo { Icon = "checklist", Title = L.T("Spotify verbinden"), Text = L.T("Für die Warteschlange einmal deine Spotify-App in den Einstellungen verbinden."), TimeoutMs = 6000 }
                .Action(L.T("Einstellungen"), () => SettingsWindow.ShowSingle("General"), true));
            return;
        }
        if (!_queueOpen && _lyricsOpen) MusicLyrics_Click(null, null); // one of the two at a time
        _queueOpen = !_queueOpen;
        if (_queueOpen) ApplyQueueMode();
        QueueView.Visibility = _queueOpen ? Visibility.Visible : Visibility.Collapsed;
        MusicContent.VerticalAlignment = _queueOpen ? VerticalAlignment.Top : VerticalAlignment.Center;
        MusicQueueBtn.Foreground = _queueOpen ? (Brush)FindResource("Green") : null;
        if (MusicQueueBtn.Foreground == null) MusicQueueBtn.ClearValue(ForegroundProperty);
        AnimateSize(ExpW, TabHeight(_tab) + NotchExtra, ExpRadius, true);
        if (_queueOpen)
        {
            QueueHint.Text = L.T("lädt …");
            await RefreshQueueNow();
            QueueHint.Text = H.Spotify.Queue.Count == 0 ? "leer" : L.T($"{H.Spotify.Queue.Count} Titel");
        }
    }

    private bool _queueSkipping;

    /// <summary>Queue as Spotify has it for the song that is playing right now.</summary>
    private async Task RefreshQueueNow()
    {
        QueueHint.Text = L.T("lädt …");
        await H.Spotify.RefreshQueueAsync(H.Media.Title);
        QueueHint.Text = H.Spotify.Queue.Count == 0 ? "leer" : L.T($"{H.Spotify.Queue.Count} Titel");
    }

    /// <summary>Click on a song in the queue: skip forward until it plays (same as pressing "next" that often).</summary>
    private async void QueueItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (_queueSkipping || (sender as FrameworkElement)?.DataContext is not QueueItem item) return;
        int skips = H.Spotify.Queue.IndexOf(item) + 1;
        if (skips <= 0) return;
        _queueSkipping = true;
        QueueHint.Text = L.T($"springe zu „{item.Name}“ …");
        try
        {
            for (int i = 0; i < skips; i++)
            {
                var before = H.Media.Title;
                H.Media.Next();
                // wait until the player really moved on (max ~1.5 s per step)
                for (int w = 0; w < 15 && H.Media.Title == before; w++) await Task.Delay(100);
                await Task.Delay(120);
            }
        }
        finally { _queueSkipping = false; }
        await RefreshQueueNow();
    }

    /// <summary>The stack was switched on (island, tray, settings or hotkey) – what you copied just before is not in it
    /// yet. User 2026-11-16: "wenn man ohne den Modus kopiert, dann funktioniert das nicht". The island offers the
    /// copies of the last 10 minutes (oldest first, at most 9); one click and they are the first in the stack.</summary>
    private void OfferRecentCopies()
    {
        var recent = H.Clipboard.RecentCopies(TimeSpan.FromMinutes(10));
        if (recent.Count == 0) return;
        // after the short "Stapel an" (when it came from the island's own switch)
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            await Task.Delay(1100);
            if (!H.Clipboard.QueueMode) return;
            recent = H.Clipboard.RecentCopies(TimeSpan.FromMinutes(10)).Where(i => !H.Clipboard.Queue.Contains(i)).ToList();
            if (recent.Count == 0) return;
            var p = new PeekInfo
            {
                Key = "queue-offer", Source = L.T("Zwischenablage"), SourceIcon = "stack.fill", Icon = "stack.fill",
                GlyphBrush = (Brush)FindResource("Accent"),
                Title = recent.Count == 1 ? L.T("Letzte Kopie mit dazu?") : L.T($"{recent.Count} Kopien von eben dazu?"),
                Text = L.T("kommen zuerst – so, wie du sie kopiert hast"),
                TimeoutMs = 9000
            };
            p.Button("plus", "Mit hinein", () => H.Clipboard.AddToQueueFront(recent), (Brush)FindResource("Accent"));
            ShowPeek(p);
        }));
    }

    private void SetQueueMode(bool on)
    {
        H.Clipboard.QueueMode = on;
        QueueToggle.IsChecked = on;
        Ext.SetIcon(QueueToggle, on ? "stack.fill" : "stack");
        ShowHint("stack", on ? "Stapel an" : "Stapel aus", on ? (Brush)FindResource("Accent") : null, key: "queue");
        UpdatePill();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_state != State.Expanded) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            if (ClipConfirm.Visibility == Visibility.Visible) HideClipConfirm();
            else if (ClipSearch.IsKeyboardFocused && ClipSearch.Text.Length > 0) ClipSearch.Text = "";
            else Collapse();
            e.Handled = true;
            return;
        }
        // Strg+Z: back with what was just deleted (outside of text fields – those undo their own typing)
        if (key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control && _undo != null && Keyboard.FocusedElement is not TextBox)
        {
            DoUndo();
            e.Handled = true;
            return;
        }
        if (key == Key.Tab && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            StepTab(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1, true);
            FocusForTab();
            e.Handled = true;
            return;
        }

        bool inText = Keyboard.FocusedElement is TextBox;
        if (_tab == "Clip" && !inText)
        {
            int digit = key is >= Key.D1 and <= Key.D9 ? key - Key.D0 : key is >= Key.NumPad1 and <= Key.NumPad9 ? key - Key.NumPad0 : 0;
            if (digit > 0 && Keyboard.Modifiers == ModifierKeys.None)
            {
                var item = _clipView.Cast<ClipItem>().Skip(digit - 1).FirstOrDefault();
                if (item != null) _ = UseClip(item, S.ClickPastes);
                e.Handled = true;
                return;
            }
            if (key == Key.Enter && ClipList.SelectedItem is ClipItem sel) { _ = UseClip(sel, S.ClickPastes); e.Handled = true; return; }
            if (key == Key.Delete && ClipList.SelectedItem is ClipItem del) { DeleteClip(del); e.Handled = true; return; }
            if (key == Key.P && Keyboard.Modifiers == ModifierKeys.None && ClipList.SelectedItem is ClipItem pin) { H.Clipboard.TogglePin(pin); RefreshClipView(); e.Handled = true; return; }
            if (key is >= Key.A and <= Key.Z && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift) { ClipSearch.Focus(); return; }
        }
        if (_tab == "Music" && !inText)
        {
            if (key == Key.Space) { H.Media.PlayPause(); e.Handled = true; }
            else if (key == Key.Left) { Skip(-1); e.Handled = true; }
            else if (key == Key.Right) { Skip(+1); e.Handled = true; }
            return;
        }

        // everything with the keyboard (2026-11-17)
        if (inText || Keyboard.Modifiers != ModifierKeys.None) return;
        // the clipboard list uses ← → itself (strip / tiles); vertical lists don't
        bool inList = Keyboard.FocusedElement is DependencyObject fd && (fd == ClipList || ItemsControl.ItemsControlFromItemContainer(fd) == ClipList);
        if ((key == Key.Left || key == Key.Right) && !inList)
        {
            // ← → walk through the tabs (on Musik they skip songs, as before)
            StepTab(key == Key.Left ? -1 : 1, false);
            FocusForTab();
            e.Handled = true;
            return;
        }
        if (_tab == "Notify" && NotifList.SelectedItem is IslandNotification ns)
        {
            if (key == Key.Enter) { H.Notifications.Open(ns); ns.Unread = false; e.Handled = true; return; }
            if (key == Key.Delete) { DismissNotification(ns); e.Handled = true; return; }
        }
        // typing starts writing where the page has a field (the key itself lands in it)
        if (key is >= Key.A and <= Key.Z or >= Key.D0 and <= Key.D9)
        {
            TextBox box = _tab switch { "Notes" => NoteInput, "Ai" => AiInput, "Remind" => RemindText, _ => null };
            if (box != null) { box.Focus(); box.CaretIndex = box.Text.Length; }
        }
    }

    // =====================================================================
    //  Tabs: centered carousel, the mouse wheel moves it
    // =====================================================================

    private void SelectTab(string tab, bool save, bool animate = true)
    {
        // a copy: checking the Timer tab puts the Timer | Wecker switch into this bar (the list must not change under us)
        foreach (var rb in TabBar.Children.OfType<RadioButton>().ToList())
            if ((string)rb.Tag == tab && rb.IsChecked != true) rb.IsChecked = true;
        if (save) { S.LastTab = tab; App.SaveSettings(); }
    }

    private void StepTab(int dir, bool wrap)
    {
        // in the order the bar shows them (style B groups them differently); Live / OBS only while they are there
        var tabs = VisibleTabButtons().Select(r => (string)r.Tag).ToArray();
        if (tabs.Length == 0) return;
        int i = Array.IndexOf(tabs, _tab) + dir;
        if (wrap) i = (i + tabs.Length) % tabs.Length;
        if (i < 0 || i >= tabs.Length) return;
        SelectTab(tabs[i], true);
    }

    /// <summary>Every wheel notch moves exactly one tab – no throttling, fast scrolling is never swallowed.</summary>
    private void TabBar_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        _wheelAcc += e.Delta; // touchpads send small deltas, so accumulate to whole notches
        while (_wheelAcc >= 120) { StepTab(-1, false); _wheelAcc -= 120; }
        while (_wheelAcc <= -120) { StepTab(1, false); _wheelAcc += 120; }
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        var tab = (string)((RadioButton)sender).Tag;
        bool changed = tab != _tab;
        // page switch with a direction: a picture of the old page, taken before it is hidden
        int slideDir = 0;
        System.Windows.Media.Imaging.BitmapSource oldPage = null;
        // fast wheel over several tabs: while a slide still runs no new picture is taken – the moving page just becomes
        // the newest one and glides on to its place (2026-11-18)
        if (changed && _state == State.Expanded && S.Animations != AnimationLevel.Aus && !_sliding)
        {
            var order = VisibleTabButtons().Select(r => (string)r.Tag).ToList();
            slideDir = Math.Sign(order.IndexOf(tab) - order.IndexOf(_tab));
            oldPage = SnapPage();
        }
        _tab = tab;
        TabLive.Visibility = tab == "Live" ? Visibility.Visible : Visibility.Collapsed;
        TabMusic.Visibility = tab == "Music" ? Visibility.Visible : Visibility.Collapsed;
        TabAgent.Visibility = tab == "Agent" ? Visibility.Visible : Visibility.Collapsed;
        TabAi.Visibility = tab == "Ai" ? Visibility.Visible : Visibility.Collapsed;
        TabSend.Visibility = tab == "Send" ? Visibility.Visible : Visibility.Collapsed;
        TabObs.Visibility = tab == "Obs" ? Visibility.Visible : Visibility.Collapsed;
        TabNotify.Visibility = tab == "Notify" ? Visibility.Visible : Visibility.Collapsed;
        TabClip.Visibility = tab == "Clip" ? Visibility.Visible : Visibility.Collapsed;
        TabShelf.Visibility = tab == "Shelf" ? Visibility.Visible : Visibility.Collapsed;
        TabGames.Visibility = tab == "Games" ? Visibility.Visible : Visibility.Collapsed;
        TabTimer.Visibility = tab == "Timer" ? Visibility.Visible : Visibility.Collapsed;
        TabRemind.Visibility = tab == "Remind" ? Visibility.Visible : Visibility.Collapsed;
        TabNotes.Visibility = tab == "Notes" ? Visibility.Visible : Visibility.Collapsed;
        TabToday.Visibility = tab == "Today" ? Visibility.Visible : Visibility.Collapsed;
        TabControls.Visibility = tab == "Controls" ? Visibility.Visible : Visibility.Collapsed;
        if (!_autoOpening) S.LastTab = tab;
        if (_tabNames != null && _tabNames.TryGetValue(tab, out var title)) TabTitle.Text = title;
        UpdateShelfTab();
        if (changed && _state == State.Expanded && _autoTab != null && tab != _autoTab) { ForgetMoment(_autoTab); _autoTab = null; }
        _indicatorAnimate = changed && _state == State.Expanded; // the bar glides along (placed in UpdateTabDots)
        UpdateTabDots();
        if (changed && _state == State.Expanded && S.TabBarAutoHide)
        {
            // switching by wheel / keyboard / shortcut shows the bar for a moment
            _headerPinUntil = DateTime.Now.AddMilliseconds(1300);
            UpdateHeaderFade();
        }
        if (_state == State.Expanded && changed)
        {
            // only the clipboard is taller – every other tab keeps the same size
            AnimateSize(ExpW, TabHeight(tab) + NotchExtra, ExpRadius, true);
            if (oldPage != null && slideDir != 0) PageSlide(slideDir, oldPage);
            else if (!_sliding) Pages.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            OnTabShown();
        }
    }

    private void OnTabShown()
    {
        switch (_tab)
        {
            case "Music": UpdateMusicUi(); H.Audio.Refresh(); break;
            case "Clip": RefreshClipView(); foreach (var i in H.Clipboard.Items) i.RefreshMeta(); break;
            case "Shelf": H.Shelf.PruneMissing(); UpdateShelfUi(); break;
            case "Timer": UpdateTimerUi(); break;
            case "Today": UpdateTodayUi(); break;
            case "Live": UpdateLiveUi(); break;
            case "Agent": UpdateAgentUi(); break;
            case "Ai": UpdateAiUi(); break;
            case "Send": UpdateSendUi(); _ = H.LocalSend.Announce(); break;
            case "Notes": UpdateNotesUi(); break;
            case "Games": UpdateGamesUi(); break;
            case "Obs": UpdateObsUi(); break;
            case "Notify": UpdateNotifyUi(); break;
            case "Controls": H.Audio.Refresh(); _ = H.Brightness.RefreshAsync(); UpdateControlsUi(); break;
        }
    }

    // =====================================================================
    //  Tab bar look (approved mockup 2026-11-14): A "leise" (default), B "Gruppen", C "Titelzeile";
    //  optional "D" for all of them: fades to page dots while the pointer is not on the bar
    // =====================================================================

    private static readonly string[] FlatTabOrder = { "Live", "Obs", "Music", "Clip", "Shelf", "Send", "Notes", "Agent", "Ai", "Games", "Notify", L.T("Timer"), "Today", "Remind", "Controls" };
    private static readonly string[] GroupTabOrder = { "Live", "Obs", "Music", "|", "Clip", "Shelf", "Send", "Notes", "Games", L.T("Timer"), "Today", "Remind", "|", "Agent", "Ai", "Notify", "Controls" };
    private static readonly SolidColorBrush LiveBadge = Frozen(Color.FromRgb(0x30, 0xD1, 0x58));
    private static readonly SolidColorBrush RedBadge = Frozen(Color.FromRgb(0xFF, 0x45, 0x3A));
    private static readonly SolidColorBrush DotBrush = Frozen(Color.FromRgb(0x48, 0x48, 0x4A));
    private Dictionary<string, RadioButton> _tabBtns;
    private Dictionary<string, string> _tabNames;
    private DateTime _headerPinUntil;
    private bool _headerVisible = true;

    private static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    /// <summary>Switched off in Settings › Erscheinungsbild › Reihenfolge der Tabs.</summary>
    private static bool IsTabHidden(string tab) => S.HiddenTabs.Contains(tab);

    private static readonly Dictionary<string, int> TabGroup = new()
    {
        ["Live"] = 0, ["Obs"] = 0, ["Music"] = 0, ["Clip"] = 1, ["Shelf"] = 1, ["Send"] = 1, ["Notes"] = 1, ["Games"] = 1, ["Timer"] = 1, ["Today"] = 1, ["Remind"] = 1,
        ["Agent"] = 2, ["Ai"] = 2, ["Notify"] = 2, ["Controls"] = 2
    };

    private static IEnumerable<string> WithGroupDots(List<string> order)
    {
        int last = -1;
        foreach (var t in order)
        {
            int g = TabGroup.TryGetValue(t, out var x) ? x : 1;
            if (last >= 0 && g != last) yield return "|";
            last = g;
            yield return t;
        }
    }

    private IEnumerable<RadioButton> VisibleTabButtons() =>
        TabBar.Children.OfType<RadioButton>().Where(r => r.Visibility == Visibility.Visible).ToList();

    private void ApplyTabStyle()
    {
        if (_tabBtns == null)
        {
            _tabBtns = TabBar.Children.OfType<RadioButton>().ToDictionary(r => (string)r.Tag);
            _tabNames = _tabBtns.ToDictionary(p => p.Key, p => p.Value.Content as string ?? p.Key);
        }
        var style = S.TabBar;
        var look = (Style)FindResource(style switch { TabBarStyle.Gruppen => "TabButton", TabBarStyle.Titel => "TabButtonC", _ => "TabButtonA" });

        TabBar.Children.Clear();
        // the user's own order from Settings (dragged there) – else the built-in one; B puts a dot where the group changes
        var custom = S.TabOrder.Where(_tabBtns.ContainsKey).ToList();
        foreach (var k in FlatTabOrder) if (custom.Count > 0 && !custom.Contains(k)) custom.Add(k);
        IEnumerable<string> order = custom.Count > 0
            ? (style == TabBarStyle.Gruppen ? WithGroupDots(custom) : custom)
            : (style == TabBarStyle.Gruppen ? GroupTabOrder : FlatTabOrder);
        foreach (var t in order)
        {
            if (t == "|")
            {
                TabBar.Children.Add(new Ellipse { Width = 3, Height = 3, Fill = DotBrush, Margin = new Thickness(5, 0, 5, 3), VerticalAlignment = VerticalAlignment.Center });
                continue;
            }
            _tabBtns[t].Style = look;
            if (IsTabHidden(t)) _tabBtns[t].Visibility = Visibility.Collapsed;
            else if (t is not ("Live" or "Obs" or "Shelf")) _tabBtns[t].Visibility = Visibility.Visible;
            TabBar.Children.Add(_tabBtns[t]);
        }
        UpdateShelfTab();
        if (IsTabHidden(_tab) && _state == State.Expanded) SelectTab(VisibleTabButtons().Select(r => (string)r.Tag).FirstOrDefault() ?? "Music", false);

        TabBar.HorizontalAlignment = style == TabBarStyle.Titel ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        TabTitle.Visibility = style == TabBarStyle.Titel ? Visibility.Visible : Visibility.Collapsed;
        TabTitle.Text = _tabNames.TryGetValue(_tab, out var title) ? title : "";
        TabDivider.Visibility = style == TabBarStyle.Gruppen ? Visibility.Collapsed : Visibility.Visible;
        double toolW = style switch { TabBarStyle.Gruppen => 27, TabBarStyle.Titel => 21, _ => 22 };
        double toolIcon = style switch { TabBarStyle.Gruppen => 15, TabBarStyle.Titel => 12, _ => 13 };
        foreach (var b in new[] { GearBtn, TuckBtn })
        {
            b.Width = toolW;
            Ext.SetIconSize(b, toolIcon);
            b.Opacity = style == TabBarStyle.Gruppen ? 1 : 0.8;
        }
        UpdateTabBadges();
        UpdateTabDots();
        UpdateHeaderFade(true);
    }

    /// <summary>Page dots for "D" – and in style A the active tab's name only while it still fits.</summary>
    private void UpdateTabDots()
    {
        if (_tabBtns == null) return;
        TabDots.Children.Clear();
        foreach (var rb in VisibleTabButtons())
        {
            bool on = rb.IsChecked == true;
            TabDots.Children.Add(new Border
            {
                Width = on ? 12 : 4, Height = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(2.5, 0, 2.5, 0),
                Background = on ? Brushes.White : DotBrush
            });
        }
        PlaceTabIndicator();
        if (S.TabBar != TabBarStyle.Leise) { PlaceTimerSwitch(); return; }
        foreach (var (tag, rb) in _tabBtns) rb.Content = _tabNames[tag];
        TabHeaderBar.Measure(new Size(double.PositiveInfinity, 24));
        if (TabHeaderBar.DesiredSize.Width > ExpW - 24)
            foreach (var rb in _tabBtns.Values) rb.Content = ""; // e.g. Live + OBS visible: icons only
        PlaceTimerSwitch();
    }

    // ---------- Timer | Wecker (approved mockup 2026-11-15): the page name itself becomes a milled switch ----------

    private MilledSwitch _timerSwitch;
    private double _timerSwitchFont;
    private bool _alarmMode;
    private (int H, int M, int S) _timerWheels;

    /// <summary>
    /// On the Timer page the page name turns into the switch: in the title line (C) where the name stands, in the
    /// quiet bar (A) and the groups (B) right after the timer symbol.
    /// </summary>
    private void PlaceTimerSwitch()
    {
        if (_tabBtns == null) return;
        var style = S.TabBar;
        double fs = style == TabBarStyle.Titel ? 12.5 : 11;
        if (_timerSwitch == null || Math.Abs(_timerSwitchFont - fs) > 0.1)
        {
            (_timerSwitch?.Parent as Panel)?.Children.Remove(_timerSwitch);
            _timerSwitch = new MilledSwitch(L.T("Timer"), L.T("Wecker"), fs) { Second = _alarmMode };
            _timerSwitch.Changed += SetAlarmMode;
            _timerSwitchFont = fs;
        }
        (_timerSwitch.Parent as Panel)?.Children.Remove(_timerSwitch);
        bool on = _tab == "Timer";
        if (!on)
        {
            if (style == TabBarStyle.Titel) TabTitle.Visibility = Visibility.Visible;
            return;
        }
        if (style == TabBarStyle.Titel)
        {
            TabTitle.Visibility = Visibility.Collapsed;
            Grid.SetColumn(_timerSwitch, 0);
            _timerSwitch.Margin = new Thickness(0, 0, 10, 0);
            TabHeaderBar.Children.Add(_timerSwitch);
        }
        else
        {
            _tabBtns["Timer"].Content = ""; // the switch says it
            int i = TabBar.Children.IndexOf(_tabBtns["Timer"]);
            _timerSwitch.Margin = new Thickness(3, 0, 4, 0);
            TabBar.Children.Insert(i + 1, _timerSwitch);
        }
    }

    private void SetAlarmMode(bool alarm)
    {
        if (_alarmMode == alarm) return;
        _alarmMode = alarm;
        if (_timerSwitch != null) _timerSwitch.Second = alarm;
        if (alarm)
        {
            // the wheels keep the timer's values for later and show the alarm time (or 7:00)
            _timerWheels = (HourWheel.Value, MinuteWheel.Value, SecondWheel.Value);
            var at = H.Alarm.At ?? DateTime.Today.AddHours(7);
            HourWheel.Value = at.Hour;
            MinuteWheel.Value = at.Minute;
        }
        else
        {
            HourWheel.Value = _timerWheels.H;
            MinuteWheel.Value = _timerWheels.M;
            SecondWheel.Value = _timerWheels.S;
        }
        Pages.BeginAnimation(OpacityProperty, new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        UpdateTimerUi();
    }

    /// <summary>Activity marks: Claude working (orange), call / mic in use (green), OBS recording and unread notifications (red).</summary>
    private void UpdateTabBadges()
    {
        if (_tabBtns == null) return;
        Ext.SetBadge(_tabBtns["Agent"], H.Agents.AnyRunning || H.Agents.AnyWaiting ? AgentBrush : null);
        Ext.SetBadge(_tabBtns["Live"], H.Privacy.InUse ? LiveBadge : null);
        Ext.SetBadge(_tabBtns["Obs"], H.Obs.Recording ? RedBadge : null);
        int n = _unreadNotif;
        BadgeHop(n); // the tab hops when the count goes up
        Ext.SetBadge(_tabBtns["Notify"], n > 0 ? RedBadge : null);
        Ext.SetBadgeText(_tabBtns["Notify"], n > 0 && S.TabBar == TabBarStyle.Gruppen ? (n > 9 ? "9+" : n.ToString()) : "");
        UpdateMinimalTabs(); // agents and unread notifications change here
    }

    private void UpdateHeaderFade(bool instant = false)
    {
        bool show = !S.TabBarAutoHide || _state != State.Expanded || DateTime.Now < _headerPinUntil || IsCursorNearHeader();
        if (show == _headerVisible && !instant) return;
        _headerVisible = show;
        var d = TimeSpan.FromMilliseconds(instant ? 0 : show ? 160 : 320);
        TabHeaderBar.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1 : 0, d));
        TabDots.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 0 : 1, d));
    }

    /// <summary>On the bar, or above it (where the pointer comes from when the island opens by hovering).</summary>
    private bool IsCursorNearHeader()
    {
        if (!TabHeader.IsVisible) return false;
        try
        {
            Native.GetCursorPos(out var p);
            var tl = TabHeader.PointToScreen(new Point(0, 0));
            var dpi = VisualTreeHelper.GetDpi(this);
            double w = TabHeader.ActualWidth * PxX, h = TabHeader.ActualHeight * PxY;
            return p.X >= tl.X - 14 * PxX && p.X <= tl.X + w + 14 * PxX
                && p.Y >= tl.Y - 40 * PxY && p.Y <= tl.Y + h + 6 * PxY;
        }
        catch { return false; }
    }

    // =====================================================================
    //  Timer
    // =====================================================================

    private void UpdateTimerUi()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(UpdateTimerUi)); return; }
        bool active = H.Timer.Active && !_alarmMode; // the alarm side always shows the wheels
        TimerSetup.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        TimerRunning.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        bool a = _alarmMode;
        // the same wheels: duration (h m s) or a time of day (hh : mm Uhr)
        SecondWheel.Visibility = TimerUnitS.Visibility = a ? Visibility.Collapsed : Visibility.Visible;
        HourWheel.TwoDigits = MinuteWheel.TwoDigits = a;
        TimerUnitH.Text = a ? ":" : "h";
        TimerUnitM.Text = a ? L.T("Uhr") : "m";
        TimerUnitM.Margin = new Thickness(a ? 4 : 0, 0, 8, 0);
        TimerPresets.Visibility = a ? Visibility.Collapsed : Visibility.Visible;
        AlarmPresets.Visibility = AlarmStatus.Visibility = a ? Visibility.Visible : Visibility.Collapsed;
        if (a)
        {
            bool showsSet = H.Alarm.At is { } at && at.Hour == HourWheel.Value && at.Minute == MinuteWheel.Value;
            // set and unchanged → "Aus"; otherwise orange "Stellen"
            TimerStartBtn.Content = showsSet ? L.T("Aus") : L.T("Stellen");
            TimerStartBtn.Background = showsSet ? Tint((Brush)FindResource("Danger"), 0.24) : Tint((Brush)FindResource("Orange"), 0.24);
            TimerStartBtn.Foreground = showsSet ? (Brush)FindResource("Danger") : (Brush)FindResource("Orange");
            AlarmStatus.Text = H.Alarm.IsSet ? L.T($"{H.Alarm.At:HH:mm} · {H.Alarm.UntilText}") : L.T("Kein Wecker gestellt");
        }
        else
        {
            TimerStartBtn.Content = "Start";
            TimerStartBtn.Background = Tint((Brush)FindResource("Green"), 0.24);
            TimerStartBtn.Foreground = (Brush)FindResource("Green");
        }
        ApplyFocusMode(); // the focus side: "Fokus starten" and the week bars (2026-11-18)
    }

    private void TimerStart_Click(object sender, RoutedEventArgs e)
    {
        if (_focusMode && !_alarmMode) { StartFocus(); return; }
        if (_alarmMode)
        {
            bool showsSet = H.Alarm.At is { } at && at.Hour == HourWheel.Value && at.Minute == MinuteWheel.Value;
            if (showsSet) { H.Alarm.Clear(); ShowHint("bell", L.T("Wecker aus"), null, key: "alarm"); }
            else
            {
                H.Alarm.SetTime(HourWheel.Value, MinuteWheel.Value);
                Pop(TimerStartBtn);
            }
            UpdateTimerUi();
            return;
        }
        int seconds = HourWheel.Value * 3600 + MinuteWheel.Value * 60 + SecondWheel.Value;
        if (seconds <= 0) { MinuteWheel.Value = 1; seconds = 60; }
        H.Timer.SetDurationSeconds(seconds);
        H.Timer.Start();
        UpdateTimerUi();
    }

    private void TimerPause_Click(object sender, RoutedEventArgs e) => H.Timer.Toggle();

    private void TimerCancel_Click(object sender, RoutedEventArgs e)
    {
        if (_focusRun != null) FocusFinished(false);
        H.Timer.Reset();
        UpdateTimerUi();
    }

    private void FocusForTab()
    {
        switch (_tab)
        {
            case "Clip":
                ClipList.Focus();
                if (ClipList.Items.Count > 0)
                {
                    ClipList.SelectedIndex = 0;
                    ClipList.ScrollIntoView(ClipList.SelectedItem);
                    Dispatcher.BeginInvoke(new Action(() => (ClipList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus()), DispatcherPriority.Loaded);
                }
                break;
            case "Remind": RemindText.Focus(); break;
            case "Notes": NoteInput.Focus(); NoteInput.CaretIndex = NoteInput.Text.Length; break;
            case "Notify":
                // ↑ ↓ pick a message, Enter opens it, Entf removes it
                if (NotifList.Items.Count > 0)
                {
                    NotifList.Focus();
                    if (NotifList.SelectedIndex < 0) NotifList.SelectedIndex = 0;
                    Dispatcher.BeginInvoke(new Action(() => (NotifList.ItemContainerGenerator.ContainerFromIndex(NotifList.SelectedIndex) as ListBoxItem)?.Focus()), DispatcherPriority.Loaded);
                }
                break;
            case "Ai": AiInput.Focus(); AiInput.CaretIndex = AiInput.Text.Length; break;
            default: Focus(); break;
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e) { Collapse(); SettingsWindow.ShowSingle(); }

    private void PeriodicUi()
    {
        if (_state == State.Expanded && _tab == "Music") { UpdateMusicPosition(); if (_lyricsOpen) UpdateLyrics(); }
        FeedIslandQueue();
        if (_state == State.Expanded && _tab == "Controls" && DateTime.Now.Second % 2 == 0) { H.Audio.Refresh(); UpdateControlsUi(); }
        if (_state == State.Collapsed && (H.Timer.Active || H.Privacy.InUse || _recordingText != null || (H.Game.IsFullscreen && S.Fullscreen == FullscreenMode.StatsBar))) UpdatePill();
    }

    // =====================================================================
    //  Music
    // =====================================================================

    /// <summary>The player's name as Windows reports it (Spotify, Google Chrome, Helium …).</summary>
    private static string SourceName(string app, string title)
    {
        if (string.IsNullOrWhiteSpace(app)) return "";
        app = app.Trim();
        // some browsers report a cryptic app id (e.g. "G5WGXLDGMQ…") instead of a name – then say nothing
        return Regex.IsMatch(app, @"^[A-Z0-9_.!-]{12,}$") ? "" : app;
    }

    private void UpdateMusicUi()
    {
        var m = H.Media;
        bool has = m.HasMedia && !string.IsNullOrEmpty(m.Title);
        MusicEmpty.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        MusicContent.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        if (!has) return;
        // while skipping, the old song stays until the covers slide (the player sends title and picture separately)
        if (_skipDir != 0) { Ext.SetIcon(PlayPauseBtn, m.IsPlaying ? "pause.fill" : "play.fill"); return; }
        MusicTitle.Text = m.Title;
        // "Vortex Radar · YouTube" – where it plays, so it is clear why Spotify's extras (queue, volume) may be missing
        // in a browser: the service itself (SoundCloud, YouTube Music …) instead of "Google Chrome" (2026-11-16)
        var source = PlayerService() ?? SourceName(m.AppName, m.Title);
        MusicArtist.Text = string.Join(" · ", new[] { m.Artist, source }.Where(s => !string.IsNullOrWhiteSpace(s)));
        MusicArtist.ToolTip = string.IsNullOrEmpty(source) ? null : L.T($"{source} öffnen");
        MusicApp.Text = m.AppName ?? "";
        MusicCover.ImageSource = m.Cover;
        Ext.SetIcon(PlayPauseBtn, m.IsPlaying ? "pause.fill" : "play.fill");
        MusicEq.Opacity = m.IsPlaying ? 1 : 0.35;
        MusicSourceBtn.Visibility = m.Sessions.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        // every player gets its own volume (its mixer slider); the queue stays Spotify's (needs its login)
        bool ownVolume = PlayerProcess(m.AppName) != null;
        MusicVolBtn.Visibility = ownVolume ? Visibility.Visible : Visibility.Collapsed;
        if (!_volMode) MusicVolBtn.ToolTip = BrowserProcess.ContainsKey(m.AppName ?? "") ? L.T($"Lautstärke von {m.AppName} (alle Tabs)") : L.T($"{m.AppName}-Lautstärke");
        MusicQueueBtn.Visibility = IsSpotify ? Visibility.Visible : Visibility.Collapsed;
        if (!IsSpotify && _queueOpen) MusicQueue_Click(null, null);
        if (!ownVolume || _volApp != null && _volApp != PlayerProcess(m.AppName)) ExitVolMode();
        UpdateMusicExtras();
        UpdateMusicPosition();
    }

    private void UpdateMusicPosition()
    {
        var m = H.Media;
        if (_volMode) { UpdateVolText(); return; } // the bar shows Spotify's volume right now
        MusicProgress.IsEnabled = m.CanSeek;
        if (!m.CanSeek) { MusicPos.Text = ""; MusicDur.Text = ""; MusicProgress.Value = 0; return; }
        var pos = m.Position;
        if (!_seeking) MusicProgress.Value = pos.TotalSeconds / m.Duration.TotalSeconds;
        MusicPos.Text = Fmt(_seeking ? TimeSpan.FromSeconds(MusicProgress.Value * m.Duration.TotalSeconds) : pos);
        MusicDur.Text = "-" + Fmt(m.Duration - pos);
    }

    private static string Fmt(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private void OnTrackChanged()
    {
        Ui.Run(() =>
        {
            UpdateMusicUi();
            UpdatePill();
            if (!S.TrackPeek || !H.Media.IsPlaying || _state != State.Collapsed || H.Game.IsFullscreen || string.IsNullOrEmpty(H.Media.Title)) return;
            // first the covers swap on the closed island (the new picture usually comes a moment after the title),
            // then the notice – shown right away it would hide the small moment
            if (S.Animations == AnimationLevel.Aus) { ShowTrackPeek(); return; }
            var title = H.Media.Title;
            _trackPeekNotBefore = DateTime.Now.AddMilliseconds(500);
            void Later()
            {
                if (H.Media.Title != title) return; // skipped on meanwhile – that song brings its own notice
                var wait = (_trackPeekNotBefore - DateTime.Now).TotalMilliseconds;
                if (wait > 20) { After((int)wait, Later); return; }
                if (_state == State.Collapsed) TitleMoment(); // A4 (2026-11-18): the words come one by one in the island
            }
            After(500, Later);
        });
    }

    private void ShowTrackPeek() =>
        ShowPeek(new PeekInfo { Key = "track", Image = H.Media.Cover, Icon = "music", Title = H.Media.Title, Text = H.Media.Artist, TimeoutMs = 2400 });

    // ---------- Spotify volume: the speaker turns the progress bar into Spotify's own mixer volume ----------

    private readonly AppVolume _spotifyVol = new();
    private bool _volMode, _volSetting;
    private string _volApp; // whose mixer slider the bar shows (any player since 2026-11-16)

    private static bool IsSpotify => H.Media.AppName == "Spotify";

    private void MusicVol_Click(object sender, RoutedEventArgs e)
    {
        if (_volMode) { ExitVolMode(); return; }
        var proc = PlayerProcess(H.Media.AppName);
        if (proc == null || !_spotifyVol.Attach(proc))
        {
            MusicVolBtn.ToolTip = L.T($"{H.Media.AppName} gibt gerade keinen Ton aus");
            return;
        }
        _volApp = proc;
        _volMode = true;
        _volSetting = true;
        MusicVolume.Value = _spotifyVol.Volume;
        _volSetting = false;
        MusicProgress.Visibility = Visibility.Collapsed;
        MusicVolume.Visibility = Visibility.Visible;
        MusicPos.Visibility = Visibility.Collapsed;
        MusicVolIcon.Visibility = Visibility.Visible;
        MusicVolBtn.Foreground = (Brush)FindResource("Green");
        MusicVolBtn.ToolTip = L.T("Zurück zum Fortschritt");
        MusicVolume.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        UpdateVolText();
    }

    private void ExitVolMode()
    {
        if (!_volMode) return;
        _volMode = false;
        _volApp = null;
        _spotifyVol.Release();
        MusicVolume.Visibility = Visibility.Collapsed;
        MusicProgress.Visibility = Visibility.Visible;
        MusicPos.Visibility = Visibility.Visible;
        MusicVolIcon.Visibility = Visibility.Collapsed;
        MusicVolBtn.ClearValue(ForegroundProperty);
        MusicVolBtn.ToolTip = L.T($"{H.Media.AppName}-Lautstärke");
        MusicProgress.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        UpdateMusicPosition();
    }

    private void MusicVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_volSetting || !_volMode) return;
        _spotifyVol.Volume = e.NewValue;
        UpdateVolText();
    }

    private void UpdateVolText()
    {
        MusicDur.Text = $"{MusicVolume.Value * 100:0} %";
        MusicVolIcon.Kind = MusicVolume.Value < 0.005 ? "speaker.slash" : "speaker";
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e) => H.Media.PlayPause();
    private void Next_Click(object sender, RoutedEventArgs e) => Skip(+1);
    private void Prev_Click(object sender, RoutedEventArgs e) => Skip(-1);

    // ---------- skipping in the open island (user's pick 2026-11-15: "A · Karussell" with directions) ----------
    //  Weiter: the bar runs on to the right end, the old cover slides out to the right and the new one comes in
    //  from the left. Zurück: the bar runs back to the left (0), the covers move right → left. Title and artist follow.

    private int _skipDir;
    private string _skipTitle;
    private string _skipArtist;
    private long _skipHash;
    private DateTime _skipAt;
    private bool _skipHolding;
    private bool _skipMirror;
    private DispatcherTimer _skipTimer;
    private (ImageSource Cover, string Title, string Artist) _skipOld;

    private void Skip(int dir)
    {
        BeginSkip(dir);
        if (dir > 0) H.Media.Next(); else H.Media.Previous();
    }

    private void BeginSkip(int dir)
    {
        _skipMirror = _swipeSkip; // a swipe: the covers move the way the finger went
        _swipeSkip = false;
        if (_state == State.Expanded && _tab == "Music" && S.Animations != AnimationLevel.Aus && MusicContent.IsVisible && !_volMode)
        {
            _skipDir = dir;
            _skipTitle = H.Media.Title;
            _skipArtist = H.Media.Artist;
            _skipHash = H.Media.CoverHash;
            _skipAt = DateTime.Now;
            _skipOld = (MusicCover.ImageSource, MusicTitle.Text, MusicArtist.Text);
            _skipHolding = false;
            // the bar goes first. Weiter: on to the end. Zurück: players (Spotify) only restart the song when it ran
            // more than ~3 s – then the bar runs back to 0 and that is all; within the first seconds it really goes
            // to the previous song – then the bar first fills up and runs right → left when the covers slide
            bool restart = dir < 0 && H.Media.Position.TotalSeconds > 3;
            if (dir < 0 && !restart)
                // a real step back: the bar waits where it is; when the covers slide it jumps to full and runs back (user wish)
                MusicProgress.BeginAnimation(RangeBase.ValueProperty, new DoubleAnimation(MusicProgress.Value, MusicProgress.Value, TimeSpan.FromMilliseconds(1)));
            else
                MusicProgress.BeginAnimation(RangeBase.ValueProperty, new DoubleAnimation(MusicProgress.Value, dir > 0 ? 1 : 0, TimeSpan.FromMilliseconds(380))
                    { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
            if (_skipTimer == null)
            {
                _skipTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                _skipTimer.Tick += (_, _) => SkipTick();
            }
            _skipTimer.Start();
        }
    }

    /// <summary>Waits for the player: the new title (and, briefly, its new picture), then the covers slide.</summary>
    private void SkipTick()
    {
        var waited = DateTime.Now - _skipAt;
        // a new song: another title – or another picture / artist (players do not always send the title first, user report 2026-11-19)
        bool newTitle = H.Media.Title != _skipTitle || H.Media.Artist != _skipArtist || H.Media.Cover != null && H.Media.CoverHash != _skipHash;
        if (newTitle && !_skipHolding) { _skipHolding = true; _skipAt = DateTime.Now; waited = TimeSpan.Zero; } // from now on, wait for the picture
        if (_skipHolding)
        {
            bool newPicture = H.Media.Cover != null && H.Media.CoverHash != _skipHash;
            if (!newPicture && waited < TimeSpan.FromMilliseconds(1100)) return; // (the new picture is now asked for every 250 ms)
            _skipTimer.Stop();
            _skipHolding = false;
            int dir = _skipDir;
            _skipDir = 0;
            SlideSong(dir);
            return;
        }
        // nothing new after 3 s (e.g. "Zurück" only restarted the song): just let the bar follow the player again
        if (waited > TimeSpan.FromSeconds(3)) { _skipTimer.Stop(); _skipDir = 0; ReleaseSkipBar(); UpdateMusicUi(); } // (whatever changed meanwhile shows now)
    }

    private void SlideSong(int dir)
    {
        int way = _skipMirror ? -dir : dir; // which way the covers move
        UpdateMusicUi(); // the new song's text and picture on the real elements
        var (oldCover, oldTitle, oldArtist) = _skipOld;
        const double Way = 78, TextWay = 30;
        var outEase = new CubicEase { EasingMode = EasingMode.EaseIn };
        var inEase = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut };
        TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

        // the cover: the frame clips, so it is a carousel inside the frame
        MusicCoverBox.Clip = new RectangleGeometry(new Rect(0, 0, 78, 78), 12, 12);
        MusicCoverGhostImg.ImageSource = oldCover;
        MusicCoverGhost.Visibility = Visibility.Visible;
        MusicCoverGhostShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, way * Way, Ms(380)) { EasingFunction = outEase });
        var shrink = new DoubleAnimation(1, 0.85, Ms(380)) { EasingFunction = outEase };
        MusicCoverGhostScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        MusicCoverGhostScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
        var fade = new DoubleAnimation(1, 0, Ms(380));
        fade.Completed += (_, _) => { MusicCoverGhost.Visibility = Visibility.Collapsed; MusicCoverGhost.BeginAnimation(OpacityProperty, null); };
        MusicCoverGhost.BeginAnimation(OpacityProperty, fade);
        // (key frames that already hold the start value during the short delay – otherwise the new one flashes in place)
        DoubleAnimationUsingKeyFrames Late(double from, double to, int delay, int dur, IEasingFunction e) => new()
        {
            KeyFrames =
            {
                new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(Ms(delay))),
                new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(Ms(delay + dur)), e)
            }
        };
        MusicCoverShift.BeginAnimation(TranslateTransform.XProperty, Late(-way * Way, 0, 80, 620, inEase));
        var grow = Late(0.85, 1, 80, 620, inEase);
        MusicCoverScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        MusicCoverScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        MusicCoverFace.BeginAnimation(OpacityProperty, Late(0, 1, 80, 400, null));

        // title and artist follow the same way, a moment later
        MusicTitleGhost.Text = oldTitle;
        MusicArtistGhost.Text = oldArtist;
        MusicTextGhost.Visibility = Visibility.Visible;
        MusicTextGhostShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, way * TextWay, Ms(260)) { EasingFunction = outEase });
        var tfade = new DoubleAnimation(1, 0, Ms(260));
        tfade.Completed += (_, _) => { MusicTextGhost.Visibility = Visibility.Collapsed; MusicTextGhost.BeginAnimation(OpacityProperty, null); };
        MusicTextGhost.BeginAnimation(OpacityProperty, tfade);
        MusicTextShift.BeginAnimation(TranslateTransform.XProperty, Late(-way * TextWay, 0, 140, 520, inEase));
        MusicText.BeginAnimation(OpacityProperty, Late(0, 1, 140, 360, null));

        if (dir < 0)
        {
            // really back to the previous song: the bar jumps to full at once and runs right → left with the covers
            // (no filling animation – that came too late for the slide, user wish 2026-11-15)
            var back = new DoubleAnimationUsingKeyFrames();
            back.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            back.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(Ms(560)), new CubicEase { EasingMode = EasingMode.EaseInOut }));
            MusicProgress.BeginAnimation(RangeBase.ValueProperty, back);
            After(600, ReleaseSkipBar);
        }
        else After(420, ReleaseSkipBar);
    }

    /// <summary>The bar belongs to the player again – after "Weiter" it dips out at the end and comes back at the new start.</summary>
    private void ReleaseSkipBar()
    {
        if (!MusicProgress.HasAnimatedProperties) return;
        var dip = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(120));
        dip.Completed += (_, _) =>
        {
            MusicProgress.BeginAnimation(RangeBase.ValueProperty, null);
            UpdateMusicPosition();
            MusicProgress.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { FillBehavior = FillBehavior.Stop });
        };
        MusicProgress.BeginAnimation(OpacityProperty, dip);
    }
    // while the scrubber is held the position follows the mouse; the song jumps there on release
    private void OnSliderDragStarted(Slider s)
    {
        if (s != MusicProgress) return;
        _seeking = true;
        UpdateMusicPosition();
    }

    private void OnSliderDragEnded(Slider s)
    {
        if (s != MusicProgress) return;
        if (H.Media.CanSeek && !_sliderTest) H.Media.Seek(TimeSpan.FromSeconds(MusicProgress.Value * H.Media.Duration.TotalSeconds));
        _seeking = false;
    }

    private void MusicSource_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var (name, session) in H.Media.Sessions)
        {
            var mi = new MenuItem { Header = name };
            var s = session;
            mi.Click += (_, _) => H.Media.SelectSession(s);
            menu.Items.Add(mi);
        }
        menu.PlacementTarget = (UIElement)sender;
        menu.IsOpen = true;
    }

    // =====================================================================
    //  Clipboard
    // =====================================================================

    private sealed class ClipSorter : System.Collections.IComparer
    {
        public int Compare(object x, object y)
        {
            var a = (ClipItem)x; var b = (ClipItem)y;
            if (a.Pinned != b.Pinned) return a.Pinned ? -1 : 1;
            return b.Time.CompareTo(a.Time);
        }
    }

    private bool ClipFilterPredicate(object o)
    {
        var i = (ClipItem)o;
        bool kind = _clipFilter switch
        {
            "Text" => i.Kind == ClipKind.Text,
            "Link" => i.Kind == ClipKind.Link,
            "Image" => i.Kind == ClipKind.Image,
            "Files" => i.Kind == ClipKind.Files,
            "Pinned" => i.Pinned,
            // layout C: pictures live in their own strip above the list
            _ => !(S.ClipLayout == ClipLayout.Streifen && i.Kind == ClipKind.Image)
        };
        return kind && i.Matches(ClipSearch?.Text);
    }

    private void RefreshClipView()
    {
        foreach (var i in H.Clipboard.Items) i.RefreshMeta(); // ages and day groups move on
        _clipView.Refresh();
        foreach (var i in H.Clipboard.Items) i.Index = 0;
        int n = 1;
        foreach (ClipItem i in _clipView) { if (n > 9) break; i.Index = n++; }
        UpdateClipStrip();
        bool none = H.Clipboard.Items.Count == 0;
        ClipChipsRow.Visibility = S.ClipLayout != ClipLayout.Streifen && !none ? Visibility.Visible : Visibility.Collapsed;
        ClipEmpty.Text = none ? L.T("Noch nichts kopiert") : L.T("Nichts gefunden");
        ClipEmpty.Visibility = _clipView.IsEmpty && ClipStripRow.Visibility != Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        UpdateClipHeight();
    }

    /// <summary>
    /// The clipboard page is only as tall as its entries (user wish 2026-11-14): 300 px at most, a short list makes
    /// it shorter, an empty history leaves just the search and "Noch nichts kopiert" at the normal page height.
    /// </summary>
    private double ClipHeight()
    {
        if (_clipView == null) return 300;
        const double Chrome = 7 + 24 + 6 + 10, Search = 30;
        if (H.Clipboard.Items.Any(i => i.IsExpanded)) return 300; // an opened entry needs the room
        int n = _clipView.Count;
        double h = Chrome + Search;
        if (ClipChipsRow.Visibility == Visibility.Visible) h += 23;
        if (ClipStripRow.Visibility == Visibility.Visible) h += 61;
        if (QueuePanel.Visibility == Visibility.Visible) h += 26;
        if (n == 0) h += ClipStripRow.Visibility == Visibility.Visible ? 0 : 44;
        else if (S.ClipLayout == ClipLayout.Kacheln) h += 8 + Math.Ceiling(n / 2.0) * 82;
        else h += 2 + n * 40 + (_clipView.Groups?.Count ?? 0) * 25;
        // "Zwischenablage leeren?" needs its whole card (user report 2026-11-16: with only pictures the buttons were cut off)
        if (ClipConfirm.Visibility == Visibility.Visible)
        {
            ClipConfirmCard.Measure(new Size(ExpW, double.PositiveInfinity));
            h = Math.Max(h, ClipConfirmCard.DesiredSize.Height + 7 + 10 + 24 + 6);
        }
        return Math.Clamp(Math.Ceiling(h), ExpH, 300);
    }

    private void UpdateClipHeight()
    {
        if (_state != State.Expanded || _tab != "Clip") return;
        double h = TabHeight("Clip") + NotchExtra;
        if (Math.Abs(h - _targetH) > 0.5) AnimateSize(ExpW, h, ExpRadius, false);
    }

    // ---------- clipboard layouts (Settings › Zwischenablage): A list by day · B tiles · C picture strip + list ----------

    private static readonly (string Key, string Label)[] ClipFilters =
        { ("All", L.T("Alle")), ("Text", "Text"), ("Link", "Links"), ("Image", L.T("Bilder")), ("Files", L.T("Dateien")), ("Pinned", L.T("Angeheftet")) };

    private void ApplyClipLayout()
    {
        var l = S.ClipLayout;
        bool tiles = l == ClipLayout.Kacheln;
        ClipList.ItemTemplate = (DataTemplate)FindResource(tiles ? "ClipCardTemplate" : "ClipTemplate");
        ClipList.ItemContainerStyle = (Style)FindResource(tiles ? "ClipCardItem" : "ClipRowItem");
        ClipList.ItemsPanel = (ItemsPanelTemplate)FindResource(tiles ? "ClipTilesPanel" : "ClipRowsPanel");
        ClipList.Margin = new Thickness(tiles ? -3 : 0, tiles ? 8 : 2, tiles ? -3 : 0, 0);
        // tiles scroll by pixels: otherwise the two-column grid is squeezed into the visible height
        ScrollViewer.SetCanContentScroll(ClipList, !tiles);
        _clipView.GroupDescriptions.Clear();
        if (!tiles) _clipView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ClipItem.DayGroup)));
        // C keeps the compact filter menu, A and B show the filters as words
        FilterBtn.Visibility = l == ClipLayout.Streifen ? Visibility.Visible : Visibility.Collapsed;
        if (l != ClipLayout.Streifen && _clipFilter != "All" && FilterBtn.Foreground != Brushes.White) FilterBtn.Foreground = Brushes.White;
        BuildClipChips();
        RefreshClipView();
    }

    private void BuildClipChips()
    {
        ClipChips.Children.Clear();
        foreach (var (key, label) in ClipFilters)
        {
            bool on = _clipFilter == key;
            var t = new TextBlock
            {
                Text = label, FontSize = 11.5, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center,
                Foreground = on ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)),
                FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal
            };
            var chip = new Border
            {
                Child = t, Margin = new Thickness(key == "All" ? 2 : 0, 0, 13, -0.5), Background = Brushes.Transparent, Cursor = Cursors.Hand,
                BorderThickness = new Thickness(0, 0, 0, 1.5), BorderBrush = on ? Brushes.White : Brushes.Transparent
            };
            var k = key;
            chip.MouseLeftButtonUp += (_, e) => { e.Handled = true; _clipFilter = k; BuildClipChips(); RefreshClipView(); };
            ClipChips.Children.Add(chip);
        }
    }

    /// <summary>Layout C: the newest pictures (search applies) – four plus "+N", or five when that is all.</summary>
    private void UpdateClipStrip()
    {
        if (ClipStripRow == null) return;
        bool on = S.ClipLayout == ClipLayout.Streifen && _clipFilter == "All";
        var pics = on
            ? H.Clipboard.Items.Where(i => i.Kind == ClipKind.Image && i.Matches(ClipSearch?.Text))
                .OrderByDescending(i => i.Pinned).ThenByDescending(i => i.Time).ToList()
            : new List<ClipItem>();
        int show = pics.Count > 5 ? 4 : pics.Count;
        ClipStrip.ItemsSource = pics.Take(show).ToList();
        ClipStripMore.Visibility = pics.Count > 5 ? Visibility.Visible : Visibility.Collapsed;
        ClipStripMoreText.Text = L.T($"+ {pics.Count - 4}");
        ClipStripRow.Visibility = pics.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        // a hairline under the search only when nothing else separates it from the list
        ClipHeader.BorderThickness = new Thickness(0, 0, 0, S.ClipLayout == ClipLayout.Streifen && pics.Count == 0 ? 0.5 : 0);
    }

    private void ClipStrip_Click(object sender, MouseButtonEventArgs e)
    {
        if (ItemOf(sender) is not ClipItem item) return;
        e.Handled = true;
        _ = UseClip(item, S.ClickPastes);
    }

    private void ClipStrip_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemOf(sender) is not ClipItem item) return;
        e.Handled = true;
        OpenQuickLook(item);
    }

    /// <summary>"+N": the list shows all pictures (with the filter menu back to "Alle" the strip returns).</summary>
    private void ClipStripMore_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _clipFilter = "Image";
        FilterBtn.Foreground = (Brush)FindResource("Accent");
        RefreshClipView();
    }

    private void FilterBtn_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
        foreach (var (key, label) in new[] { ("All", L.T("Alle")), ("Text", "Text"), ("Link", "Links"), ("Image", L.T("Bilder")), ("Files", L.T("Dateien")), ("Pinned", L.T("Angeheftet")) })
        {
            var mi = new MenuItem { Header = label, IsChecked = _clipFilter == key };
            var k = key;
            mi.Click += (_, _) =>
            {
                _clipFilter = k;
                Ext.SetHover(FilterBtn, (Brush)FindResource("Blue"));
                FilterBtn.Foreground = k == "All" ? Brushes.White : (Brush)FindResource("Accent");
                RefreshClipView();
            };
            menu.Items.Add(mi);
        }
        menu.Items.Add(new Separator());
        var clear = new MenuItem { Header = L.T("Verlauf leeren") };
        clear.Click += (_, _) => { H.Clipboard.ClearUnpinned(); RefreshClipView(); };
        menu.Items.Add(clear);
        menu.IsOpen = true;
    }

    private void ClipSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_clipView != null) RefreshClipView();
    }

    private void ClipSearch_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && ClipList.Items.Count > 0)
        {
            ClipList.SelectedIndex = 0;
            (ClipList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && ClipList.Items.Count > 0)
        {
            _ = UseClip((ClipItem)(ClipList.SelectedItem ?? ClipList.Items[0]), S.ClickPastes);
            e.Handled = true;
        }
    }

    private static ClipItem ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as ClipItem;

    private void ClipItem_Click(object sender, MouseButtonEventArgs e)
    {
        // clicks inside an expanded entry (selecting text, its buttons) must not paste
        for (var d = e.OriginalSource as DependencyObject; d != null && d != sender; d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d))
            if (d is FrameworkElement fe && "noclick".Equals(fe.Tag)) return;
        if (ItemOf(sender) is ClipItem item) _ = UseClip(item, S.ClickPastes);
    }

    /// <summary>⌄ opens one entry (full text or a big picture), like an accordion – others close.</summary>
    private void ClipExpand_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not ClipItem item) return;
        bool open = !item.IsExpanded;
        foreach (var i in H.Clipboard.Items) if (i != item) i.IsExpanded = false;
        item.IsExpanded = open;
        UpdateClipHeight(); // an opened entry gets the full height, closing gives it back
        if (open) Dispatcher.BeginInvoke(new Action(() => ClipList.ScrollIntoView(item)), DispatcherPriority.Background);
    }

    private void ClipPaste_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is ClipItem i) _ = UseClip(i, true);
    }

    private async Task UseClip(ClipItem item, bool paste)
    {
        if (!H.Clipboard.SetClipboard(item))
        {
            ShowHint("xmark", "Nicht mehr da", (Brush)FindResource("Danger"));
            return;
        }
        if (paste)
        {
            var target = H.Foreground.LastExternal;
            Collapse();
            if (target != IntPtr.Zero) await PasteHelper.PasteInto(target);
            return;
        }
        // copy only: the island stays open, the row confirms with "Kopiert ✓"
        foreach (var i in H.Clipboard.Items) if (i != item) i.JustCopied = false;
        item.JustCopied = true;
        await Task.Delay(1400);
        item.JustCopied = false;
    }

    private void ClipCopy_Click(object sender, RoutedEventArgs e) { if (ItemOf(sender) is ClipItem i) _ = UseClip(i, false); }
    private void ClipPin_Click(object sender, RoutedEventArgs e) { if (ItemOf(sender) is ClipItem i) { H.Clipboard.TogglePin(i); RefreshClipView(); } }
    private void ClipDelete_Click(object sender, RoutedEventArgs e) { if (ItemOf(sender) is ClipItem i) DeleteClip(i, sender); }
    private void ClipSave_Click(object sender, RoutedEventArgs e) { if (ItemOf(sender) is ClipItem i) SaveImageItem(i); }

    /// <summary>Click on the big picture of an opened entry → Quick-Look view almost up to the monitor edges.</summary>
    private void ClipImage_Click(object sender, MouseButtonEventArgs e)
    {
        if (ItemOf(sender) is not ClipItem item) return;
        e.Handled = true;
        OpenQuickLook(item);
    }

    /// <summary>A picture from the clipboard to the KI chat (2026-11-17).</summary>
    private void ClipAskAi_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not ClipItem item || item.ImageFile == null || !File.Exists(item.ImageFile)) return;
        e.Handled = true;
        try
        {
            var bi = new System.Windows.Media.Imaging.BitmapImage();
            bi.BeginInit();
            bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bi.UriSource = new Uri(item.ImageFile);
            bi.EndInit();
            bi.Freeze();
            AskAiAbout(bi);
        }
        catch { }
    }

    private void ClipQuickLook_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not ClipItem item) return;
        e.Handled = true;
        OpenQuickLook(item);
    }

    private void OpenQuickLook(ClipItem item)
    {
        Collapse();
        QuickLookWindow.Show(item,
            copy: i =>
            {
                if (H.Clipboard.SetClipboard(i)) ShowHint("copy", L.T("Kopiert"), null, check: true, key: "copy");
            },
            liveText: i => EditorWindow.Open(i, liveText: true),
            save: SaveImageItem,
            // edited picture = new entry on top (the original stays) and it's on the clipboard right away
            edited: img =>
            {
                H.Clipboard.AddImage(img, item.IsScreenshot);
                ShowHint("pencil", L.T("Bearbeitet · kopiert"), null, check: true, key: "copy");
            });
    }

    private void ClipOpen_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not ClipItem i) return;
        Collapse();
        EditorWindow.Open(i, liveText: true);
    }

    // ---------- "delete everything" with an Apple-style confirmation inside the island ----------

    private void ClipClearAll_Click(object sender, RoutedEventArgs e)
    {
        int n = H.Clipboard.Items.Count;
        if (n == 0) return;
        ShowClipConfirm(n, H.Clipboard.Items.Count(i => i.Pinned));
    }

    private void ShowClipConfirm(int n, int pinned)
    {
        ClipConfirmText.Text = (n == 1 ? L.T("Der Eintrag wird gelöscht") : L.T($"Alle {n} Einträge werden gelöscht"))
                               + (pinned > 0 ? $" – auch {(pinned == 1 ? "der angeheftete" : $"{pinned} angeheftete")}." : ".");
        _sticky = true; // moving the pointer away must not close the island while asking
        ClipConfirm.Visibility = Visibility.Visible;
        UpdateClipHeight(); // the island opens far enough for the whole question
        ClipConfirm.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        var spring = new DoubleAnimation(0.86, 1, TimeSpan.FromMilliseconds(380)) { EasingFunction = new ElasticEase { Oscillations = 1, Springiness = 6 } };
        ClipConfirmScale.BeginAnimation(ScaleTransform.ScaleXProperty, spring);
        ClipConfirmScale.BeginAnimation(ScaleTransform.ScaleYProperty, spring);
    }

    private void HideClipConfirm()
    {
        if (ClipConfirm.Visibility != Visibility.Visible) return;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120));
        fade.Completed += (_, _) => { if (ClipConfirm.Opacity < 0.05) { ClipConfirm.Visibility = Visibility.Collapsed; UpdateClipHeight(); } };
        ClipConfirm.BeginAnimation(OpacityProperty, fade);
        var shrink = new DoubleAnimation(0.92, TimeSpan.FromMilliseconds(120));
        ClipConfirmScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        ClipConfirmScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
    }

    private void ClipConfirmCancel_Click(object sender, RoutedEventArgs e) => HideClipConfirm();

    private void ClipConfirmYes_Click(object sender, RoutedEventArgs e)
    {
        H.Clipboard.ClearAll();
        ClipSearch.Text = "";
        RefreshClipView();
        UpdateQueueUi();
        UpdatePill();
        HideClipConfirm();
    }

    private void ClearClips_Click(object sender, RoutedEventArgs e)
    {
        H.Clipboard.ClearUnpinned();
        RefreshClipView();
    }

    private void QueueToggle_Click(object sender, RoutedEventArgs e) => SetQueueMode(QueueToggle.IsChecked == true);

    private void QueueRemove_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is ClipItem i) H.Clipboard.RemoveFromQueue(i);
    }

    private void ClearQueue_Click(object sender, RoutedEventArgs e) => H.Clipboard.ClearQueue();

    private void UpdateQueueUi()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(UpdateQueueUi)); return; }
        bool on = H.Clipboard.QueueMode;
        QueueToggle.IsChecked = on;
        Ext.SetIcon(QueueToggle, on ? "stack.fill" : "stack"); // the switch itself shows it – no status in the closed island
        QueuePanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        var next = H.Clipboard.Queue.FirstOrDefault();
        QueueHeader.Text = H.Clipboard.QueueCount == 0
            ? L.T("Stapel an – kopiere nacheinander, Strg+V fügt der Reihe nach ein")
            : L.T($"Stapel · {H.Clipboard.QueueCount} · als Nächstes: {next?.Preview}");
        UpdatePill();
    }

    // =====================================================================
    //  Screenshot entry points
    // =====================================================================

    public void TakeScreenshot(bool ocr) => ScreenshotController.Start(ocr);

    private void ScreenshotBtn_Click(object sender, RoutedEventArgs e) => TakeScreenshot(false);

    public void SaveImageItem(ClipItem item)
    {
        try
        {
            if (item.ImageFile == null || !File.Exists(item.ImageFile)) return;
            ScreenshotService.SaveCopy(item.ImageFile, S.ScreenshotFolder);
            ShowHint("save", "Gesichert", (Brush)FindResource("Green"), check: true);
        }
        catch (Exception ex)
        {
            App.Log(ex);
            ShowHint("xmark", "Fehler", (Brush)FindResource("Danger"));
        }
    }

    // =====================================================================
    //  Shelf & phone
    // =====================================================================

    /// <summary>
    /// The Ablage icon only exists while files are in it (user wish 2026-11-14) – and while you are on the page,
    /// e.g. because you are just dragging files onto the island.
    /// </summary>
    private void UpdateShelfTab()
    {
        if (ShelfTabBtn == null) return;
        bool show = !IsTabHidden("Shelf") && (H.Shelf.Items.Count > 0 || _state == State.Expanded && _tab == "Shelf");
        var vis = show ? Visibility.Visible : Visibility.Collapsed;
        if (ShelfTabBtn.Visibility != vis) { ShelfTabBtn.Visibility = vis; UpdateTabDots(); }
        UpdateMinimalTabs();
    }

    // ---------- Minimal-Modus (user wish 2026-11-17): chosen tabs stay away while there is nothing on them ----------

    /// <summary>The tabs the Minimal-Modus can hide, in the order of the settings page.</summary>
    public static readonly string[] MinimalCandidates = { "Clip", "Ai", "Agent", "Send", "Music", "Notify" };

    private bool HasContent(string tab) => tab switch
    {
        "Clip" => H.Clipboard.Items.Count > 0,
        "Ai" => AiService.HasKey(S.AiProvider),
        "Agent" => H.Agents.Any,
        "Send" => H.LocalSend.Devices.Count > 0,
        "Music" => H.Media.HasMedia,
        "Notify" => _unreadNotif > 0,
        _ => true,
    };

    /// <summary>Empty and chosen in Settings › Minimal-Modus.</summary>
    private bool MinimalAway(string tab) => S.MinimalMode && S.MinimalTabs.Contains(tab) && !HasContent(tab);

    private void UpdateMinimalTabs()
    {
        if (_tabBtns == null) return;
        bool changed = false;
        foreach (var t in MinimalCandidates)
        {
            if (!_tabBtns.TryGetValue(t, out var b)) continue;
            // the page you are looking at stays until you leave it
            bool show = !IsTabHidden(t) && (!MinimalAway(t) || _state == State.Expanded && _tab == t);
            var vis = show ? Visibility.Visible : Visibility.Collapsed;
            if (b.Visibility != vis) { b.Visibility = vis; changed = true; }
        }
        if (changed) UpdateTabDots();
    }

    private void UpdateShelfUi()
    {
        bool empty = H.Shelf.Items.Count == 0;
        ShelfEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ShelfHeader.Visibility = ShelfScroll.Visibility = ShelfAddTile.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        if (empty) return;
        long bytes = 0;
        foreach (var i in H.Shelf.Items) try { if (File.Exists(i.Path)) bytes += new FileInfo(i.Path).Length; } catch { }
        int n = H.Shelf.Items.Count;
        ShelfSummary.Text = $"{n} {(n == 1 ? "Datei" : "Dateien")}" + (bytes > 0 ? $" · {FormatBytes(bytes)}" : "");
        // the "+" tile sits right after the last file (up to the visible width)
        ShelfAddTile.Margin = new Thickness(Math.Min(n * 68, ExpW - 24 - 62), 0, 0, 0);
    }

    private static string FormatBytes(long b) =>
        b >= 1L << 30 ? $"{b / (double)(1L << 30):0.0} GB" : b >= 1 << 20 ? $"{b / (double)(1 << 20):0.0} MB" : $"{Math.Max(1, b / 1024)} KB";

    private void ShelfAddTile_Click(object sender, MouseButtonEventArgs e) { e.Handled = true; ShelfAdd_Click(sender, null); }
    private void ShelfAddLink_Click(object sender, RoutedEventArgs e) => ShelfAdd_Click(sender, null);

    private void Island_DragEnter(object sender, DragEventArgs e)
    {
        // files – or a link from the browser (only the Spiele tab takes links)
        bool link = !e.Data.GetDataPresent(DataFormats.FileDrop) && LinkFrom(e.Data) != null;
        if (_internalDrag || !e.Data.GetDataPresent(DataFormats.FileDrop) && !link) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        e.Effects = link ? DragDropEffects.Link : DragDropEffects.Copy;
        e.Handled = true;
        if (_state == State.Collapsed) MagnetBulge(e.GetPosition(Island).X); // it reaches for the file first
        if (link)
        {
            if (_state != State.Expanded || _tab != "Games") Expand("Games", false);
            GamesAddRect.Stroke = (Brush)FindResource("Accent");
            return;
        }
        // on the open "Spiele" tab a drop adds games/programs, everywhere else files go to the shelf
        // (the island is usually closed while you drag from the desktop – it then reopens on the page you were on)
        if (_state != State.Expanded && S.LastTab == "Games") Expand("Games", false);
        if (_state == State.Expanded && _tab == "Games") { GamesAddRect.Stroke = (Brush)FindResource("Accent"); return; }
        if (_state != State.Expanded || _tab != "Shelf") Expand("Shelf", false);
    }

    private void Island_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = _internalDrag ? DragDropEffects.None
            : e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy
            : _tab == "Games" && LinkFrom(e.Data) != null ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
        _cursorOutsideSince = DateTime.MaxValue;
    }

    private void Island_Drop(object sender, DragEventArgs e)
    {
        if (_internalDrag) return;
        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (files == null && _tab == "Games" && _state == State.Expanded && LinkFrom(e.Data) is { } url)
        {
            e.Handled = true;
            GamesAddRect.Stroke = new SolidColorBrush(Color.FromArgb(0x40, 0xEB, 0xEB, 0xF5));
            _ = AddLink(url);
            return;
        }
        if (files != null && _tab == "Games" && _state == State.Expanded)
        {
            AddGames(files);
        }
        else if (files != null && OfferConvert(files)) { } // one picture: JPG · PNG · smaller · PDF – or the Ablage (40a)
        else if (files != null)
        {
            H.Shelf.Add(files);
            if (_tab != "Shelf") SelectTab("Shelf", true);
        }
        IslandGulp(); // and swallows it
        e.Handled = true;
    }

    private void ShelfItem_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _dragItem = (sender as FrameworkElement)?.DataContext as ShelfItem;
        if (e.ClickCount == 2 && _dragItem != null)
        {
            try { Process.Start(new ProcessStartInfo(_dragItem.Path) { UseShellExecute = true }); } catch { }
            _dragItem = null;
        }
    }

    private void ShelfItem_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragItem == null) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _dragStart.X) < 6 && Math.Abs(p.Y - _dragStart.Y) < 6) return;
        var item = _dragItem;
        _dragItem = null;
        var data = new DataObject(DataFormats.FileDrop, new[] { item.Path });
        _internalDrag = true;
        try { DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy | DragDropEffects.Move); }
        finally { _internalDrag = false; }
        if (!File.Exists(item.Path) && !Directory.Exists(item.Path)) H.Shelf.Remove(item);
    }

    private void ShelfRemove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ShelfItem i) H.Shelf.Remove(i);
    }

    private void ShelfAdd_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Multiselect = true, Title = L.T("Dateien zur Ablage hinzufügen") };
        if (dlg.ShowDialog() == true) H.Shelf.Add(dlg.FileNames);
    }

    private void ShelfCopyTo_Click(object sender, RoutedEventArgs e)
    {
        if (H.Shelf.Items.Count == 0) return;
        using var dlg = new WF.FolderBrowserDialog { Description = L.T("Zielordner"), UseDescriptionForTitle = true };
        if (dlg.ShowDialog() != WF.DialogResult.OK) return;
        foreach (var i in H.Shelf.Items.ToList())
        {
            try { if (File.Exists(i.Path)) File.Copy(i.Path, Path.Combine(dlg.SelectedPath, Path.GetFileName(i.Path)), false); }
            catch (Exception ex) { App.Log(ex); }
        }
        ShowHint("folder", "Kopiert", (Brush)FindResource("Yellow"), check: true);
    }

    private void ShelfToClipboard_Click(object sender, RoutedEventArgs e)
    {
        if (H.Shelf.Items.Count == 0) return;
        var sc = new System.Collections.Specialized.StringCollection();
        foreach (var i in H.Shelf.Items) sc.Add(i.Path);
        try { Clipboard.SetFileDropList(sc); } catch { }
        Collapse();
        ShowHint("copy", L.T("Kopiert"), null, check: true, key: "copy");
    }

    private void ShelfClear_Click(object sender, RoutedEventArgs e) => H.Shelf.Clear();

    // =====================================================================
    //  Spiele: games and programs – picture + name, one click starts
    // =====================================================================

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);

    /// <summary>Test only: really moves the mouse and clicks "+" → "Steam-Spiel" → first game, then logs what happened.</summary>
    public async void GamesClickTest(string file)
    {
        var log = new System.Text.StringBuilder();
        H.Launcher.Persist = false;
        int before = H.Launcher.Items.Count;
        ContextMenu opened = null;
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((s, _) => opened = (ContextMenu)s));
        Point Center(FrameworkElement fe) => fe.PointToScreen(new Point(fe.ActualWidth / 2, fe.ActualHeight / 2));
        async Task MoveTo(Point p) { SetCursorPos((int)p.X, (int)p.Y); await Task.Delay(250); }
        async Task ClickAt(Point p) { await MoveTo(p); mouse_event(0x2, 0, 0, 0, IntPtr.Zero); await Task.Delay(60); mouse_event(0x4, 0, 0, 0, IntPtr.Zero); }
        try
        {
            Expand("Games", true);
            await Task.Delay(800);
            await MoveTo(Center(GamesAddTile));
            log.AppendLine($"expanded: state={_state} tab={_tab} addTileVisible={GamesAddTile.IsVisible}");
            await ClickAt(Center(GamesAddTile));
            await Task.Delay(700);
            log.AppendLine($"after + click: menu={(opened != null)} open={opened?.IsOpen} state={_state} captured={Mouse.Captured?.GetType().Name}");
            if (opened?.IsOpen == true)
            {
                var steam = opened.Items.OfType<MenuItem>().FirstOrDefault(m => (m.Header as string) == "Steam-Spiel");
                await MoveTo(Center(steam));
                await Task.Delay(900);
                log.AppendLine($"steam submenu open={steam?.IsSubmenuOpen} items={steam?.Items.Count} state={_state}");
                var first = steam?.Items.OfType<MenuItem>().FirstOrDefault(m => m.IsEnabled && m.IsVisible);
                if (first != null)
                {
                    log.AppendLine($"clicking '{first.Header}'");
                    await ClickAt(Center(first));
                    await Task.Delay(1200);
                }
            }
            log.AppendLine($"result: items {before} -> {H.Launcher.Items.Count} state={_state} tab={_tab}");
            // a normal Start-menu shortcut (.lnk) through the same path as the file dialog / drag & drop
            var lnk = Directory.EnumerateFiles(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "*.lnk",
                new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).FirstOrDefault();
            if (lnk != null)
            {
                int n = H.Launcher.AddFiles(new[] { lnk });
                var it = H.Launcher.Items.LastOrDefault();
                log.AppendLine($"lnk '{Path.GetFileName(lnk)}': added={n} name='{it?.Name}' icon={(it?.Icon != null)} exists={(it != null && H.Launcher.Exists(it))}");
            }
        }
        catch (Exception ex) { log.AppendLine("EXCEPTION " + ex); }
        foreach (var g in H.Launcher.Items.Skip(before).ToList()) H.Launcher.Remove(g);
        H.Launcher.Persist = true;

        // price alarm: a short stock name, then the mouse goes along the row to the trash can and clicks it
        PriceAlert alarm = null;
        try
        {
            H.Prices.Persist = false;
            var (a, err) = await H.Prices.AddAsync("AAPL", "");
            alarm = a;
            log.AppendLine($"alarm added: {(a != null)} {err}");
            if (a != null)
            {
                Expand("Notify", true);
                SegAlarm.IsChecked = true;
                await Task.Delay(900);
                if (AlarmList.ItemContainerGenerator.ContainerFromItem(a) is ListBoxItem lbi)
                {
                    var cp = FindChild<ContentPresenter>(lbi);
                    var row = (FrameworkElement)cp.ContentTemplate.FindName("row", cp);
                    var del = (Button)cp.ContentTemplate.FindName("del", cp);
                    // the empty gap between the short name and the trash can
                    await MoveTo(row.PointToScreen(new Point(row.ActualWidth - 70, row.ActualHeight / 2)));
                    await MoveTo(row.PointToScreen(new Point(row.ActualWidth - 40, row.ActualHeight / 2)));
                    log.AppendLine($"in the gap: trash visible={del.Visibility}");
                    await ClickAt(Center(del));
                    await Task.Delay(500);
                    log.AppendLine($"after trash click: still in list={H.Prices.Items.Contains(a)}");
                }
                else log.AppendLine("row not found");
            }
        }
        catch (Exception ex) { log.AppendLine("EXCEPTION " + ex); }
        if (alarm != null && H.Prices.Items.Contains(alarm)) H.Prices.Remove(alarm);
        H.Prices.Persist = true;
        File.WriteAllText(file, log.ToString());
        App.Quit();
    }

    private void UpdateGamesUi()
    {
        GamesEmpty.Visibility = H.Launcher.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        GamesAddRect.Stroke = new SolidColorBrush(Color.FromArgb(0x40, 0xEB, 0xEB, 0xF5));
    }

    private async Task AddLink(Uri url, string name = null)
    {
        var item = await H.Launcher.AddLinkAsync(url, name);
        UpdateGamesUi();
        if (item == null) { ShowHint("link", L.T("Schon da"), null); return; }
        Dispatcher.BeginInvoke(new Action(() => GamesScroll.ScrollToRightEnd()), DispatcherPriority.Background);
    }

    /// <summary>A link dragged from the browser (address bar, a link on a page) – null if the drag holds none.</summary>
    private static Uri LinkFrom(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent("UniformResourceLocatorW") && data.GetData("UniformResourceLocatorW") is MemoryStream ms)
                return LauncherService.ParseLink(System.Text.Encoding.Unicode.GetString(ms.ToArray()).TrimEnd('\0'));
            if (data.GetDataPresent(DataFormats.UnicodeText))
                return LauncherService.ParseLink(data.GetData(DataFormats.UnicodeText) as string);
        }
        catch { }
        return null;
    }

    private void AddGames(string[] files)
    {
        int n = H.Launcher.AddFiles(files);
        UpdateGamesUi();
        if (n > 0) Dispatcher.BeginInvoke(new Action(() => GamesScroll.ScrollToRightEnd()), DispatcherPriority.Background);
        else ShowHint("gamecontroller", L.T("Schon da"), null);
    }

    private void GamesScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // the wheel scrolls the row sideways (only when there is something to scroll)
        if (GamesScroll.ScrollableWidth <= 0) return;
        e.Handled = true;
        GamesScroll.ScrollToHorizontalOffset(GamesScroll.HorizontalOffset - e.Delta * 0.5);
    }

    private static LaunchItem GameOf(object sender) => (sender as FrameworkElement)?.DataContext as LaunchItem;

    private void Game_Click(object sender, MouseButtonEventArgs e)
    {
        if (GameOf(sender) is not LaunchItem g || g.Editing) return;
        if (e.OriginalSource is DependencyObject d && FindParent<TextBox>(d) != null) return;
        e.Handled = true;
        if (_gamesReorder?.JustDragged == true) return; // that was a move, not a start
        LaunchGame(g);
    }

    private void LaunchGame(LaunchItem g)
    {
        if (!H.Launcher.Exists(g))
        {
            ShowHint("xmark", "Nicht mehr da", (Brush)FindResource("Danger"));
            return;
        }
        if (H.Launcher.Launch(g))
        {
            Collapse();
            ShowHint("gamecontroller", L.T($"{g.Name} startet"), null, check: true);
        }
        else ShowHint("xmark", "Start fehlgeschlagen", (Brush)FindResource("Danger"));
    }

    private static T FindChild<T>(DependencyObject d) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is T t) return t;
            if (FindChild<T>(c) is { } deeper) return deeper;
        }
        return null;
    }

    private static T FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        for (; d != null; d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d))
            if (d is T t) return t;
        return null;
    }

    private void Game_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (GameOf(sender) is not LaunchItem g) return;
        e.Handled = true;
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom, MaxHeight = 460 };
        MenuItem Item(string header, Action act)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => act();
            menu.Items.Add(mi);
            return mi;
        }
        if (g.IsSteamLibrary)
        {
            // the Steam tile: the installed games right here, the last played first – a click starts it
            var games = H.Steam.RecentGames();
            if (games.Count == 0) menu.Items.Add(new MenuItem { Header = L.T("Keine Steam-Spiele gefunden"), IsEnabled = false });
            foreach (var (id, name, _) in games)
            {
                var mi = Item(name, () =>
                {
                    SteamService.Play(id);
                    Collapse();
                    ShowHint("gamecontroller", L.T($"{name} startet"), null, check: true);
                });
                if (H.Steam.SmallIcon(id) is { } file)
                {
                    try
                    {
                        var bi = new System.Windows.Media.Imaging.BitmapImage();
                        bi.BeginInit(); bi.UriSource = new Uri(file); bi.DecodePixelWidth = 32; bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bi.EndInit(); bi.Freeze();
                        mi.Icon = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(4), Background = new ImageBrush(bi) { Stretch = Stretch.UniformToFill } };
                    }
                    catch { }
                }
            }
            menu.Items.Add(new Separator());
            Item(L.T("Steam-Bibliothek öffnen"), () => LaunchGame(g));
        }
        else Item(L.T("Starten"), () => LaunchGame(g));
        Item(L.T("Umbenennen"), () => { foreach (var i in H.Launcher.Items) i.Editing = false; g.Editing = true; });
        if (g.SteamAppId == null && (File.Exists(g.Target) || Directory.Exists(g.Target)))
            Item(L.T("Im Explorer zeigen"), () =>
            {
                try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{g.Target}\"") { UseShellExecute = true }); } catch { }
            });
        int idx = H.Launcher.Items.IndexOf(g);
        if (idx > 0) Item(L.T("Nach links"), () => H.Launcher.Move(g, idx - 1));
        if (idx < H.Launcher.Items.Count - 1) Item(L.T("Nach rechts"), () => H.Launcher.Move(g, idx + 1));
        menu.Items.Add(new Separator());
        Item(L.T("Entfernen"), () => { H.Launcher.Remove(g); UpdateGamesUi(); });
        menu.IsOpen = true;
    }

    private void GameRename_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox t || !t.IsVisible) return;
        Activate();
        Dispatcher.BeginInvoke(new Action(() => { t.Focus(); Keyboard.Focus(t); t.SelectAll(); }), DispatcherPriority.Input);
    }

    private void GameRename_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox t || GameOf(t) is not LaunchItem g) return;
        if (e.Key == Key.Enter) { e.Handled = true; H.Launcher.Rename(g, t.Text); g.Editing = false; }
        else if (e.Key == Key.Escape) { e.Handled = true; t.Text = g.Name; g.Editing = false; }
    }

    private void GameRename_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox t && GameOf(t) is LaunchItem { Editing: true } g) { H.Launcher.Rename(g, t.Text); g.Editing = false; }
    }

    private void GameAdd_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
        var file = new MenuItem { Header = L.T("Programm oder Verknüpfung …") };
        file.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog
            {
                Multiselect = true,
                Title = L.T("Spiel oder Programm hinzufügen"),
                Filter = "Programme und Verknüpfungen|*.exe;*.lnk;*.url;*.bat;*.cmd|Alle Dateien|*.*",
                DereferenceLinks = false
            };
            if (dlg.ShowDialog() == true) AddGames(dlg.FileNames);
        };
        menu.Items.Add(file);

        // a web address as a tile (user wish 2026-11-15) – a link on the clipboard is filled in already
        var link = new MenuItem { Header = L.T("Link …") };
        link.Click += async (_, _) =>
        {
            string pre = null;
            try { if (Clipboard.ContainsText() && LauncherService.ParseLink(Clipboard.GetText()) is { } u) pre = u.AbsoluteUri; } catch { }
            _sticky = true;
            var dlg = new LinkPrompt(pre);
            bool ok = dlg.ShowDialog() == true;
            if (ok) await AddLink(dlg.Url, dlg.LinkName);
        };
        menu.Items.Add(link);

        if (!H.Launcher.Items.Any(i => i.IsSteamLibrary) && LauncherService.SteamInstalled)
        {
            var lib = new MenuItem { Header = L.T("Steam (alle Spiele per Rechtsklick)") };
            lib.Click += (_, _) => { H.Launcher.AddSteamLibrary(); UpdateGamesUi(); };
            menu.Items.Add(lib);
        }

        var steam = new MenuItem { Header = L.T("Steam-Spiel") };
        var games = H.Steam.InstalledGames();
        if (games.Count == 0) steam.Items.Add(new MenuItem { Header = L.T("Keine Steam-Spiele gefunden"), IsEnabled = false });
        foreach (var (id, name) in games)
        {
            bool had = H.Launcher.Items.Any(i => i.SteamAppId == id);
            var mi = new MenuItem { Header = name, IsCheckable = false, IsChecked = had, IsEnabled = !had };
            mi.Click += (_, _) =>
            {
                H.Launcher.AddSteam(id, name);
                UpdateGamesUi();
                Dispatcher.BeginInvoke(new Action(() => GamesScroll.ScrollToRightEnd()), DispatcherPriority.Background);
            };
            steam.Items.Add(mi);
        }
        menu.Items.Add(steam);
        menu.IsOpen = true;
    }


    // =====================================================================
    //  Reminders & timer
    // =====================================================================

    private void RemindText_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { AddReminder(null); e.Handled = true; }
    }

    private void RemindAdd_Click(object sender, RoutedEventArgs e) => AddReminder(null);

    private void RemindQuickMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
        foreach (var (label, tag) in new[] { (L.T("In 5 Minuten"), "5"), (L.T("In 15 Minuten"), "15"), (L.T("In 1 Stunde"), "60"), (L.T("Heute 18:00"), "T18"), (L.T("Morgen 9:00"), "M9") })
        {
            var mi = new MenuItem { Header = label, Tag = tag };
            mi.Click += RemindQuick_Click;
            menu.Items.Add(mi);
        }
        menu.IsOpen = true;
    }

    private void RemindQuick_Click(object sender, RoutedEventArgs e)
    {
        var tag = (string)((FrameworkElement)sender).Tag;
        var now = DateTime.Now;
        DateTime due = tag switch
        {
            "T18" => now.Date.AddHours(18) > now ? now.Date.AddHours(18) : now.Date.AddDays(1).AddHours(18),
            "M9" => now.Date.AddDays(1).AddHours(9),
            _ => now.AddMinutes(int.Parse(tag))
        };
        AddReminder(due);
    }

    /// <summary>
    /// Reads one sentence: what, when ("in 20 min", "um 14:30", "morgen 9 Uhr") and how often ("täglich",
    /// "werktags", "jeden Dienstag", "monatlich"). Without a time it rings in 10 minutes.
    /// </summary>
    private static (string Text, DateTime Due, RepeatKind Repeat, bool HasTime) ParseReminder(string raw)
    {
        var rest = raw.Trim();
        var repeat = ReminderService.TakeRepeat(ref rest, out var weekday);
        string text = rest.Length == 0 ? L.T("Erinnerung") : rest;
        bool hasTime = ReminderService.TryParseQuick(rest, out var parsedText, out var due);
        if (hasTime) text = parsedText;
        else due = weekday != null ? DateTime.Today.AddHours(9) : DateTime.Now.AddMinutes(10);
        if (weekday is { } wd)
        {
            // "jeden Dienstag 19 Uhr": the next Tuesday at that time
            int add = ((int)wd - (int)due.DayOfWeek + 7) % 7;
            due = due.Date.AddDays(add) + due.TimeOfDay;
            if (due <= DateTime.Now) due = due.AddDays(7);
        }
        return (text, due, repeat, hasTime || weekday != null);
    }

    private void RemindText_TextChanged(object sender, TextChangedEventArgs e)
    {
        var raw = RemindText.Text.Trim();
        if (raw.Length == 0) { RemindPreview.Text = ""; return; }
        var (_, due, repeat, has) = ParseReminder(raw);
        var de = L.Culture;
        string when = !has ? L.T("in 10 Min")
            : due.Date == DateTime.Today ? L.T($"heute {due:HH:mm}")
            : due.Date == DateTime.Today.AddDays(1) ? L.T($"morgen {due:HH:mm}")
            : due.ToString("ddd HH:mm", de);
        RemindPreview.Text = repeat == RepeatKind.Einmal ? when : L.T($"{when} · {repeat.ToString().ToLower(de)}");
    }

    private void AddReminder(DateTime? forced)
    {
        var raw = RemindText.Text.Trim();
        if (raw.Length == 0 && forced == null) { RemindText.Focus(); return; }
        var (text, due, repeat, _) = ParseReminder(raw);
        if (forced.HasValue) due = forced.Value;
        H.Reminders.Add(text, due, repeat);
        RemindText.Text = "";
    }

    private async void RemindDone_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Reminder r) return;
        await Task.Delay(380); // let the check animation play first
        H.Reminders.Complete(r);
    }

    private void RemindDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Reminder r) H.Reminders.Delete(r);
    }

    private void OnReminderDue(Reminder r)
    {
        SystemSounds.Asterisk.Play();
        Wobble(); // rings like an alarm clock
        if (_state == State.Expanded && !IsActive) Collapse();
        ShowPeek(new PeekInfo { Key = "rem:" + r.Id, Important = true, Icon = "bell.ring", GlyphBrush = (Brush)FindResource("Orange"), Title = r.Text, Text = "Erinnerung · jetzt", TimeoutMs = 0 }
            .Action(L.T("Später"), () => H.Reminders.Snooze(r, 10))
            .Action(L.T("Erledigt"), () => H.Reminders.Complete(r), true));
    }

    private void OnTimerCompleted()
    {
        if (_focusRun != null) { FocusFinished(true); return; } // a focus session: no alarm – the collected messages come (1a)
        // the sound chosen in Settings › Allgemein › Timer – repeats until confirmed if enabled
        H.Sounds.Play(S.TimerSound, S.TimerSoundRepeat);
        if (_state == State.Expanded && !IsActive) Collapse();
        var again = H.Timer.DurationSeconds;
        // first a moment on the island itself (orange rings running away + one short wiggle), then the notice
        int wait = 0;
        if (S.Animations != AnimationLevel.Aus && _state != State.Expanded)
        {
            if (S.Animations == AnimationLevel.An) Ripples(((SolidColorBrush)FindResource("Orange")).Color);
            Wobble(); // rings like an alarm clock – the same as a due reminder (user wish 2026-11-18: wherever you must act)
            wait = 450;
        }
        After(wait, () => ShowPeek(new PeekInfo { Key = "timer", Important = true, Icon = "timer", GlyphBrush = (Brush)FindResource("Orange"), Title = L.T("Timer abgelaufen"), Text = H.Timer.ModeText, TimeoutMs = 0 }
            .Action(L.T("Nochmal"), () => { H.Sounds.Stop(); H.Timer.SetDurationSeconds(again); H.Timer.Start(); })
            .Action("OK", () => H.Sounds.Stop(), true)));
    }

    /// <summary>A newer PeakPill on GitHub: a notice with "Installieren" – downloads, swaps itself, starts again.</summary>
    private void OnUpdateAvailable(UpdateService.Release rel)
    {
        ShowPeek(new PeekInfo
            {
                Key = "update", Icon = "download", GlyphBrush = (Brush)FindResource("Blue"),
                Source = "PeakPill · Update", SourceIcon = "download", SourceBrush = (Brush)FindResource("Blue"), Ticker = true,
                Title = L.T($"Version {rel.Version.ToString(3)} ist da"),
                Text = FirstNoteLine(rel.Notes) ?? L.T($"Du hast {UpdateService.Current.ToString(3)}"),
                TimeoutMs = 12000
            }
            .Button("open", "Was ist neu", () => { try { Process.Start(new ProcessStartInfo(rel.Page) { UseShellExecute = true }); } catch { } })
            .Button("download", "Installieren", () => _ = InstallUpdate(rel), (Brush)FindResource("Blue")));
    }

    /// <summary>The first real line of the release notes – headings ("## Behoben") are skipped, markdown marks removed.</summary>
    private static string FirstNoteLine(string notes)
    {
        foreach (var raw in (notes ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('>')) continue;
            line = Regex.Replace(line.TrimStart('-', '*', ' '), @"\*\*|__|`|\[([^\]]*)\]\([^)]*\)", m => m.Groups[1].Success ? m.Groups[1].Value : "");
            return line.Length > 0 ? line : null;
        }
        return null;
    }

    private async Task InstallUpdate(UpdateService.Release rel)
    {
        ShowHint("download", "Update wird geladen …", (Brush)FindResource("Blue"), key: "update");
        if (await UpdateService.InstallAsync(rel))
        {
            ShowHint("refresh", "Startet neu …", (Brush)FindResource("Blue"), key: "update");
            await Task.Delay(900);
            App.Quit();
        }
        else ShowHint("xmark", "Update fehlgeschlagen", (Brush)FindResource("Danger"), key: "update");
    }

    /// <summary>The alarm rings: the timer's sound, the same moment on the island (rings + wiggle), then a notice with snooze.</summary>
    private void OnAlarmRang()
    {
        H.Sounds.Play(S.TimerSound, true);
        Note(L.T("Timer"), 60);
        if (_state == State.Expanded && !IsActive) Collapse();
        int wait = 0;
        if (S.Animations != AnimationLevel.Aus && _state != State.Expanded)
        {
            if (S.Animations == AnimationLevel.An) Ripples(((SolidColorBrush)FindResource("Orange")).Color);
            Wobble(); // the alarm clock rings – the island wobbles with it
            wait = 450;
        }
        After(wait, () => ShowPeek(new PeekInfo { Key = "alarm", Important = true, Icon = "bell.ring", GlyphBrush = (Brush)FindResource("Orange"),
                                                  Title = L.T($"Wecker · {DateTime.Now:HH:mm}"), Text = L.T("Schlummern: 5 Minuten"), TimeoutMs = 0 }
            .Action(L.T("Später"), () => { H.Sounds.Stop(); H.Alarm.Snooze(5); })
            .Action("OK", () => H.Sounds.Stop(), true)));
    }

    // =====================================================================
    //  Control Center
    // =====================================================================

    private void UpdateControlsUi()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(UpdateControlsUi)); return; }
        WifiTile.IsEnabled = H.Radios.HasWifi;
        WifiTile.IsChecked = H.Radios.WifiOn;
        BtTile.IsEnabled = H.Radios.HasBluetooth;
        BtTile.IsChecked = H.Radios.BluetoothOn;
        MicTile.IsChecked = H.Audio.MicMuted;
        // volume takes the full width when the display has no adjustable brightness
        bool bright = H.Brightness.Supported;
        BrightnessSlider.Visibility = bright ? Visibility.Visible : Visibility.Collapsed;
        BrightnessCol.Width = bright ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        SliderGap.Width = new GridLength(bright ? 8 : 0);
        var mode = PowerMode.Current();
        PowerTile.IsChecked = mode != "Ausgeglichen";
        PowerTile.ToolTip = L.T($"Energiemodus: {mode}");
        var micApp = H.Privacy.Apps.FirstOrDefault(a => a.Mic);
        MicTile.ToolTip = micApp != null ? L.T($"Mikrofon wird benutzt von {micApp.Name} · {micApp.Elapsed}") : L.T("Alle Mikrofone stummschalten");
    }


    private async void WifiTile_Click(object sender, RoutedEventArgs e) => await H.Radios.SetWifi(WifiTile.IsChecked == true);
    private async void BtTile_Click(object sender, RoutedEventArgs e) => await H.Radios.SetBluetooth(BtTile.IsChecked == true);
    private void MicTile_Click(object sender, RoutedEventArgs e) => ToggleMic();

    private void PowerTile_Click(object sender, RoutedEventArgs e)
    {
        var mode = PowerMode.Cycle();
        UpdateControlsUi();
        ShowHint("bolt", mode, (Brush)FindResource("Orange"), key: "power");
    }

    private void SettingsTile_Click(object sender, RoutedEventArgs e)
    {
        ((ToggleButton)sender).IsChecked = false;
        try { Process.Start(new ProcessStartInfo((string)((FrameworkElement)sender).Tag) { UseShellExecute = true }); } catch { }
        Collapse();
    }

    private void LockTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton t) t.IsChecked = false;
        Collapse();
        Native.LockWorkStation();
    }

    private void SleepTile_Click(object sender, RoutedEventArgs e)
    {
        Collapse();
        WF.Application.SetSuspendState(WF.PowerState.Suspend, false, false);
    }

    // =====================================================================
    //  Developer aid: renders every state to PNG, then quits
    // =====================================================================

    /// <summary>Developer aid: opens the music tab, reports the scrubber's screen rectangle and logs every mouse event on it.</summary>
    public async void SliderTest(string logFile)
    {
        _sliderTest = true; // never actually move the user's song during the test
        void Log(string s) { try { File.AppendAllText(logFile, $"{DateTime.Now:HH:mm:ss.fff} {s}\n"); } catch { } }
        Ext.SliderDragStarted += _ => Log("DragStarted");
        Ext.SliderDragEnded += _ => Log($"DragEnded value={MusicProgress.Value:0.000}");
        await Task.Delay(1200);
        _peeks.Clear(); EndPeek(); EndHint();
        Expand("Music", true);
        await Task.Delay(900);
        var dpi = VisualTreeHelper.GetDpi(this);
        var tl = MusicProgress.PointToScreen(new Point(0, 0));
        Log($"RECT {tl.X:0} {tl.Y:0} {MusicProgress.ActualWidth * dpi.DpiScaleX:0} {MusicProgress.ActualHeight * dpi.DpiScaleY:0} enabled={MusicProgress.IsEnabled} canSeek={H.Media.CanSeek} state={_state}");
        MusicProgress.PreviewMouseLeftButtonDown += (_, e) => Log($"PreviewDown handled={e.Handled} src={e.OriginalSource.GetType().Name}");
        MusicProgress.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((_, e) => Log($"PreviewDown(all) handled={e.Handled}")), true);
        MusicProgress.GotMouseCapture += (_, _) => Log("GotCapture");
        MusicProgress.LostMouseCapture += (_, _) => Log($"LostCapture value={MusicProgress.Value:0.000} state={_state}");
        MusicProgress.ValueChanged += (_, e) => Log($"Value {e.NewValue:0.000} seeking={_seeking} captured={MusicProgress.IsMouseCaptured}");
        PreviewMouseMove += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed) Log($"WinMove captured={Mouse.Captured?.GetType().Name}"); };
        Deactivated += (_, _) => Log("Window Deactivated");
        Activated += (_, _) => Log("Window Activated");
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        t.Tick += (_, _) => Log($"tick state={_state} value={MusicProgress.Value:0.000}");
        t.Start();
        Log("READY");
    }

#if DEBUG
    /// <summary>Film strips of the spring motion (open, close, bubble out/in) – one PNG per sequence, then quits.</summary>
    public async Task MotionTestAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        bool trackPeek = S.TrackPeek, wasTucked = _tucked;
        S.TrackPeek = false;
        var frames = new List<System.Windows.Media.Imaging.BitmapSource>();
        void Frame()
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)(ActualWidth * dpi.DpiScaleX), (int)(190 * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            var bg = new DrawingVisual();
            using (var dc = bg.RenderOpen())
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(70, 90, 120)), null, new Rect(0, 0, ActualWidth, 190));
            rtb.Render(bg);
            rtb.Render(Root);
            rtb.Freeze();
            frames.Add(rtb);
        }
        void Save(string name)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
                for (int i = 0; i < frames.Count; i++)
                    dc.DrawImage(frames[i], new Rect(i % 2 * ActualWidth, i / 2 * 190, ActualWidth, 190));
            var dpi = VisualTreeHelper.GetDpi(this);
            int rows = (frames.Count + 1) / 2;
            var all = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(2 * ActualWidth * dpi.DpiScaleX), (int)(rows * 190 * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            all.Render(dv);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(all));
            using var fs = File.Create(Path.Combine(dir, name + ".png"));
            enc.Save(fs);
            frames.Clear();
        }
        async Task Film(int count, int ms) { for (int i = 0; i < count; i++) { Frame(); await Task.Delay(ms); } }

        H.Agents.ForTest(); // the real Claude sessions would put their own bubble there
        H.Clipboard.Persist = false; // never write the user's clipboard history from a test run
        await Task.Delay(1500);
        if (_tucked) Untuck();
        _peeks.Clear(); EndPeek(); EndHint();
        await Task.Delay(800);

        Expand("Music", true); // sticky – the pointer is somewhere else during the test
        await Film(14, 45);
        Save("1_open");
        await Task.Delay(600);
        Collapse();
        await Film(14, 45);
        Save("2_close");
        await Task.Delay(800);

        // a bubble pulls itself out of the island …
        UpdateBubble(null);
        await Task.Delay(1200);
        _freezeBubbles = true;
        BubbleIcon.Kind = "mic"; BubbleIcon.Foreground = Brushes.White; BubbleIcon.Visibility = Visibility.Visible;
        BubbleCover.Visibility = BubbleRing.Visibility = BubbleDot.Visibility = BubbleImg.Visibility = Visibility.Collapsed;
        Bubble.Width = 32;
        SetBubbleWant(Bubble, true);
        await Film(12, 30);
        Save("3_bubble_out");
        // … and is swallowed again
        SetBubbleWant(Bubble, false);
        await Film(12, 30);
        Save("4_bubble_in");

        _freezeBubbles = false;
        // interrupted: open, close after 150 ms, open again after 300 ms
        Expand("Music", true);
        await Task.Delay(150); Collapse();
        await Task.Delay(150); Expand("Music", true);
        await Film(10, 45);
        Save("5_interrupted");

        // the island styles: pill with a bubble, then the open Music page (Glas = before, Schwarz = A, Tiefe = C)
        var theme = S.Theme;
        foreach (var t in new[] { IslandTheme.Glas, IslandTheme.Schwarz, IslandTheme.Tiefe })
        {
            S.Theme = t;
            ThemeManager.Apply();
            UpdateGlow();
            Collapse();
            await Task.Delay(900);
            _freezeBubbles = false;
            UpdateBubble(Act.Live);
            _freezeBubbles = true;
            BubbleIcon.Kind = "mic"; BubbleIcon.Foreground = Brushes.White; BubbleIcon.Visibility = Visibility.Visible;
            await Task.Delay(900);
            Frame();
            Expand("Music", true);
            await Task.Delay(900);
            Frame();
            _freezeBubbles = false;
        }
        Save("6_styles");
        S.Theme = theme;
        ThemeManager.Apply();

        // tab bar looks A / B / C on two pages, then "D" (faded to page dots)
        var tabStyle = S.TabBar;
        bool autoHide = S.TabBarAutoHide;
        foreach (var ts in new[] { TabBarStyle.Leise, TabBarStyle.Gruppen, TabBarStyle.Titel })
        {
            S.TabBar = ts;
            ApplyTabStyle();
            foreach (var page in new[] { "Music", "Clip" })
            {
                Expand(page, true);
                await Task.Delay(700);
                Frame();
            }
        }
        S.TabBarAutoHide = true;
        S.TabBar = TabBarStyle.Leise;
        ApplyTabStyle();
        Expand("Music", true);
        await Task.Delay(1800);
        Frame();
        SelectTab(L.T("Timer"), false);
        await Task.Delay(200);
        Frame();
        Save("7_tabbar");
        S.TabBar = tabStyle;
        S.TabBarAutoHide = autoHide;
        ApplyTabStyle();

        // clipboard layouts with SAMPLE entries (nothing saved – the real history comes back afterwards);
        // the cropped island pictures become the previews in Settings › Zwischenablage
        var clipLayout = S.ClipLayout;
        var real = H.Clipboard.Items.ToList();
        H.Clipboard.Persist = false;
        try
        {
            H.Clipboard.Items.Clear();
            var demoDir = Path.Combine(Path.GetTempPath(), "peakpill_demo");
            Directory.CreateDirectory(demoDir);
            string Pic(string name, Color a, Color b, int w, int h)
            {
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawRectangle(new SolidColorBrush(a), null, new Rect(0, 0, 160, 120));
                    dc.DrawRoundedRectangle(new SolidColorBrush(b), null, new Rect(28, 26, 64, 36), 6, 6);
                    dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(150, b.R, b.G, b.B)), null, new Rect(48, 74, 88, 22), 6, 6);
                }
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(160, 120, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                var f = Path.Combine(demoDir, name + ".png");
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                using (var fs = File.Create(f)) enc.Save(fs);
                return f;
            }
            var now = DateTime.Now;
            var demo = new List<ClipItem>
            {
                new() { Kind = ClipKind.Text, Text = "IBAN DE89 3704 0044 0532 0130 00", Source = L.T("Notizen"), Time = now.AddDays(-1), Pinned = true },
                new() { Kind = ClipKind.Link, Text = "https://example.com/rezepte/lasagne", Source = "Browser", Time = now.AddMinutes(-2) },
                new() { Kind = ClipKind.Image, ImageFile = Pic("a", Color.FromRgb(0x3B, 0x6E, 0xA8), Color.FromRgb(0x9C, 0xC3, 0xEA), 856, 553), ImageWidth = 856, ImageHeight = 553, IsScreenshot = true, Source = "Screenshot", Time = now.AddMinutes(-14) },
                new() { Kind = ClipKind.Text, Text = L.T("Einkaufsliste: Milch, Brot, Tomaten, Basilikum"), Source = L.T("Notizen"), Time = now.AddMinutes(-32) },
                new() { Kind = ClipKind.Image, ImageFile = Pic("b", Color.FromRgb(0xC9, 0x8B, 0x4A), Color.FromRgb(0xF0, 0xD2, 0xA8), 2160, 2880), ImageWidth = 2160, ImageHeight = 2880, Source = "Discord", Time = now.AddMinutes(-61) },
                new() { Kind = ClipKind.Text, Text = "0123456789abcdef0123456789abcdef", Source = "Terminal", Time = now.AddHours(-3) },
                new() { Kind = ClipKind.Image, ImageFile = Pic("c", Color.FromRgb(0x4F, 0x7F, 0x4F), Color.FromRgb(0xB9, 0xDB, 0xB0), 4032, 3024), ImageWidth = 4032, ImageHeight = 3024, Source = "Fotos", Time = now.AddHours(-4) },
                new() { Kind = ClipKind.Files, Files = new List<string> { @"C:\Users\Public\Rechnung_Oktober.pdf" }, Source = "Explorer", Time = now.AddDays(-1).AddHours(-2) },
                new() { Kind = ClipKind.Text, Text = L.T("Treffen um 18:30 am Hauptbahnhof, Gleis 4"), Source = "WhatsApp", Time = now.AddDays(-1).AddHours(-3) },
                new() { Kind = ClipKind.Link, Text = "https://github.com/dotnet/wpf/issues", Source = "Browser", Time = now.AddDays(-2) },
            };
            foreach (var d in demo) H.Clipboard.Items.Add(d);
            foreach (var (layout, file) in new[] { (ClipLayout.Streifen, "clip_streifen"), (ClipLayout.Liste, "clip_liste"), (ClipLayout.Kacheln, "clip_kacheln") })
            {
                S.ClipLayout = layout;
                _clipFilter = "All";
                ApplyClipLayout();
                Expand("Clip", true);
                await Task.Delay(2000);
                var dpi = VisualTreeHelper.GetDpi(this);
                var full = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(ActualWidth * dpi.DpiScaleX), (int)(ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                var bg = new DrawingVisual();
                using (var dc = bg.RenderOpen()) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x3D, 0x4A, 0x63)), null, new Rect(0, 0, ActualWidth, ActualHeight));
                full.Render(bg);
                full.Render(Root);
                var at = Island.TranslatePoint(new Point(0, 0), Root);
                var crop = new Int32Rect((int)(at.X * dpi.DpiScaleX), (int)(at.Y * dpi.DpiScaleY), (int)(Island.ActualWidth * dpi.DpiScaleX), (int)(Island.ActualHeight * dpi.DpiScaleY));
                var pic = new System.Windows.Media.Imaging.CroppedBitmap(full, crop);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(pic));
                using (var fs = File.Create(Path.Combine(dir, file + ".png"))) enc.Save(fs);
            }
        }
        finally
        {
            H.Clipboard.Items.Clear();
            foreach (var i in real) H.Clipboard.Items.Add(i);
            S.ClipLayout = clipLayout;
            _clipFilter = "All";
            ApplyClipLayout();
        }
        S.TrackPeek = trackPeek; // the test never changes the user's settings
        if (wasTucked) Tuck();
        App.Quit();
    }
#endif

    public async Task RenderTestAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        H.Agents.ForTest(); // the user's real Claude sessions are neither shown nor overwritten
        H.Clipboard.Persist = false; // a test run never writes the clipboard history (the app quits afterwards)
        bool trackPeek = S.TrackPeek;
        S.TrackPeek = false;
        async Task Snap(string name, int delay = 700)
        {
            await Task.Delay(delay);
            var dpi = VisualTreeHelper.GetDpi(this);
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)(ActualWidth * dpi.DpiScaleX), (int)(ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            var bg = new DrawingVisual();
            using (var dc = bg.RenderOpen())
                dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(70, 90, 120), Color.FromRgb(140, 110, 150), 45), null, new Rect(0, 0, ActualWidth, ActualHeight));
            rtb.Render(bg);
            rtb.Render(Root);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var fs = File.Create(Path.Combine(dir, name + ".png"));
            enc.Save(fs);
        }
        bool startedTucked = _tucked;
        try
        {
            await Task.Delay(1500);
            if (startedTucked) Untuck(); // the test needs the island visible – tucked again at the end
            _peeks.Clear();
            EndPeek();
            EndHint();
            await Snap("01_pill");
            ShowHint("copy", L.T("Kopiert"), null, check: true);
            await Snap("02a_hint_text", 450);
            await Snap("02b_hint_check", 550);
            await Task.Delay(1200);
            ShowHint("mic.slash", "Stumm", (Brush)FindResource("Danger"), key: "mic");
            await Snap("02c_hint_mic", 500);
            await Task.Delay(1800);
            ShowPeek(new PeekInfo { Icon = "bell", GlyphBrush = (Brush)FindResource("Orange"), Title = "Wäsche aufhängen", Text = "Jetzt", TimeoutMs = 0 }
                .Action(L.T("Später"), null).Action(L.T("Erledigt"), null, true));
            await Snap("02d_alert");
            EndPeek();
            // Spiele tab: a few test tiles (installed Steam games + programs), nothing saved, removed afterwards
            H.Launcher.Persist = false;
            var testGames = new List<LaunchItem>();
            if (H.Launcher.Items.Count == 0)
            {
                foreach (var (id, name) in H.Steam.InstalledGames().Take(3)) testGames.Add(H.Launcher.AddSteam(id, name));
                int before = H.Launcher.Items.Count;
                H.Launcher.AddFiles(new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe") });
                testGames.AddRange(H.Launcher.Items.Skip(before));
            }
            foreach (var tab in TabOrder)
            {
                Expand(tab, true);
                if (tab == "Games") UpdateGamesUi();
                await Snap("03_" + tab);
            }
            foreach (var g in testGames) H.Launcher.Remove(g);
            H.Launcher.Persist = true;
            // Spotify volume bar (only reads the current volume, never changes it)
            if (IsSpotify)
            {
                SelectTab("Music", false);
                MusicVol_Click(null, null);
                await Snap("04h_Music_volume", 500);
                ExitVolMode();
            }

            // search text must start exactly where the placeholder starts; the "delete all" question (never answered here)
            SelectTab("Clip", false);
            ClipSearch.Text = L.T("Suchen");
            await Snap("04f_Clip_search_typed", 400);
            ClipSearch.Text = "";
            ShowClipConfirm(12, 2); // display only – "Löschen" is never pressed in the test
            await Snap("04g_Clip_confirm", 600);
            HideClipConfirm();
            await Task.Delay(300);

            var firstClip = H.Clipboard.Items.FirstOrDefault();
            if (firstClip != null)
            {
                SelectTab("Clip", false);
                firstClip.IsExpanded = true;
                await Snap("04_Clip_expanded");
                firstClip.IsExpanded = false;
                firstClip.JustCopied = true;
                await Snap("04d_Clip_copied", 500);
                firstClip.JustCopied = false;
                var img = H.Clipboard.Items.FirstOrDefault(i => i.Kind == ClipKind.Image);
                if (img != null)
                {
                    img.IsExpanded = true; ClipList.ScrollIntoView(img); await Snap("04b_Clip_image"); img.IsExpanded = false;
                    // Quick-Look view: render only its own content (no real screen capture)
                    QuickLookWindow.Show(img, null, null, null, edited: _ => { });
                    await Task.Delay(900);
                    void SnapQuickLook(QuickLookWindow ql, FrameworkElement qc, string name)
                    {
                        var dpi = VisualTreeHelper.GetDpi(ql);
                        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(ql.ActualWidth * dpi.DpiScaleX), (int)(ql.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                        var bg = new DrawingVisual();
                        using (var dc = bg.RenderOpen())
                        {
                            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(70, 90, 120), Color.FromRgb(140, 110, 150), 45), null, new Rect(0, 0, ql.ActualWidth, ql.ActualHeight));
                            dc.DrawRectangle(ql.Background, null, new Rect(0, 0, ql.ActualWidth, ql.ActualHeight));
                        }
                        rtb.Render(bg);
                        rtb.Render(qc);
                        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                        using (var fs = File.Create(Path.Combine(dir, name))) enc.Save(fs);
                    }
                    if (QuickLookWindow.Current is { } ql1 && ql1.Content is FrameworkElement qc1)
                    {
                        SnapQuickLook(ql1, qc1, "04e_quicklook.png");
                        ql1.EditForTest(L.T("Frei"), false);
                        await Task.Delay(400);
                        SnapQuickLook(ql1, qc1, "04f_quicklook_edit.png");
                        ql1.EditForTest("16:9", true);
                        await Task.Delay(400);
                        SnapQuickLook(ql1, qc1, "04g_quicklook_edit_turned.png");
                        if (ql1.ResultForTest() is { } res)
                        {
                            var re = new System.Windows.Media.Imaging.PngBitmapEncoder();
                            re.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(res));
                            using var rf = File.Create(Path.Combine(dir, $"04h_edit_result_{res.PixelWidth}x{res.PixelHeight}_src{img.ImageWidth}x{img.ImageHeight}.png"));
                            re.Save(rf);
                        }
                        ql1.CloseNow();
                    }
                }
            }
            SelectTab(L.T("Timer"), false);
            H.Timer.SetDuration(90);
            H.Timer.Start();
            await Snap("04c_Timer_running");
            H.Timer.Reset();
            H.Timer.SetDuration(25);
            Collapse();

            // Live: made-up apps (Discord streams + mic, Chrome mic) – nothing real is touched
            var discord = Directory.GetFiles(Environment.ExpandEnvironmentVariables(@"%LocalAppData%\Discord"), "Discord.exe", SearchOption.AllDirectories).FirstOrDefault()
                          ?? @"C:\Windows\explorer.exe";
            var chrome = new[] { @"C:\Program Files\Google\Chrome\Application\chrome.exe", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe" }.FirstOrDefault(File.Exists)
                         ?? @"C:\Windows\notepad.exe";
            var fsMode = S.Fullscreen;
            S.Fullscreen = FullscreenMode.Normal; // a fullscreen window on the test PC would show the CPU bar instead
            H.Privacy.Demo((discord, true, false, false, 724));
            await Snap("08a_live_pill_mic", 500);
            H.Privacy.DemoStarted('m');
            await Snap("08b_live_hint_mic", 600);
            EndHint();
            H.Privacy.Demo((discord, true, false, true, 192));
            await Snap("08c_live_pill_screen", 500);
            Expand("Live", true);
            await Snap("08d_live_single");
            Collapse();
            H.Privacy.Demo((discord, false, false, true, 192), (chrome, true, true, false, 75));
            await Snap("08e_live_pill_two", 500);
            Expand("Live", true);
            await Snap("08f_live_list");
            Collapse();
            // Steam download next to a Discord call (made up)
            H.Privacy.Demo((discord, true, false, false, 300));
            H.Steam.Demo("ELDEN RING", 0.62, L.T("12,4 MB/s"), "1245620");
            await Snap("08g_pill_steam_ring", 700);
            Expand("Live", true);
            await Snap("08h_live_steam_row");
            Collapse();
            H.Steam.EndDemo();

            // AI agents: made-up Claude session
            H.Privacy.Demo((discord, true, false, false, 300));
            H.Agents.Demo(L.T("Spotify-Lautstärke einbauen"), "Bearbeitet IslandWindow.xaml.cs", 3, 5, 754, AgentState.Running);
            await Snap("09a_pill_music_mic_agent", 700);
            H.Privacy.EndDemo();
            Expand("Agent", true);
            await Snap("09b_agent_running");
            H.Agents.Demo(L.T("Frage beantworten"), L.T("Durchsucht den Code"), 0, 0, 42, AgentState.Running);
            await Snap("09c_agent_loader", 900);
            H.Agents.Demo(L.T("Spotify-Lautstärke einbauen"), L.T("Wartet auf dich"), 3, 5, 800, AgentState.Waiting);
            await Snap("09d_agent_waiting");
            H.Agents.Demo(L.T("Spotify-Lautstärke einbauen"), L.T("Fertig"), 5, 5, 842, AgentState.Done);
            await Snap("09e_agent_done");
            // two sessions at once → list
            H.Agents.Demo(L.T("Spiele-Tab bauen"), "Bearbeitet LauncherService.cs", 2, 4, 312, AgentState.Running, "demo2", "dynamic island");
            UpdateAgentUi();
            await Snap("09f_agent_two", 500);
            Collapse();
            H.Agents.EndDemo();

            // Mitteilungen: real notifications + made-up alarms / suggestions (never saved)
            H.Prices.Persist = false;
            var demoAlarms = new[]
            {
                new PriceAlert { Kind = AlertKind.Shop, Name = L.T("Sony WH-1000XM5 · Amazon"), Condition = AlertCondition.Below, Target = 299, Price = 347, Currency = "€", Status = L.T("geprüft 15:05") },
                new PriceAlert { Kind = AlertKind.Crypto, Name = "Bitcoin", Condition = AlertCondition.Above, Target = 70000, Price = 65292, Change = -1.8, Currency = "€", Status = L.T("geprüft 15:05") },
                new PriceAlert { Kind = AlertKind.Stock, Name = "Apple Inc. (AAPL)", Condition = AlertCondition.Move, Target = 5, Price = 355.63, Change = 1.8, Currency = "$", Status = L.T("geprüft 15:05") },
                new PriceAlert { Kind = AlertKind.Steam, Name = "Hollow Knight: Silksong · Steam", Condition = AlertCondition.Sale, Price = 19.5, Currency = "€", Status = L.T("geprüft 15:05") },
            };
            foreach (var d in demoAlarms) H.Prices.Items.Add(d);
            var demoSug = new Suggestion { Key = "demo", Title = L.T("Amazon-Produkt erkannt"), Text = L.T("Preisalarm für „Sony WH-1000XM5“?"), IconKind = "cart" };
            H.Suggestions.Items.Insert(0, demoSug);
            Expand("Notify", true);
            SegNotif.IsChecked = true;
            await Snap("10a_notify_list");
            SegAlarm.IsChecked = true;
            await Snap("10b_notify_alarms", 500);
            SegSuggest.IsChecked = true;
            await Snap("10c_notify_suggestions", 500);
            SegNotif.IsChecked = true;
            Collapse();
            foreach (var d in demoAlarms) H.Prices.Items.Remove(d);
            H.Suggestions.Items.Remove(demoSug);
            H.Prices.Persist = true;
            SelectTab(L.T("Timer"), false);
            Expand(L.T("Timer"), true);
            await Snap("10d_timer_seconds");
            Collapse();
            S.Fullscreen = fsMode;
            await Task.Delay(300);

            // tucked: invisible, strip appears near the top (forced visible for the snapshot)
            bool wasTucked = S.IslandTucked;
            Tuck();
            await Snap("07a_tucked", 500);
            _nearTimer.Stop();
            TuckStrip.BeginAnimation(OpacityProperty, null);
            TuckStrip.Opacity = 1;
            await Snap("07b_tucked_near", 300);
            Untuck();
            Collapse();
            if (wasTucked) Tuck();
            await Task.Delay(600);
            // editor + Live Text on a generated test image (not added to the user's history/clipboard)
            var testPng = Path.Combine(dir, "test_input.png");
            {
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 900, 420));
                    var tf = new Typeface("Segoe UI");
                    void T(string s, double y, double size) => dc.DrawText(new FormattedText(s, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, tf, size, Brushes.Black, 1.0), new Point(40, y));
                    T(L.T("Einkaufsliste für Samstag"), 40, 34);
                    T(L.T("Milch, Brot, Äpfel und Kaffee"), 120, 26);
                    T(L.T("Termin beim Zahnarzt um 14:30 Uhr"), 180, 26);
                    T(L.T("PeakPill Live Text funktioniert offline."), 240, 26);
                }
                var rtbIn = new System.Windows.Media.Imaging.RenderTargetBitmap(900, 420, 96, 96, PixelFormats.Pbgra32);
                rtbIn.Render(dv);
                ScreenshotService.SavePng(rtbIn, testPng);
            }
            var item = new ClipItem { Kind = ClipKind.Image, ImageFile = testPng, ImageWidth = 900, ImageHeight = 420 };
            {
                EditorWindow.Open(item, true);
                await Task.Delay(3000);
                var ed = Application.Current.Windows.OfType<EditorWindow>().FirstOrDefault();
                if (ed != null) { ed.SaveSnapshot(Path.Combine(dir, "06_editor.png")); ed.Close(); }
            }
            SettingsWindow.ShowSingle();
            await Task.Delay(900);
            var sw = Application.Current.Windows.OfType<SettingsWindow>().First();
            // generate all own timer tones (silently) and list them for checking
            File.WriteAllLines(Path.Combine(dir, "sounds.txt"), H.Sounds.Options().Select(o =>
            {
                var p = SoundService.PathOf(o.Id);
                return $"{o.Name} | {o.Id} | {(p != null && File.Exists(p) ? new FileInfo(p).Length + " bytes" : "-")}";
            }));
            foreach (var page in new[] { "General", "Widgets", "Look" })
            {
                sw.ShowPage(page);
                await Task.Delay(500);
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)sw.ActualWidth, (int)sw.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                rtb.Render((Visual)sw.Content);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                using var fs = File.Create(Path.Combine(dir, "05_settings_" + page + ".png"));
                enc.Save(fs);
            }
        }
        catch (Exception ex) { App.Log(ex); }
        S.TrackPeek = trackPeek;
        if (startedTucked && !_tucked) Tuck(); // leave the user's choice as it was
        App.Quit();
    }
}
