using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using PeakPill.Core;
using PeakPill.Services;

namespace PeakPill.UI;

/// <summary>
/// Fun (user's picks 2026-11-18): 48a/b rain and snow falling through the island · 49 holidays and birthdays with
/// confetti · 51 a glass of water that fills over the day · 52 small achievements.
/// </summary>
public partial class IslandWindow
{
    private readonly Random _funRnd = new();
    private DispatcherTimer _funTimer;
    private int _funTick;
    private int _wasWet = -1;
    private bool _weatherPending;
    private DateTime _lastShower, _lastDrinkAsk = DateTime.Now, _drinkActiveSince = DateTime.Now;

    private void WireFun()
    {
        _funTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _funTimer.Tick += (_, _) => FunTick();
        _funTimer.Start();
        H.Weather.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(WeatherService.HourCodes)) Ui.Run(WeatherMoment); };
        H.Day.PropertyChanged += (_, _) => Ui.Run(UpdateWaterGlass);
        UpdateWaterGlass();
    }

    private void FunTick()
    {
        _funTick++;
        // 48b: rain / snow now – now and then a few drops fall through the closed island, whatever it shows (before
        // 2026-11-26 only with the clock: with music playing the user never saw a drop)
        if (S.WeatherFx && _state == State.Collapsed && WeatherService.Precip(H.Weather.Code) is var p and > 0
            && DateTime.Now - _lastShower > TimeSpan.FromSeconds(45))
        {
            _lastShower = DateTime.Now;
            Shower(p, 3400, 12);
        }
        if (_weatherPending && _state == State.Collapsed) WeatherMoment();
        HolidayCheck();
        DrinkCheck();
        if (_funTick % 3 == 0) CheckAchievements();
        UpdateWaterGlass();
    }

    // =====================================================================
    //  48 · rain and snow
    // =====================================================================

    /// <summary>
    /// 48a: dry → rain or snow: a bigger shower falls through the island. Only the effect, no words (user wish
    /// 2026-11-26: "nicht dass dort steht es regnet, sondern einfach nur den Effekt").
    /// </summary>
    private void WeatherMoment()
    {
        if (!S.WeatherFx) return;
        int wet = WeatherService.Precip(H.Weather.Code);
        // already raining when PeakPill starts counts too; the island busy (open, a card) → it waits for the next quiet moment
        if (_wasWet != 1 && wet > 0 && _state != State.Collapsed) { _weatherPending = true; return; }
        _weatherPending = false;
        if (_wasWet != 1 && wet > 0)
        {
            _lastShower = DateTime.Now;
            Shower(wet, 3800, 24);
        }
        _wasWet = wet > 0 ? 1 : 0;
    }

    /// <summary>Settings "Testen": rain, then snow – whatever the weather and the switch say.</summary>
    public void WeatherPreview()
    {
        bool open = _state == State.Expanded;
        if (open) Collapse();
        After(open ? 350 : 0, () => Shower(1, 3800, 24));
        After(open ? 5150 : 4800, () => Shower(2, 4200, 18));
    }

    /// <summary>Drops (1) or flakes (2) fall through the island for a moment.</summary>
    private void Shower(int kind, int ms, int count)
    {
        if (S.Animations == AnimationLevel.Aus || !Island.IsVisible) return;
        double w = Island.ActualWidth, h = Island.ActualHeight;
        if (w < 20) return;
        bool snow = kind == 2;
        var color = snow ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x64, 0xD2, 0xFF));
        for (int i = 0; i < count; i++)
        {
            Shape s = snow ? new Ellipse { Width = 4.5, Height = 4.5, Fill = color, Opacity = 0.95 }
                           : new Rectangle { Width = 2, Height = 10, RadiusX = 1, RadiusY = 1, Fill = color, Opacity = 0.95 };
            var t = new TranslateTransform(0, -8);
            s.RenderTransform = t;
            Canvas.SetLeft(s, 10 + _funRnd.NextDouble() * (w - 20));
            WeatherFx.Children.Add(s);
            double fall = snow ? 1900 + _funRnd.Next(900) : 750 + _funRnd.Next(350); // slow enough to see in a pill only ~37 px tall
            var begin = TimeSpan.FromMilliseconds(_funRnd.Next(Math.Max(1, ms - (int)fall)));
            var a = new DoubleAnimation(-8, h + 4, TimeSpan.FromMilliseconds(fall)) { BeginTime = begin, FillBehavior = FillBehavior.Stop };
            if (snow) t.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-4, 4, TimeSpan.FromMilliseconds(fall / 2)) { BeginTime = begin, AutoReverse = true });
            a.Completed += (_, _) => WeatherFx.Children.Remove(s);
            t.Y = -20;
            t.BeginAnimation(TranslateTransform.YProperty, a);
        }
    }

    // =====================================================================
    //  49 · holidays and birthdays
    // =====================================================================

    private void HolidayCheck()
    {
        if (!S.Holidays || H.Day.Today.HolidayShown || H.Usage.ActiveStreak < 5 || !CanMoment()) return;
        var (text, style) = TodaysOccasion();
        if (text == null) return;
        H.Day.Today.HolidayShown = true;
        H.Day.Save();
        _hintHoldMs = 4500;
        ShowHint(style == "birthday" ? "gift" : "sparkles", text, style == "xmas" ? (Brush)FindResource("Orange") : Brushes.White, key: "holiday");
        After(250, () => Confetti(style));
    }

    /// <summary>A birthday from the settings, or a German public holiday (plus Christmas Eve and New Year's Eve).</summary>
    private (string Text, string Style) TodaysOccasion()
    {
        var d = DateTime.Today;
        foreach (var b in S.Birthdays)
        {
            var parts = b.Split('|');
            if (parts.Length == 2 && DateTime.TryParseExact(parts[1].Trim() + "." + d.Year, "dd.MM.yyyy", null, System.Globalization.DateTimeStyles.None, out var bd) && bd == d)
                return (L.T($"Heute hat {parts[0].Trim()} Geburtstag"), "birthday");
        }
        var easter = Easter(d.Year);
        if (d.Month == 1 && d.Day == 1) return (L.T("Frohes neues Jahr"), "newyear");
        if (d == easter.AddDays(-2)) return (L.T("Karfreitag"), "calm");
        if (d == easter || d == easter.AddDays(1)) return (L.T("Frohe Ostern"), "easter");
        if (d.Month == 5 && d.Day == 1) return (L.T("Tag der Arbeit"), "calm");
        if (d == easter.AddDays(39)) return (L.T("Christi Himmelfahrt"), "calm");
        if (d == easter.AddDays(49) || d == easter.AddDays(50)) return (L.T("Frohe Pfingsten"), "easter");
        if (d.Month == 10 && d.Day == 3) return (L.T("Tag der Deutschen Einheit"), "calm");
        if (d.Month == 12 && d.Day is 24 or 25 or 26) return (L.T("Frohe Weihnachten"), "xmas");
        if (d.Month == 12 && d.Day == 31) return (L.T("Guten Rutsch"), "newyear");
        return (null, null);
    }

    private static DateTime Easter(int y)
    {
        int a = y % 19, b = y / 100, c = y % 100, d = b / 4, e = b % 4, f = (b + 8) / 25, g = (b - f + 1) / 3,
            h = (19 * a + b - d - g + 15) % 30, i = c / 4, k = c % 4, l = (32 + 2 * e + 2 * i - h - k) % 7, m = (a + 11 * h + 22 * l) / 451;
        int month = (h + l - 7 * m + 114) / 31, day = (h + l - 7 * m + 114) % 31 + 1;
        return new DateTime(y, month, day);
    }

    /// <summary>Confetti (or golden stars at Christmas) flies up out of the island and falls again.</summary>
    private void Confetti(string style)
    {
        if (S.Animations == AnimationLevel.Aus || style == "calm") return;
        Point c;
        try { c = Island.TranslatePoint(new Point(Island.ActualWidth / 2, Island.ActualHeight - 4), FlyLayer); } catch { return; }
        Color[] colors = style switch
        {
            "xmas" => new[] { Color.FromRgb(0xFF, 0xD6, 0x0A), Color.FromRgb(0xFF, 0xE6, 0x80), Colors.White },
            "easter" => new[] { Color.FromRgb(0xFF, 0xB3, 0xD1), Color.FromRgb(0xB5, 0xE8, 0x9C), Color.FromRgb(0xFF, 0xE6, 0x80), Color.FromRgb(0xA7, 0xD8, 0xFF) },
            _ => new[] { Color.FromRgb(0xFF, 0x45, 0x3A), Color.FromRgb(0xFF, 0xD6, 0x0A), Color.FromRgb(0x30, 0xD1, 0x58), Color.FromRgb(0x0A, 0x84, 0xFF), Color.FromRgb(0xBF, 0x5A, 0xF2) },
        };
        for (int i = 0; i < 26; i++)
        {
            var color = new SolidColorBrush(colors[_funRnd.Next(colors.Length)]);
            Shape s = style == "xmas" ? new Ellipse { Width = 4, Height = 4, Fill = color } : new Rectangle { Width = 5, Height = 3, Fill = color };
            var move = new TranslateTransform();
            var turn = new RotateTransform(_funRnd.Next(360));
            s.RenderTransform = new TransformGroup { Children = { turn, move } };
            Canvas.SetLeft(s, c.X + _funRnd.Next(-30, 30));
            Canvas.SetTop(s, c.Y);
            FlyLayer.Children.Add(s);
            double dx = _funRnd.Next(-110, 110), up = 40 + _funRnd.Next(50), down = 60 + _funRnd.Next(80);
            var d = TimeSpan.FromMilliseconds(1300 + _funRnd.Next(500));
            var y = new DoubleAnimationUsingKeyFrames { Duration = d };
            y.KeyFrames.Add(new EasingDoubleKeyFrame(-up, KeyTime.FromPercent(0.35), new CubicEase { EasingMode = EasingMode.EaseOut }));
            y.KeyFrames.Add(new EasingDoubleKeyFrame(down, KeyTime.FromPercent(1), new QuadraticEase { EasingMode = EasingMode.EaseIn }));
            var fade = new DoubleAnimationUsingKeyFrames { Duration = d };
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.7)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
            y.Completed += (_, _) => FlyLayer.Children.Remove(s);
            move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, dx, d) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            move.BeginAnimation(TranslateTransform.YProperty, y);
            turn.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(turn.Angle, turn.Angle + _funRnd.Next(-540, 540), d));
            s.BeginAnimation(OpacityProperty, fade);
        }
    }

    // =====================================================================
    //  51 · water
    // =====================================================================

    private const int DrinkEveryMinutes = 90;

    private void UpdateWaterGlass()
    {
        bool show = S.Water && IdleClock.IsVisible && _state == State.Collapsed;
        WaterGlass.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        double frac = Math.Clamp((double)H.Day.Today.Water / Math.Max(1, S.WaterGoal), 0, 1);
        double target = 12.5 * frac;
        if (Math.Abs(WaterFill.Height - target) < 0.1 || !show) { WaterFill.Height = target; return; }
        WaterFill.BeginAnimation(HeightProperty, new DoubleAnimation(WaterFill.Height, target, TimeSpan.FromMilliseconds(700))
            { EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
        WaterFill.Height = target;
    }

    private void DrinkCheck()
    {
        if (!S.Water) return;
        if (H.Usage.ActiveStreak < 10) _drinkActiveSince = DateTime.Now > _drinkActiveSince.AddMinutes(30) ? DateTime.Now : _drinkActiveSince;
        if (H.Day.Today.Water >= S.WaterGoal || DateTime.Now.Hour < 8 || DateTime.Now.Hour >= 22) return;
        if (DateTime.Now - _lastDrinkAsk < TimeSpan.FromMinutes(DrinkEveryMinutes) || H.Usage.ActiveStreak < 60 || !CanMoment()) return;
        _lastDrinkAsk = DateTime.Now;
        _hintHoldMs = 6000;
        ShowHint("drop", L.T("Zeit für ein Glas Wasser"), (Brush)FindResource("Teal"), key: "water",
                 actions: new List<(string, string, Brush, Action)> { ("check", L.T("Getrunken"), (Brush)FindResource("Teal"), Drank) });
    }

    private void Drank()
    {
        H.Day.Today.Water++;
        H.Day.Save();
        if (H.Day.Today.Water == S.WaterGoal)
            ShowHint("drop", L.T($"Tagesziel geschafft · {S.WaterGoal} Gläser"), (Brush)FindResource("Teal"), check: true, key: "water");
        else
            ShowHint("drop", L.T($"{H.Day.Today.Water} von {S.WaterGoal} Gläsern"), (Brush)FindResource("Teal"), check: true, key: "water");
        UpdateWaterGlass();
    }

    // =====================================================================
    //  52 · achievements
    // =====================================================================

    private sealed record Achievement(string Id, string Title, string Text, Func<object, bool> Reached);

    private static readonly Achievement[] AllAchievements =
    {
        new("early", "Früher Vogel", "Vor 7 Uhr am PC", w => DateTime.Now.Hour < 7 && DateTime.Now.Hour >= 4 && App.Hub.Usage.ActiveStreak > 0),
        new("owl", "Nachteule", "Nach 1 Uhr nachts noch am PC", w => DateTime.Now.Hour is >= 1 and < 4 && App.Hub.Usage.ActiveStreak > 0),
        new("focus1", "Fokussiert", "Die erste Fokus-Runde geschafft", w => App.Hub.Day.Days.Values.Any(d => d.FocusMinutes >= 1)),
        new("focus10", "Tief drin", "10 Stunden Fokus", w => App.Hub.Day.Days.Values.Sum(d => d.FocusMinutes) >= 600),
        new("water", "Gut gewässert", "Das Trink-Ziel eines Tages erreicht", w => App.Hub.Day.Days.Values.Any(d => d.Water >= App.Settings.WaterGoal)),
        new("eyes", "Weitblick", "5 Augenpausen an einem Tag", w => App.Hub.Day.Days.Values.Any(d => d.EyeBreaks >= 5)),
        new("claude10", "Teamwork", "Claude hat an einem Tag 10 Aufgaben fertig", w => App.Hub.Day.Days.Values.Any(d => d.ClaudeDone >= 10)),
        new("todo5", "Aufgeräumt", "5 Aufgaben an einem Tag erledigt", w => App.Hub.Day.Days.Values.Any(d => d.TodosDone >= 5)),
        new("replay", "Festgehalten", "Der erste Instant Replay", w => App.Hub.Day.Days.Values.Any(d => d.Replays >= 1)),
        new("streak", "Dranbleiber", "An 7 Tagen hintereinander Fokus", w => Enumerable.Range(0, 7).All(i => App.Hub.Day.Days.TryGetValue(DateTime.Today.AddDays(-i).ToString("yyyy-MM-dd"), out var d) && d.FocusMinutes >= 1)),
    };

    private void CheckAchievements()
    {
        foreach (var a in AllAchievements)
        {
            if (S.Achievements.ContainsKey(a.Id)) continue;
            bool reached;
            try { reached = a.Reached(this); } catch { continue; }
            if (!reached) continue;
            S.Achievements[a.Id] = DateTime.Now;
            App.SaveSettings();
            ShowAchievement(a);
            return; // one at a time
        }
    }

    private void ShowAchievement(Achievement a)
    {
        var gold = new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x0A));
        var p = new PeekInfo
        {
            Key = "achievement:" + a.Id, Icon = "trophy", GlyphBrush = gold, Ring = 1,
            Source = L.T("Erfolg"), SourceIcon = "trophy", SourceBrush = gold,
            Title = L.T(a.Title), Text = L.T(a.Text), TimeoutMs = 6000,
        };
        ShowPeek(p);
        After(300, () => { if (_peek == p) Confetti("xmas"); });
    }

    /// <summary>For the settings page: every achievement, reached or not.</summary>
    public static IEnumerable<(string Title, string Text, DateTime? When)> AchievementList(AppSettings s) =>
        AllAchievements.Select(a => (L.T(a.Title), L.T(a.Text), s.Achievements.TryGetValue(a.Id, out var t) ? t : (DateTime?)null));
}
