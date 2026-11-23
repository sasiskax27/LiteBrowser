using System.Text.Json;

namespace LiteBrowser;

sealed class Bookmark
{
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string? Icon { get; set; } // PNG 16×16 в base64
}

sealed class HistoryEntry
{
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTime Time { get; set; }
}

sealed class Highlight
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";     // выделенный текст (пусто — заметка ко всей странице)
    public string Note { get; set; } = "";
    public DateTime Time { get; set; } = DateTime.Now;
}

sealed class SessionTab
{
    public string Url { get; set; } = "";
    public bool Pinned { get; set; }
    public string? Group { get; set; }
    public string? GroupColor { get; set; }
    public bool GroupCollapsed { get; set; }
    public string? Container { get; set; }
}

sealed class Session
{
    public List<string> Tabs { get; set; } = new();
    public List<SessionTab>? Info { get; set; }
    public int Active { get; set; }
    /// <summary>Положение окна: x, y, ширина, высота, развёрнуто (1/0).</summary>
    public int[]? Window { get; set; }
    /// <summary>Окно выделено под сайт (правило окон).</summary>
    public string? Rule { get; set; }
}

/// <summary>Закладки, история и сессия. Пишется на диск с задержкой, чтобы не тормозить.</summary>
static class Store
{
    const int MaxHistory = 5000;
    static string dir = "";
    static bool bookmarksDirty, historyDirty;

    public static List<Bookmark> Bookmarks { get; private set; } = new();
    public static List<HistoryEntry> History { get; private set; } = new();
    public static event Action? BookmarksChanged;

    static string F(string name) => Path.Combine(dir, name);

    public static void Load(string dataDir)
    {
        dir = dataDir;
        Bookmarks = Read<List<Bookmark>>("bookmarks.json") ?? new();
        History = Read<List<HistoryEntry>>("history.json") ?? new();
    }

    static T? Read<T>(string name)
    {
        try { return File.Exists(F(name)) ? JsonSerializer.Deserialize<T>(File.ReadAllText(F(name)), AppSettings.Json) : default; }
        catch { return default; }
    }

    static void Write(string name, object value)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var tmp = F(name + ".tmp");
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, AppSettings.Json));
            File.Move(tmp, F(name), true);
        }
        catch { }
    }

    // ---- Закладки ----

    public static Bookmark? FindBookmark(string url) => Bookmarks.FirstOrDefault(b => b.Url == url);

    public static void AddBookmark(string title, string url, string? icon)
    {
        if (FindBookmark(url) != null) return;
        Bookmarks.Add(new Bookmark { Title = title, Url = url, Icon = icon });
        bookmarksDirty = true;
        BookmarksChanged?.Invoke();
    }

    public static void RemoveBookmark(Bookmark b)
    {
        if (!Bookmarks.Remove(b)) return;
        bookmarksDirty = true;
        BookmarksChanged?.Invoke();
    }

    public static void RenameBookmark(Bookmark b, string title)
    {
        b.Title = title;
        bookmarksDirty = true;
        BookmarksChanged?.Invoke();
    }

    /// <summary>Добавляет закладки без дублей. Возвращает, сколько добавлено.</summary>
    public static int ImportBookmarks(IEnumerable<(string title, string url)> items)
    {
        var known = new HashSet<string>(Bookmarks.Select(b => b.Url));
        int added = 0;
        foreach (var (title, url) in items)
        {
            if (!known.Add(url)) continue;
            Bookmarks.Add(new Bookmark { Title = title.Length > 200 ? title[..200] : title, Url = url });
            added++;
        }
        if (added > 0) { bookmarksDirty = true; BookmarksChanged?.Invoke(); Flush(); }
        return added;
    }

    /// <summary>Сливает историю с имеющейся, оставляя самые свежие записи.</summary>
    public static int ImportHistory(IEnumerable<HistoryEntry> items)
    {
        var known = new HashSet<string>(History.Select(h => h.Url + "|" + h.Time.Ticks / TimeSpan.TicksPerMinute));
        var fresh = items.Where(h => known.Add(h.Url + "|" + h.Time.Ticks / TimeSpan.TicksPerMinute)).ToList();
        if (fresh.Count == 0) return 0;
        History.AddRange(fresh);
        History.Sort((a, b) => a.Time.CompareTo(b.Time));
        if (History.Count > MaxHistory) History.RemoveRange(0, History.Count - MaxHistory);
        historyDirty = true;
        Flush();
        return fresh.Count;
    }

    // ---- История ----

    public static HistoryEntry AddHistory(string url, string title)
    {
        var last = History.Count > 0 ? History[^1] : null;
        if (last != null && last.Url == url && (DateTime.Now - last.Time).TotalMinutes < 1) return last;
        var e = new HistoryEntry { Url = url, Title = title, Time = DateTime.Now };
        History.Add(e);
        if (History.Count > MaxHistory) History.RemoveRange(0, History.Count - MaxHistory);
        historyDirty = true;
        return e;
    }

    public static void TouchHistory() => historyDirty = true;

    public static IEnumerable<HistoryEntry> SearchHistory(string q, int limit)
    {
        q = q.Trim();
        IEnumerable<HistoryEntry> items = Enumerable.Reverse(History);
        if (q.Length > 0)
            items = items.Where(h => h.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || h.Url.Contains(q, StringComparison.OrdinalIgnoreCase));
        return items.Take(limit);
    }

    public static void DeleteHistory(string url, DateTime time)
    {
        PageIndex.Remove(url);
        History.RemoveAll(h => h.Url == url && Math.Abs((h.Time - time).TotalSeconds) < 1);
        historyDirty = true;
    }

    public static void ClearHistory()
    {
        PageIndex.Clear();
        History.Clear();
        historyDirty = true;
        Flush();
    }

    // ---- Сессия ----

    public static void SaveSession(Session s) => Write("session.json", s);
    public static Session? LoadSession() => Read<Session>("session.json");

    /// <summary>Все окна сразу (у каждого — свои вкладки). Старый файл session.json — запасной вариант.</summary>
    public static void SaveWindows(List<Session> windows)
    {
        Write("windows.json", windows);
        if (windows.Count > 0) Write("session.json", windows[0]);
    }

    public static List<Session> LoadWindows()
    {
        var list = Read<List<Session>>("windows.json")?.Where(s => s.Tabs.Count > 0).ToList();
        if (list is { Count: > 0 }) return list;
        return LoadSession() is { Tabs.Count: > 0 } s ? new() { s } : new();
    }

    // ---- Выделения и заметки на страницах ----
    static List<Highlight>? highlights;
    public static List<Highlight> Highlights => highlights ??= Read<List<Highlight>>("highlights.json") ?? new();
    public static event Action? HighlightsChanged;

    public static string PageKey(string url)
    {
        int i = url.IndexOf('#');
        return i >= 0 ? url[..i] : url;
    }

    public static void AddHighlight(Highlight h)
    {
        Highlights.Add(h);
        if (Highlights.Count > 5000) Highlights.RemoveAt(0);
        Write("highlights.json", Highlights);
        HighlightsChanged?.Invoke();
    }

    /// <summary>Синхронизация: добавить заметки с другого компьютера, которых здесь нет.</summary>
    public static int MergeHighlights(List<Highlight> remote)
    {
        var ids = Highlights.Select(h => h.Id).ToHashSet();
        var fresh = remote.Where(h => !string.IsNullOrEmpty(h.Id) && !ids.Contains(h.Id)).ToList();
        if (fresh.Count == 0) return 0;
        Highlights.AddRange(fresh);
        Highlights.Sort((a, b) => a.Time.CompareTo(b.Time));
        while (Highlights.Count > 5000) Highlights.RemoveAt(0);
        Write("highlights.json", Highlights);
        HighlightsChanged?.Invoke();
        return fresh.Count;
    }

    public static void RemoveHighlight(string id)
    {
        if (Highlights.RemoveAll(h => h.Id == id) == 0) return;
        Write("highlights.json", Highlights);
        HighlightsChanged?.Invoke();
    }

    // ---- Восстановление после сбоя ----
    static string Marker => F("running.lock");
    /// <summary>Отмечаем запуск; если отметка осталась с прошлого раза — браузер закрылся неожиданно.</summary>
    public static bool MarkRunning()
    {
        bool crashed = File.Exists(Marker);
        try { Directory.CreateDirectory(dir); File.WriteAllText(Marker, Environment.ProcessId.ToString()); } catch { }
        return crashed;
    }
    public static void MarkClosed() { try { File.Delete(Marker); } catch { } }
    public static void SaveCrashSession(Session s) => Write("session-crash.json", s);
    public static Session? LoadCrashSession() => Read<Session>("session-crash.json");
    public static void SaveCrashWindows(List<Session> windows) => Write("windows-crash.json", windows);
    public static List<Session> LoadCrashWindows()
    {
        var list = Read<List<Session>>("windows-crash.json")?.Where(s => s.Tabs.Count > 0).ToList();
        if (list is { Count: > 0 }) return list;
        return LoadCrashSession() is { Tabs.Count: > 0 } s ? new() { s } : new();
    }

    // ---- Счётчик заблокированного за всё время ----
    static long blockedTotal = -1;
    static bool statsDirty;

    public static long BlockedTotal
    {
        get
        {
            if (blockedTotal < 0) blockedTotal = Read<Dictionary<string, long>>("stats.json")?.GetValueOrDefault("blocked") ?? 0;
            return blockedTotal;
        }
    }

    public static void CountBlocked() { _ = BlockedTotal; blockedTotal++; statsDirty = true; }

    public static void Flush()
    {
        if (statsDirty) { statsDirty = false; Write("stats.json", new Dictionary<string, long> { ["blocked"] = blockedTotal }); }
        if (bookmarksDirty) { bookmarksDirty = false; Write("bookmarks.json", Bookmarks); }
        if (historyDirty) { historyDirty = false; Write("history.json", History); }
        PageIndex.Flush();
    }
}
