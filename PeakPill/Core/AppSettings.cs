namespace PeakPill.Core;

public enum IslandShape { Pill, Notch }
/// <summary>Schwarz = variant "A" (pure black + light edge on top), Tiefe = variant "C" (black with depth + cover glow).</summary>
public enum IslandTheme { Glas, Schwarz, Graphit, Mitternacht, Aurora, Tiefe }
public enum FullscreenMode { StatsBar, Hide, Normal }
/// <summary>Tab bar of the open island (approved mockup 2026-11-14): A = quiet with the active tab's name, B = groups, C = title line.</summary>
public enum TabBarStyle { Leise, Gruppen, Titel }
/// <summary>Clipboard page (approved mockup 2026-11-14): A list by day, B tiles, C picture strip + list (default).</summary>
public enum ClipLayout { Liste, Kacheln, Streifen }
/// <summary>The small moments on the island (Claude done, timer end …): full (subtle), reduced (no light/rings), off.</summary>
public enum AnimationLevel { An, Reduziert, Aus }
/// <summary>Who answers on the KI page.</summary>
public enum AiProvider { Claude, ChatGpt, Gemini }

public enum HotkeyAction
{
    ToggleIsland, Clipboard, Screenshot, ScreenshotOcr, Shelf, Music, Widgets, Reminders, Controls,
    MicMute, QueueMode, PlayPause, NextTrack, PrevTrack, FocusTimer, Settings, AiChat, QuickNote, AiSelection, AiRegion, Calculator, Search, ColorPicker, InstantReplay, VoiceCommand, SongRecognize
}

public sealed record HotkeyDef(HotkeyAction Action, string Label, string Default);

public static class HotkeyInfo
{
    // Defaults avoid Ctrl+Alt+Q/E/M/2/3/7-0 because Ctrl+Alt = AltGr on German keyboards (@ € µ ² ³ { [ ] }).
    public static readonly HotkeyDef[] All =
    {
        new(HotkeyAction.ToggleIsland,  L.T("Insel öffnen / schließen"),        "Ctrl+Alt+I"),
        new(HotkeyAction.Clipboard,     L.T("Zwischenablage öffnen"),           "Ctrl+Alt+V"),
        new(HotkeyAction.Screenshot,    L.T("Screenshot (Bereich)"),            "Ctrl+Alt+S"),
        new(HotkeyAction.ScreenshotOcr, L.T("Screenshot → Text erkennen"),      "Ctrl+Alt+T"),
        new(HotkeyAction.Shelf,         L.T("Dateiablage öffnen"),              "Ctrl+Alt+F"),
        new(HotkeyAction.Music,         L.T("Musik öffnen"),                    "Ctrl+Alt+P"),
        new(HotkeyAction.Widgets,       L.T("Timer öffnen"),                    "Ctrl+Alt+W"),
        new(HotkeyAction.Reminders,     L.T("Neue Erinnerung"),                 "Ctrl+Alt+R"),
        new(HotkeyAction.Controls,      L.T("Schnellsteuerung öffnen"),         "Ctrl+Alt+C"),
        new(HotkeyAction.MicMute,       L.T("Mikrofon stumm / an"),             "Ctrl+Alt+K"),
        new(HotkeyAction.QueueMode,     L.T("Stapel-Modus an / aus"),           "Ctrl+Alt+X"),
        new(HotkeyAction.PlayPause,     L.T("Wiedergabe / Pause"),              ""),
        new(HotkeyAction.NextTrack,     L.T("Nächster Titel"),                  ""),
        new(HotkeyAction.PrevTrack,     L.T("Vorheriger Titel"),                ""),
        new(HotkeyAction.FocusTimer,    L.T("Timer starten / pausieren"),       ""),
        new(HotkeyAction.Settings,      L.T("Einstellungen öffnen"),            "Ctrl+Alt+O"),
        new(HotkeyAction.AiChat,        L.T("KI-Chat öffnen"),                  ""),
        new(HotkeyAction.QuickNote,     L.T("Schnellnotiz schreiben"),          "Ctrl+Alt+N"),
        new(HotkeyAction.AiSelection,   L.T("Markierten Text an die KI"),       "Ctrl+Alt+A"),
        new(HotkeyAction.AiRegion,      L.T("Bildschirmbereich erklären"),      "Ctrl+Alt+B"),
        new(HotkeyAction.Calculator,    L.T("Rechner"),                         "Ctrl+Alt+Y"),
        new(HotkeyAction.Search,        L.T("Schnellsuche"),                    "Ctrl+Alt+Space"),
        new(HotkeyAction.ColorPicker,   L.T("Farbpipette"),                     "Ctrl+Alt+H"),
        new(HotkeyAction.InstantReplay, L.T("Instant Replay (OBS)"),            "Ctrl+Alt+J"),
        new(HotkeyAction.VoiceCommand,  L.T("Sprachbefehl (halten)"),           "Ctrl+Alt+G"),
        new(HotkeyAction.SongRecognize, L.T("Welcher Song läuft?"),             "Ctrl+Alt+L"),
    };
}

public sealed class DesktopWidgetState
{
    public string Kind { get; set; }
    public double Left { get; set; }
    public double Top { get; set; }
    /// <summary>S (1×1), M (2×1), L (2×2) – iOS sizes (2026-11-16).</summary>
    public string Size { get; set; } = "S";
}

public sealed class AppSettings
{
    // Look
    public int SettingsVersion { get; set; }
    public IslandShape Shape { get; set; } = IslandShape.Pill;
    public IslandTheme Theme { get; set; } = IslandTheme.Schwarz;
    public string Accent { get; set; } = "#0A84FF";
    public bool MusicGlow { get; set; }
    public TabBarStyle TabBar { get; set; } = TabBarStyle.Leise;
    /// <summary>"D": the tab bar fades to small page dots while the pointer is not on it.</summary>
    public bool TabBarAutoHide { get; set; }
    public ClipLayout ClipLayout { get; set; } = ClipLayout.Streifen;
    /// <summary>Own order of the tabs (dragged in Settings); empty = the built-in order.</summary>
    public List<string> TabOrder { get; set; } = new();
    /// <summary>Tabs switched off in Settings.</summary>
    public List<string> HiddenTabs { get; set; } = new();
    /// <summary>Minimal-Modus: the tabs in <see cref="MinimalTabs"/> hide while there is nothing on them.</summary>
    public bool MinimalMode { get; set; }
    /// <summary>Several screens: the island moves to the screen of the window you work in (off by default, 2026-11-17).</summary>
    public bool FollowActiveScreen { get; set; }
    /// <summary>Timer, Claude, a call … as circles BESIDE the island (the old look). Off (default, 2026-11-18): a ring stack
    /// inside the island.</summary>
    public bool SideBubbles { get; set; }
    /// <summary>The time in the middle of the quiet island (2026-11-18, mockup B).</summary>
    public bool ClockInIsland { get; set; } = true;
    /// <summary>While you type in another window the closed island becomes see-through (2026-11-18).</summary>
    public bool FadeWhileTyping { get; set; } = true;
    public bool IslandBottom { get; set; } // 55 (2026-11-18): above the taskbar, opening upwards
    // workday (2026-11-18): eye break every 20 min, the morning and the evening moment, screen time
    public bool EyeBreak { get; set; } = true;
    public bool MorningMoment { get; set; } = true;
    public bool EveningMoment { get; set; } = true;
    public int EveningMinutes { get; set; } = 18 * 60;
    public bool ScreenTime { get; set; } = true;
    public bool LyricLine { get; set; } = true;
    // helpers (2026-11-18): small offers after copying, and files just saved
    public bool CopyHelpers { get; set; } = true;
    public bool SelectionMenu { get; set; } = true; // 2026-11-19: selecting text with the mouse offers the KI buttons
    public bool SavedFiles { get; set; } = true;
    // games (2026-11-18): a reminder after so many minutes played today (0 = off), sales on the Steam wishlist
    public int GameReminderMinutes { get; set; } = 120;
    public bool WishlistSales { get; set; } = true;
    public List<string> WishlistTold { get; set; } = new();
    // 22 (2026-11-26): songs the island recognized (newest first, at most 30) – only on this PC
    public List<SongSeen> SongHistory { get; set; } = new();
    public DateTime WishlistCheckedOn { get; set; }
    // fun (2026-11-18): rain and snow in the island, holidays and birthdays, water, achievements
    public bool WeatherFx { get; set; } = true;
    public bool Holidays { get; set; } = true;
    public List<string> Birthdays { get; set; } = new();   // "Name|dd.MM"
    public bool Water { get; set; } = true;
    public int WaterGoal { get; set; } = 8;
    public Dictionary<string, DateTime> Achievements { get; set; } = new();
    public List<string> MinimalTabs { get; set; } = new() { "Clip", "Ai", "Agent", "Send", "Music", "Notify" };
    public AnimationLevel Animations { get; set; } = AnimationLevel.An;

    // Behaviour
    public bool OpenOnHover { get; set; } = true;
    /// <summary>Opening shows what just happened (new song, copied/pasted, Claude done, call …) – time-based, not learned.</summary>
    public bool SmartTab { get; set; } = true;
    public int HoverDelayMs { get; set; } = 150;
    public bool ShowSeconds { get; set; }
    public bool TrackPeek { get; set; } = true;
    /// <summary>A page in the browser asks for a verification code → open the mail tab (and offer codes from notifications).</summary>
    public bool CodeMail { get; set; } = true;
    public string MailUrl { get; set; } = "https://mail.google.com";
    /// <summary>Look for a newer PeakPill on GitHub at start (and every 6 hours).</summary>
    public bool UpdateCheck { get; set; } = true;
    /// <summary>Volume (and laptop brightness) changes show a level bar in the island.</summary>
    public bool LevelInIsland { get; set; } = true;
    /// <summary>Music page queue: "spotify" (Spotify's own – view and add) or "island" (the island's own, sorted freely).</summary>
    public string SpotifyQueueMode { get; set; } = "spotify";
    /// <summary>Two little eyes on the left of the resting island that follow the pointer.</summary>
    public bool IslandEyes { get; set; } = true;
    /// <summary>Answer from the island: off = the text waits in the chat (you press Enter), on = sent right away.</summary>
    public bool ReplyAutoSend { get; set; }
    /// <summary>Football (OpenLigaDB): league code (bl1, bl2, bl3, dfb …) and the team to follow (name or short name).</summary>
    public string SportLeague { get; set; } = "bl1";
    public string SportTeam { get; set; } = "";
    /// <summary>Twitch: the user's own free app (dev.twitch.tv) – client id and secret (secret stored encrypted).</summary>
    public string TwitchClientId { get; set; } = "";
    public string TwitchSecretProtected { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore] public string TwitchSecret { get => PeakPill.Services.DiscordService.Unprotect(TwitchSecretProtected); set => TwitchSecretProtected = PeakPill.Services.DiscordService.Protect(value); }
    public List<string> TwitchChannels { get; set; } = new();
    /// <summary>YouTube channels (@name or channel id) – checked on their public /live page, no key.</summary>
    public List<string> YouTubeChannels { get; set; } = new();
    /// <summary>DHL: the user's own free key (developer.dhl.com, encrypted) and parcels as "Name: number".</summary>
    public string DhlKeyProtected { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore] public string DhlKey { get => PeakPill.Services.DiscordService.Unprotect(DhlKeyProtected); set => DhlKeyProtected = PeakPill.Services.DiscordService.Protect(value); }
    public List<string> Parcels { get; set; } = new();
    /// <summary>KI page (2026-11-17): which service answers, the user's own API keys (encrypted, per service) and the
    /// chosen model per service.</summary>
    /// <summary>"en" (default) or "de" – asked by the installer, switchable in Settings › Allgemein (2026-11-17).</summary>
    public string Language { get; set; } = "en";
    /// <summary>LocalSend in the island (2026-11-17): on/off, the name others see (empty = PC name), our random id.</summary>
    public bool LocalSendOn { get; set; } = true;
    public string LocalSendName { get; set; } = "";
    public string LocalSendId { get; set; } = "";
    public AiProvider AiProvider { get; set; } = AiProvider.Claude;
    public Dictionary<string, string> AiKeysProtected { get; set; } = new();
    public Dictionary<string, string> AiModels { get; set; } = new();
    /// <summary>Where the island sits (2026-11-16): the monitor (device name, null = main screen) and the centre as a
    /// share of the screen width from the middle (−0.5 … 0.5, 0 = centred).</summary>
    public string IslandScreen { get; set; }
    public double IslandX { get; set; }
    /// <summary>The island grows with the resolution as if every screen were 1080 px tall (Windows scaling taken out).</summary>
    public bool ScaleWithScreen { get; set; } = true;
    /// <summary>The user's own size on top (0.7 … 1.4).</summary>
    public double IslandSize { get; set; } = 1.0;
    /// <summary>Headphones / Bluetooth audio connected → the island shows it with the battery.</summary>
    public bool DeviceInIsland { get; set; } = true;
    /// <summary>Plugged in / battery low (laptops only).</summary>
    public bool PowerInIsland { get; set; } = true;
    /// <summary>The thin strip of the tucked island glowed once to show where it is.</summary>
    public bool TuckHintShown { get; set; }
    /// <summary>The Steam tile was put into Spiele once (removing it later is respected).</summary>
    public bool SteamTileAdded { get; set; }
    public bool Autostart { get; set; }
    public FullscreenMode Fullscreen { get; set; } = FullscreenMode.StatsBar;
    public string LastTab { get; set; } = "Music";
    /// <summary>Island tucked away into a thin strip that only appears when the cursor comes near.</summary>
    public bool IslandTucked { get; set; }

    // Shortcuts: action name -> "Ctrl+Alt+V"
    public Dictionary<string, string> Hotkeys { get; set; } = new();

    // Clipboard
    public int ClipboardLimit { get; set; } = 200;
    public bool ClipboardPersist { get; set; } = true;
    public bool ClickPastes { get; set; }
    public bool ConfirmCopy { get; set; } = true;
    public bool QueueMode { get; set; }

    // Screenshot
    public string ScreenshotFolder { get; set; } = "";
    public bool ScreenshotAutoSave { get; set; }
    public bool ScreenshotAsk { get; set; } = true;
    public int ScreenshotTimer { get; set; }
    public string LastRegion { get; set; } = "";

    // Timer
    public string TimerSound { get; set; } = "synth:soft";
    /// <summary>The alarm (Timer page › Wecker): when it rings next, null = none.</summary>
    public DateTime? AlarmAt { get; set; }
    public bool TimerSoundRepeat { get; set; } = true;
    /// <summary>The user's own Discord application (for mute status / self-mute via Discord RPC).</summary>
    public string DiscordClientId { get; set; } = "";
    /// <summary>Client secret, encrypted for this Windows user (DPAPI).</summary>
    public string DiscordSecret { get; set; } = "";
    /// <summary>The keybind set in Discord for toggling screen sharing – the island presses it to start / stop the stream.</summary>
    public string DiscordStreamKey { get; set; } = "";
    /// <summary>obs-websocket password (encrypted, DPAPI) – only if OBS asks for one.</summary>
    public string ObsPassword { get; set; } = "";
    /// <summary>The user's own Spotify app (client id, PKCE – no secret) for the queue.</summary>
    public string SpotifyClientId { get; set; } = "";
    /// <summary>The mic indicator next to the island: white (default) or blue.</summary>
    public bool MicIndicatorBlue { get; set; }

    // Widgets
    public string WeatherCity { get; set; } = "";
    public string WeatherPlace { get; set; } = "";
    public double WeatherLat { get; set; }
    public double WeatherLon { get; set; }
    public List<string> WorldClocks { get; set; }
    public List<DesktopWidgetState> DesktopWidgets { get; set; } = new();

    public void EnsureDefaults()
    {
        if (SettingsVersion < 2)
        {
            // v2 = Apple redesign: pure black island, no coloured glow
            Theme = IslandTheme.Schwarz;
            MusicGlow = false;
            SettingsVersion = 2;
        }
        if (SettingsVersion < 3)
        {
            // v3: a click in the clipboard list only copies – it no longer jumps to another window and pastes
            ClickPastes = false;
            SettingsVersion = 3;
        }
        // only Schwarz, Tiefe and Glas are offered since 2026-11-14 – older styles fall back to Schwarz
        if (Theme is IslandTheme.Graphit or IslandTheme.Mitternacht or IslandTheme.Aurora) Theme = IslandTheme.Schwarz;
        if (string.IsNullOrWhiteSpace(MailUrl)) MailUrl = "https://mail.google.com";
        Hotkeys ??= new();
        foreach (var h in HotkeyInfo.All)
            if (!Hotkeys.ContainsKey(h.Action.ToString()))
                Hotkeys[h.Action.ToString()] = h.Default;

        if (string.IsNullOrWhiteSpace(ScreenshotFolder))
            ScreenshotFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "PeakPill");

        WorldClocks ??= new() { "Eastern Standard Time", "GMT Standard Time", "Tokyo Standard Time" };
        DesktopWidgets ??= new();
        if (ClipboardLimit < 10) ClipboardLimit = 200;
        if (HoverDelayMs < 0) HoverDelayMs = 150;
    }
}

/// <summary>A song the island recognized (22).</summary>
public sealed class SongSeen
{
    public string Title { get; set; }
    public string Artist { get; set; }
    public string Url { get; set; }
    public string Cover { get; set; }
    public DateTime At { get; set; }
}
