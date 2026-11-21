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

sealed class Session
{
    public List<string> Tabs { get; set; } = new();
    public int Active { get; set; }
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
        History.RemoveAll(h => h.Url == url && Math.Abs((h.Time - time).TotalSeconds) < 1);
        historyDirty = true;
    }

    public static void ClearHistory()
    {
        History.Clear();
        historyDirty = true;
        Flush();
    }

    // ---- Сессия ----

    public static void SaveSession(Session s) => Write("session.json", s);
    public static Session? LoadSession() => Read<Session>("session.json");

    public static void Flush()
    {
        if (bookmarksDirty) { bookmarksDirty = false; Write("bookmarks.json", Bookmarks); }
        if (historyDirty) { historyDirty = false; Write("history.json", History); }
    }
}
