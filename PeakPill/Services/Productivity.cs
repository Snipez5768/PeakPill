using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows.Input;
using System.Windows.Threading;
using PeakPill.Core;

namespace PeakPill.Services;

// ---------------- Clock & world clock ----------------

public sealed class WorldClockEntry : ObservableObject
{
    private string _time, _offset;
    public TimeZoneInfo Zone { get; init; }
    public string City { get; init; }
    public string Time { get => _time; set => Set(ref _time, value); }
    public string OffsetText { get => _offset; set => Set(ref _offset, value); }
}

public sealed class ClockModel : ObservableObject
{
    private string _time, _date, _short;
    private readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromSeconds(1) };
    private static CultureInfo De => L.Culture; // the island's language (2026-11-17)

    public string TimeText { get => _time; private set => Set(ref _time, value); }
    public string DateText { get => _date; private set => Set(ref _date, value); }
    public string ShortTime { get => _short; private set => Set(ref _short, value); }
    private string _weekday, _day;
    public string WeekdayText { get => _weekday; private set => Set(ref _weekday, value); }
    public string DayText { get => _day; private set => Set(ref _day, value); }
    public ObservableCollection<WorldClockEntry> WorldClocks { get; } = new();

    public static readonly Dictionary<string, string> Cities = new()
    {
        ["Eastern Standard Time"] = "New York", ["Pacific Standard Time"] = "Los Angeles", ["Central Standard Time"] = "Chicago",
        ["Mountain Standard Time"] = "Denver", ["Hawaiian Standard Time"] = "Honolulu", ["GMT Standard Time"] = "London",
        ["W. Europe Standard Time"] = "Berlin", ["Romance Standard Time"] = "Paris", ["Central Europe Standard Time"] = "Prag",
        ["Turkey Standard Time"] = "Istanbul", ["Russian Standard Time"] = "Moskau", ["Arabian Standard Time"] = "Dubai",
        ["India Standard Time"] = "Neu-Delhi", ["China Standard Time"] = "Peking", ["Singapore Standard Time"] = "Singapur",
        ["Tokyo Standard Time"] = "Tokio", ["Korea Standard Time"] = "Seoul", ["AUS Eastern Standard Time"] = "Sydney",
        ["New Zealand Standard Time"] = "Auckland", ["E. South America Standard Time"] = "São Paulo",
        ["South Africa Standard Time"] = "Johannesburg", ["Egypt Standard Time"] = "Kairo", ["UTC"] = "UTC",
        ["Argentina Standard Time"] = "Buenos Aires", ["SE Asia Standard Time"] = "Bangkok", ["Israel Standard Time"] = "Jerusalem",
    };

    public static string CityOf(TimeZoneInfo tz)
    {
        if (Cities.TryGetValue(tz.Id, out var c)) return c;
        var dn = tz.DisplayName;
        int i = dn.IndexOf(')');
        return i > 0 && i + 2 < dn.Length ? dn[(i + 2)..] : tz.Id;
    }

    public void Start()
    {
        RebuildWorld();
        _t.Tick += (_, _) => Tick();
        _t.Start();
        Tick();
    }

    public void RebuildWorld()
    {
        WorldClocks.Clear();
        foreach (var id in App.Settings.WorldClocks)
        {
            try
            {
                var tz = TimeZoneInfo.FindSystemTimeZoneById(id);
                WorldClocks.Add(new WorldClockEntry { Zone = tz, City = CityOf(tz) });
            }
            catch { }
        }
        Tick();
    }

    private void Tick()
    {
        var now = DateTime.Now;
        TimeText = now.ToString(App.Settings.ShowSeconds ? "HH:mm:ss" : "HH:mm");
        ShortTime = now.ToString(App.Settings.ShowSeconds ? "HH:mm:ss" : "HH:mm");
        DateText = now.ToString(L.English ? "dddd, MMMM d" : "dddd, d. MMMM", De);
        WeekdayText = now.ToString("dddd", De).ToUpper(De);
        DayText = now.ToString(L.English ? "MMMM d" : "d. MMMM", De);
        var utc = DateTime.UtcNow;
        foreach (var w in WorldClocks)
        {
            var t = TimeZoneInfo.ConvertTimeFromUtc(utc, w.Zone);
            w.Time = t.ToString("HH:mm");
            var diff = t - now;
            int h = (int)Math.Round(diff.TotalHours);
            string day = t.Date > now.Date ? L.T("Morgen") : t.Date < now.Date ? L.T("Gestern") : L.T("Heute");
            w.OffsetText = h == 0 ? L.T($"{day} · gleiche Zeit") : $"{day} · {(h > 0 ? "+" : "−")}{Math.Abs(h)} Std";
        }
    }
}

// ---------------- Weather (Open-Meteo, no API key, only when a city is set) ----------------

/// <summary>One hour of the forecast.</summary>
public sealed record WeatherHour(string Time, string Icon, string Temp);

public sealed class WeatherService : ObservableObject
{
    // rain and snow for the island (48, 2026-11-18): the WMO code now and for the next hours
    public int Code { get; private set; }
    public List<(DateTime At, int Code)> HourCodes { get; private set; } = new();
    /// <summary>0 dry · 1 rain (drizzle, showers, thunder) · 2 snow.</summary>
    public static int Precip(int code) => code is >= 71 and <= 77 or 85 or 86 ? 2 : code is >= 51 and <= 67 or >= 80 and <= 82 or >= 95 ? 1 : 0;

#if DEBUG
    /// <summary>Test only: pretend the weather service just fetched this (raises the same change as a real refresh).</summary>
    public void Fake(int code, List<(DateTime At, int Code)> hours)
    {
        Code = code;
        HourCodes = hours;
        Raise(nameof(HourCodes));
    }
#endif
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromMinutes(20) };
    private bool _has;
    private string _temp, _desc, _icon, _detail, _place, _status = L.T("Lege in den Einstellungen eine Stadt fest.");

    public bool HasData { get => _has; private set => Set(ref _has, value); }
    public string TempText { get => _temp; private set => Set(ref _temp, value); }
    public string Description { get => _desc; private set => Set(ref _desc, value); }
    public string Icon { get => _icon; private set => Set(ref _icon, value); }
    public string Detail { get => _detail; private set => Set(ref _detail, value); }
    public string Place { get => _place; private set => Set(ref _place, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    private string _city, _hl;
    public string City { get => _city; private set => Set(ref _city, value); }
    public string HighLow { get => _hl; private set => Set(ref _hl, value); }
    private List<WeatherHour> _hours = new();
    /// <summary>Now and the next 4 hours (symbol and temperature).</summary>
    public List<WeatherHour> Hours { get => _hours; private set => Set(ref _hours, value); }

    public void Start()
    {
        _t.Tick += async (_, _) => await RefreshAsync();
        _t.Start();
        _ = RefreshAsync();
    }

    public async Task<string> SetCityAsync(string city)
    {
        var s = App.Settings;
        if (string.IsNullOrWhiteSpace(city))
        {
            s.WeatherCity = ""; s.WeatherPlace = ""; s.WeatherLat = s.WeatherLon = 0;
            App.SaveSettings();
            await RefreshAsync();
            return null;
        }
        var url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(city.Trim())}&count=1&language=de&format=json";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
        if (!doc.RootElement.TryGetProperty("results", out var res) || res.GetArrayLength() == 0) return null;
        var r = res[0];
        s.WeatherCity = city.Trim();
        s.WeatherLat = r.GetProperty("latitude").GetDouble();
        s.WeatherLon = r.GetProperty("longitude").GetDouble();
        var country = r.TryGetProperty("country", out var c) ? c.GetString() : "";
        s.WeatherPlace = r.GetProperty("name").GetString() + (string.IsNullOrEmpty(country) ? "" : ", " + country);
        App.SaveSettings();
        await RefreshAsync();
        return s.WeatherPlace;
    }

    public async Task RefreshAsync()
    {
        var s = App.Settings;
        if (string.IsNullOrWhiteSpace(s.WeatherCity) || (s.WeatherLat == 0 && s.WeatherLon == 0))
        {
            HasData = false;
            Status = L.T("Lege in den Einstellungen eine Stadt fest.");
            return;
        }
        try
        {
            var ci = CultureInfo.InvariantCulture;
            var url = $"https://api.open-meteo.com/v1/forecast?latitude={s.WeatherLat.ToString(ci)}&longitude={s.WeatherLon.ToString(ci)}" +
                      "&current=temperature_2m,apparent_temperature,weather_code,wind_speed_10m,is_day&daily=temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=2" +
                      "&hourly=temperature_2m,weather_code,is_day&forecast_hours=6";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            var cur = doc.RootElement.GetProperty("current");
            var daily = doc.RootElement.GetProperty("daily");
            double temp = cur.GetProperty("temperature_2m").GetDouble();
            double feels = cur.GetProperty("apparent_temperature").GetDouble();
            int code = cur.GetProperty("weather_code").GetInt32();
            double wind = cur.GetProperty("wind_speed_10m").GetDouble();
            bool day = cur.TryGetProperty("is_day", out var d) && d.GetInt32() == 1;
            double max = daily.GetProperty("temperature_2m_max")[0].GetDouble();
            double min = daily.GetProperty("temperature_2m_min")[0].GetDouble();
            var (desc, icon) = Describe(code, day);
            TempText = $"{temp:0}°";
            Description = desc;
            Icon = icon;
            Code = code;
            Detail = L.T($"↑ {max:0}°  ↓ {min:0}°  ·  gefühlt {feels:0}°  ·  Wind {wind:0} km/h");
            Place = s.WeatherPlace;
            City = (s.WeatherPlace ?? "").Split(',')[0];
            HighLow = L.T($"H: {max:0}°  T: {min:0}°");
            // the next hours for the "Heute" page (2026-11-16)
            try
            {
                var hourly = doc.RootElement.GetProperty("hourly");
                var times = hourly.GetProperty("time");
                var hours = new List<WeatherHour>();
                var codes = new List<(DateTime At, int Code)>();
                for (int i = 0; i < times.GetArrayLength() && hours.Count < 5; i++)
                {
                    var at = DateTime.Parse(times[i].GetString(), CultureInfo.InvariantCulture);
                    bool hd = hourly.GetProperty("is_day")[i].GetInt32() == 1;
                    var (_, hi) = Describe(hourly.GetProperty("weather_code")[i].GetInt32(), hd);
                    codes.Add((at, hourly.GetProperty("weather_code")[i].GetInt32()));
                    hours.Add(new WeatherHour(i == 0 ? L.T("Jetzt") : at.ToString("HH"), hi, $"{hourly.GetProperty("temperature_2m")[i].GetDouble():0}°"));
                }
                Hours = hours;
                HourCodes = codes;
                Raise(nameof(HourCodes)); // the island's rain moment (48a) listens for this – without it, it never came
                Raise(nameof(HourCodes));
            }
            catch { }
            HasData = true;
        }
        catch (Exception ex)
        {
            Status = L.T("Wetter gerade nicht erreichbar.");
            HasData = false;
            App.Log(ex);
        }
    }

    private static (string, string) Describe(int c, bool day) => c switch
    {
        0 => (L.T("Klar"), day ? "☀" : "☾"),
        1 or 2 => (L.T("Leicht bewölkt"), "⛅"),
        3 => (L.T("Bewölkt"), "☁"),
        45 or 48 => (L.T("Nebel"), "≋"),
        >= 51 and <= 57 => (L.T("Nieselregen"), "☂"),
        >= 61 and <= 67 => (L.T("Regen"), "☂"),
        >= 71 and <= 77 => (L.T("Schnee"), "❄"),
        >= 80 and <= 82 => (L.T("Regenschauer"), "☂"),
        85 or 86 => (L.T("Schneeschauer"), "❄"),
        >= 95 => (L.T("Gewitter"), "⚡"),
        _ => ("—", "☁")
    };
}

// ---------------- Reminders ----------------

public enum RepeatKind { Einmal, Täglich, Werktags, Wöchentlich, Monatlich }

public sealed class Reminder : ObservableObject
{
    private DateTime _due;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Text { get; set; }
    public RepeatKind Repeat { get; set; }
    public DateTime Due { get => _due; set { Set(ref _due, value); Raise(nameof(DueText)); Raise(nameof(IsOverdue)); } }
    [JsonIgnore] public bool Fired { get; set; }
    [JsonIgnore] public bool IsRepeating => Repeat != RepeatKind.Einmal;
    [JsonIgnore] public bool IsOverdue => Due <= DateTime.Now;

    [JsonIgnore]
    public string DueText
    {
        get
        {
            var de = L.Culture;
            string when = Due.Date == DateTime.Today ? L.T($"Heute {Due:HH:mm}") :
                          Due.Date == DateTime.Today.AddDays(1) ? L.T($"Morgen {Due:HH:mm}") :
                          Due.ToString(L.English ? "ddd, MMM d HH:mm" : "ddd, dd.MM. HH:mm", de);
            return IsRepeating ? L.T($"{when} · {Repeat.ToString().ToLower(de)}") : when;
        }
    }

    /// <summary>Due within the next hour – shown in orange.</summary>
    [JsonIgnore] public bool IsSoon => !IsOverdue && Due - DateTime.Now < TimeSpan.FromHours(1);

    public void RefreshText() { Raise(nameof(DueText)); Raise(nameof(IsOverdue)); Raise(nameof(IsSoon)); }
}

public sealed class ReminderService
{
    public ObservableCollection<Reminder> Items { get; } = new();
    public event Action<Reminder> Due;
    private readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromSeconds(5) };
    private int _tick;

    public void Load()
    {
        foreach (var r in Storage.Load<List<Reminder>>("reminders.json").OrderBy(r => r.Due)) Items.Add(r);
    }

    public void Start()
    {
        _t.Tick += (_, _) =>
        {
            var now = DateTime.Now;
            foreach (var r in Items.Where(r => !r.Fired && r.Due <= now).ToList())
            {
                r.Fired = true;
                Due?.Invoke(r);
            }
            if (_tick++ % 12 == 0) foreach (var r in Items) r.RefreshText();
        };
        _t.Start();
    }

    public Reminder Add(string text, DateTime due, RepeatKind repeat)
    {
        var r = new Reminder { Text = text, Due = due, Repeat = repeat };
        Insert(r);
        Save();
        return r;
    }

    private void Insert(Reminder r)
    {
        Items.Remove(r);
        int i = 0;
        while (i < Items.Count && Items[i].Due <= r.Due) i++;
        Items.Insert(i, r);
    }

    public void Complete(Reminder r)
    {
        if (r.Repeat == RepeatKind.Einmal) { Items.Remove(r); }
        else
        {
            var next = r.Due;
            do next = Next(next, r.Repeat); while (next <= DateTime.Now);
            r.Due = next;
            r.Fired = false;
            Insert(r);
        }
        Save();
    }

    public void Snooze(Reminder r, int minutes)
    {
        r.Due = DateTime.Now.AddMinutes(minutes);
        r.Fired = false;
        Insert(r);
        Save();
    }

    public void Delete(Reminder r) { Items.Remove(r); Save(); }

    public void Save() => Storage.Save("reminders.json", Items.ToList());

    private static DateTime Next(DateTime d, RepeatKind k) => k switch
    {
        RepeatKind.Täglich => d.AddDays(1),
        RepeatKind.Werktags => d.DayOfWeek switch { DayOfWeek.Friday => d.AddDays(3), DayOfWeek.Saturday => d.AddDays(2), _ => d.AddDays(1) },
        RepeatKind.Wöchentlich => d.AddDays(7),
        RepeatKind.Monatlich => d.AddMonths(1),
        _ => d
    };

    private static readonly Regex Rel = new(@"\b(?:in|nach)\s+(\d+(?:[.,]\d+)?)\s*(sekunden|sek|minuten|minute|min|m|stunden|stunde|std|h|tagen|tage|tag)\b", RegexOptions.IgnoreCase);
    private static readonly Regex AtTime = new(@"\b(?:(heute|morgen|übermorgen)\s+)?(?:um\s+)?(\d{1,2})(?:[:.](\d{2}))?\s*uhr\b|\b(?:(heute|morgen|übermorgen)\s+)?um\s+(\d{1,2})(?:[:.](\d{2}))?\b|\b(?:(heute|morgen|übermorgen)\s+)?(\d{1,2})[:.](\d{2})\b", RegexOptions.IgnoreCase);
    private static readonly Regex DayOnly = new(@"\b(morgen|übermorgen)\b", RegexOptions.IgnoreCase);

    /// <summary>Understands e.g. "Wäsche in 20 min", "Meeting um 14:30", "morgen 9 Uhr Arzt anrufen".</summary>
    public static bool TryParseQuick(string input, out string text, out DateTime due)
    {
        text = input?.Trim() ?? "";
        due = default;
        if (text.Length == 0) return false;
        var now = DateTime.Now;

        var m = Rel.Match(text);
        if (m.Success)
        {
            double n = double.Parse(m.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            var unit = m.Groups[2].Value.ToLowerInvariant();
            due = unit.StartsWith("sek") ? now.AddSeconds(n) :
                  unit is "h" or "std" or "stunde" or "stunden" ? now.AddHours(n) :
                  unit.StartsWith("tag") ? now.AddDays(n) : now.AddMinutes(n);
            text = Clean(text.Remove(m.Index, m.Length));
            return true;
        }

        m = AtTime.Match(text);
        if (m.Success)
        {
            string day = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[4].Success ? m.Groups[4].Value : m.Groups[7].Value;
            string hh = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[5].Success ? m.Groups[5].Value : m.Groups[8].Value;
            string mm = m.Groups[3].Success ? m.Groups[3].Value : m.Groups[6].Success ? m.Groups[6].Value : m.Groups[9].Value;
            int h = int.Parse(hh), min = string.IsNullOrEmpty(mm) ? 0 : int.Parse(mm);
            if (h > 23 || min > 59) return false;
            var date = now.Date;
            var dl = day.ToLowerInvariant();
            if (dl == "morgen") date = date.AddDays(1);
            else if (dl == "übermorgen") date = date.AddDays(2);
            due = date.AddHours(h).AddMinutes(min);
            if (string.IsNullOrEmpty(dl) && due <= now) due = due.AddDays(1);
            text = Clean(text.Remove(m.Index, m.Length));
            return true;
        }

        m = DayOnly.Match(text);
        if (m.Success)
        {
            due = now.Date.AddDays(m.Value.ToLowerInvariant() == "morgen" ? 1 : 2).AddHours(9);
            text = Clean(text.Remove(m.Index, m.Length));
            return true;
        }
        return false;
    }

    private static readonly Regex RepeatWords = new(
        @"\b(täglich|jeden\s+tag|werktags|wöchentlich|jede\s+woche|jeden\s+(?:montag|dienstag|mittwoch|donnerstag|freitag|samstag|sonntag)|monatlich|jeden\s+monat)\b",
        RegexOptions.IgnoreCase);

    /// <summary>"… täglich", "jeden Montag", "werktags" … – the repeat is read from the sentence (and removed from the text).</summary>
    public static RepeatKind TakeRepeat(ref string text, out DayOfWeek? weekday)
    {
        weekday = null;
        var m = RepeatWords.Match(text ?? "");
        if (!m.Success) return RepeatKind.Einmal;
        var w = m.Value.ToLowerInvariant();
        string[] days = { "sonntag", "montag", "dienstag", "mittwoch", "donnerstag", "freitag", "samstag" };
        for (int i = 0; i < 7; i++) if (w.EndsWith(days[i])) weekday = (DayOfWeek)i;
        text = Regex.Replace(text.Remove(m.Index, m.Length), @"\s{2,}", " ").Trim();
        return w.StartsWith("täglich") || w.Contains("tag") && !w.Contains("tags") && !w.Contains("montag") && !w.Contains("dienstag") && !w.Contains("mittwoch")
               && !w.Contains("donnerstag") && !w.Contains("freitag") && !w.Contains("samstag") && !w.Contains("sonntag") ? RepeatKind.Täglich
             : w.StartsWith("werktags") ? RepeatKind.Werktags
             : w.Contains("monat") ? RepeatKind.Monatlich
             : RepeatKind.Wöchentlich;
    }

    private static string Clean(string s)
    {
        s = Regex.Replace(s, @"\s{2,}", " ").Trim(' ', ',', '.', '-', ':');
        return s.Length == 0 ? L.T("Erinnerung") : char.ToUpper(s[0]) + s[1..];
    }
}

// ---------------- To-do ----------------

public sealed class TodoItem : ObservableObject
{
    private bool _done;
    public string Text { get; set; }
    public bool Done { get => _done; set => Set(ref _done, value); }
}

public sealed class TodoService : ObservableObject
{
    private string _new = "";
    public ObservableCollection<TodoItem> Items { get; } = new();
    public string NewText { get => _new; set => Set(ref _new, value); }
    public ICommand AddCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ClearDoneCommand { get; }

    public TodoService()
    {
        AddCommand = new RelayCommand(() =>
        {
            if (string.IsNullOrWhiteSpace(NewText)) return;
            Add(new TodoItem { Text = NewText.Trim() });
            NewText = "";
            Save();
        });
        DeleteCommand = new RelayCommand(p => { if (p is TodoItem t) { Items.Remove(t); Save(); } });
        ClearDoneCommand = new RelayCommand(() => { foreach (var t in Items.Where(i => i.Done).ToList()) Items.Remove(t); Save(); });
    }

    public void Load()
    {
        foreach (var t in Storage.Load<List<TodoItem>>("todos.json")) Add(t);
    }

    private void Add(TodoItem t)
    {
        t.PropertyChanged += (_, _) => Save();
        Items.Add(t);
    }

    public void Save() => Storage.Save("todos.json", Items.ToList());
}

// ---------------- Focus timer ----------------

public sealed class FocusTimerService : ObservableObject
{
    private TimeSpan _duration = TimeSpan.FromMinutes(25), _remaining = TimeSpan.FromMinutes(25);
    private DateTime _endAt;
    private bool _running;
    private readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public event Action Completed;

    public bool Running { get => _running; private set { Set(ref _running, value); Raise(nameof(StartPauseText)); Raise(nameof(Active)); } }
    public bool Active => _running || _remaining < _duration;
    public string RemainingText => _remaining.TotalHours >= 1 ? _remaining.ToString(@"h\:mm\:ss") : _remaining.ToString(@"mm\:ss");
    public double ProgressPercent => _duration.TotalSeconds <= 0 ? 0 : 100 * (1 - _remaining.TotalSeconds / _duration.TotalSeconds);
    public double RemainingPercent => 100 - ProgressPercent;
    public string StartPauseText => _running ? L.T("Pause") : _remaining < _duration ? L.T("Weiter") : "Start";
    public double DurationMinutes => _duration.TotalMinutes;
    public int DurationSeconds => (int)_duration.TotalSeconds;
    public string ModeText
    {
        get
        {
            var parts = new List<string>();
            if (_duration.TotalHours >= 1) parts.Add(L.T($"{(int)_duration.TotalHours} Std"));
            if (_duration.Minutes > 0) parts.Add(L.T($"{_duration.Minutes} Min"));
            if (_duration.Seconds > 0) parts.Add(L.T($"{_duration.Seconds} Sek"));
            return L.T("Timer · ") + string.Join(" ", parts);
        }
    }

    public ICommand StartPauseCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand PresetCommand { get; }

    public FocusTimerService()
    {
        _t.Tick += (_, _) => Tick();
        StartPauseCommand = new RelayCommand(Toggle);
        ResetCommand = new RelayCommand(Reset);
        PresetCommand = new RelayCommand(p => { if (int.TryParse(p?.ToString(), out var m)) { SetDuration(m); Start(); } });
    }

    public void Toggle() { if (_running) Pause(); else Start(); }

    public void Start()
    {
        if (_remaining <= TimeSpan.Zero) _remaining = _duration;
        _endAt = DateTime.Now + _remaining;
        Running = true;
        _t.Start();
        RaiseAll();
    }

    public void Pause()
    {
        _t.Stop();
        _remaining = _endAt - DateTime.Now;
        if (_remaining < TimeSpan.Zero) _remaining = TimeSpan.Zero;
        Running = false;
        RaiseAll();
    }

    public void Reset()
    {
        _t.Stop();
        _remaining = _duration;
        Running = false;
        RaiseAll();
    }

    public void SetDuration(int minutes) => SetDurationSeconds(minutes * 60);

    /// <summary>Hours, minutes and seconds from the wheels (1 s … 23:59:59).</summary>
    public void SetDurationSeconds(int seconds)
    {
        _t.Stop();
        _duration = TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 23 * 3600 + 59 * 60 + 59));
        _remaining = _duration;
        Running = false;
        RaiseAll();
    }

    private void Tick()
    {
        _remaining = _endAt - DateTime.Now;
        if (_remaining <= TimeSpan.Zero)
        {
            _remaining = TimeSpan.Zero;
            _t.Stop();
            Running = false;
            RaiseAll();
            _remaining = _duration;
            Completed?.Invoke();
            RaiseAll();
            return;
        }
        RaiseAll();
    }

    private void RaiseAll()
    {
        Raise(nameof(RemainingText)); Raise(nameof(ProgressPercent)); Raise(nameof(RemainingPercent)); Raise(nameof(StartPauseText));
        Raise(nameof(ModeText)); Raise(nameof(Active));
    }
}
