using System.Text.Json;

namespace LiteBrowser;

sealed class ReadLaterItem
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTime Added { get; set; } = DateTime.Now;
    public bool Read { get; set; }
}

/// <summary>Список «Прочитать позже»: страницы, к которым хочется вернуться, с отметкой «прочитано».</summary>
static class ReadLater
{
    static List<ReadLaterItem>? items;
    static string FilePath => Path.Combine(AppEnv.DataDir, "readlater.json");
    public static event Action? Changed;

    public static List<ReadLaterItem> All
    {
        get
        {
            if (items != null) return items;
            try { items = File.Exists(FilePath) ? JsonSerializer.Deserialize<List<ReadLaterItem>>(File.ReadAllText(FilePath), AppSettings.Json) : null; }
            catch { }
            return items ??= new();
        }
    }

    static void Save()
    {
        try
        {
            Directory.CreateDirectory(AppEnv.DataDir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(All, AppSettings.Json));
            File.Move(tmp, FilePath, true);
        }
        catch { }
        Changed?.Invoke();
    }

    public static bool Contains(string url) => All.Any(i => i.Url == url);

    /// <summary>Добавить (или вернуть в непрочитанные, если уже есть).</summary>
    public static void Add(string url, string title)
    {
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;
        var it = All.FirstOrDefault(i => i.Url == url);
        if (it != null) { it.Read = false; it.Added = DateTime.Now; All.Remove(it); All.Insert(0, it); }
        else All.Insert(0, new ReadLaterItem { Url = url, Title = title.Length > 0 ? title : url });
        if (All.Count > 1000) All.RemoveAt(All.Count - 1);
        Save();
    }

    public static void Remove(string url) { if (All.RemoveAll(i => i.Url == url) > 0) Save(); }

    public static void SetRead(string url, bool read)
    {
        var it = All.FirstOrDefault(i => i.Url == url);
        if (it == null || it.Read == read) return;
        it.Read = read;
        Save();
    }

    /// <summary>Открыли страницу из списка — отмечаем прочитанной.</summary>
    public static void MarkVisited(string url)
    {
        var it = All.FirstOrDefault(i => i.Url == url && !i.Read);
        if (it != null) { it.Read = true; Save(); }
    }

    /// <summary>Синхронизация: добавить отсутствующие записи с другого компьютера.</summary>
    public static int Merge(List<ReadLaterItem> remote)
    {
        int added = 0;
        foreach (var r in remote)
        {
            var it = All.FirstOrDefault(i => i.Url == r.Url);
            if (it == null) { All.Add(r); added++; }
            else if (r.Read && !it.Read) it.Read = true;
        }
        if (added > 0) { All.Sort((a, b) => b.Added.CompareTo(a.Added)); }
        Save();
        return added;
    }
}
