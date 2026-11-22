using System.Text.Json;

namespace LiteBrowser;

sealed class SnoozedTab
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTime Until { get; set; }
}

/// <summary>«Отложить вкладку»: вкладка закрывается и сама возвращается в нужное время (через час, вечером, завтра утром).</summary>
sealed partial class MainForm
{
    static string SnoozeFile => Path.Combine(AppEnv.DataDir, "snoozed.json");
    static List<SnoozedTab>? snoozed;
    System.Windows.Forms.Timer? snoozeTimer;

    static List<SnoozedTab> Snoozed
    {
        get
        {
            if (snoozed != null) return snoozed;
            try { snoozed = File.Exists(SnoozeFile) ? JsonSerializer.Deserialize<List<SnoozedTab>>(File.ReadAllText(SnoozeFile), AppSettings.Json) : null; }
            catch { }
            return snoozed ??= new();
        }
    }

    static void SaveSnoozed()
    {
        try { Directory.CreateDirectory(AppEnv.DataDir); File.WriteAllText(SnoozeFile, JsonSerializer.Serialize(Snoozed, AppSettings.Json)); } catch { }
    }

    static DateTime SnoozeTime(string when)
    {
        var now = DateTime.Now;
        return when switch
        {
            "hour" => now.AddHours(1),
            "evening" => now.Hour < 18 ? now.Date.AddHours(18) : now.Date.AddDays(1).AddHours(18),
            "tomorrow" => now.Date.AddDays(1).AddHours(9),
            "weekend" => now.Date.AddDays(DaysToSaturday(now)).AddHours(10),
            "week" => now.Date.AddDays(7).AddHours(9),
            _ => now.AddHours(1),
        };
    }

    static int DaysToSaturday(DateTime now)
    {
        int d = ((int)DayOfWeek.Saturday - (int)now.DayOfWeek + 7) % 7;
        return d == 0 ? 7 : d;
    }

    void SnoozeTab(BrowserTab tab, string when)
    {
        var url = TabUrl(tab);
        if (!url.StartsWith("http")) return;
        Snoozed.Add(new SnoozedTab { Url = url, Title = tab.Title, Until = SnoozeTime(when) });
        SaveSnoozed();
        CloseTab(tab);
        EnsureSnoozeTimer();
    }

    ToolStripMenuItem SnoozeMenu(BrowserTab tab)
    {
        var sub = SubMenu("Отложить вкладку", "\uE916");
        foreach (var (key, title) in new[] { ("hour", "На 1 час"), ("evening", "До вечера (18:00)"), ("tomorrow", "До завтра (9:00)"), ("weekend", "До выходных"), ("week", "На неделю") })
        {
            var k = key;
            sub.DropDownItems.Add(Theme.Item(title, () => SnoozeTab(tab, k)));
        }
        return sub;
    }

    /// <summary>Список отложенных в меню «Инструменты»: открыть сейчас.</summary>
    ToolStripMenuItem SnoozedListMenu()
    {
        var sub = SubMenu(L.T("Отложенные вкладки") + (Snoozed.Count > 0 ? $" ({Snoozed.Count})" : ""), "\uE916");
        if (Snoozed.Count == 0) sub.DropDownItems.Add(new ToolStripMenuItem(L.T("Нет отложенных вкладок")) { Enabled = false });
        foreach (var s in Snoozed.OrderBy(s => s.Until).Take(15).ToList())
        {
            var it = s;
            var text = (s.Title.Length > 0 ? s.Title : s.Url);
            if (text.Length > 50) text = text[..47] + "…";
            var item = new ToolStripMenuItem($"{text}  ·  {s.Until:dd.MM HH:mm}") { ForeColor = Theme.Text, Padding = new Padding(4, 4, 6, 4), ToolTipText = s.Url };
            item.Click += (_, _) => { Snoozed.Remove(it); SaveSnoozed(); _ = AddTab(it.Url); };
            sub.DropDownItems.Add(item);
        }
        return sub;
    }

    void EnsureSnoozeTimer()
    {
        if (Incognito || !BrowserApp.Instance.IsMainWindow(this)) return;
        snoozeTimer ??= new System.Windows.Forms.Timer { Interval = 30_000 };
        snoozeTimer.Tick -= SnoozeTick;
        snoozeTimer.Tick += SnoozeTick;
        snoozeTimer.Start();
        SnoozeTick(null, EventArgs.Empty);
    }

    /// <summary>Время пришло — возвращаем вкладки (в фоне) и сообщаем.</summary>
    void SnoozeTick(object? s, EventArgs e)
    {
        var due = Snoozed.Where(x => x.Until <= DateTime.Now).ToList();
        if (due.Count == 0) return;
        foreach (var d in due) { Snoozed.Remove(d); CreateTab(d.Url, select: false, d.Title); }
        SaveSnoozed();
        strip.Invalidate();
        infoBar.ShowBar("snooze", "\uE916", due.Count == 1 ? L.T("Вернулась отложенная вкладка: ") + due[0].Title : L.T("Вернулись отложенные вкладки: ") + due.Count,
            ("OK", () => infoBar.HideBar(), true));
    }
}
