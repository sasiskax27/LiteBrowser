using System.Text.Json;

namespace LiteBrowser;

sealed class Workspace
{
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#7c9cff";
    public Session Session { get; set; } = new();
}

sealed class WorkspaceFile
{
    public string Current { get; set; } = "";
    public List<Workspace> Items { get; set; } = new();
}

/// <summary>
/// Рабочие пространства: «Учёба», «Работа», «Игры» — у каждого свой набор вкладок.
/// Переключение сохраняет вкладки текущего пространства и открывает вкладки другого (лениво).
/// </summary>
static class Workspaces
{
    public const string DefaultName = "Основное";
    public static readonly string[] Colors = { "#7c9cff", "#6ecf96", "#ffb86b", "#ff7878", "#b07cff", "#5ccfe6", "#ff8fc7", "#e6d35c" };
    static WorkspaceFile? data;
    static string FilePath => Path.Combine(AppEnv.DataDir, "workspaces.json");

    static WorkspaceFile Data
    {
        get
        {
            if (data != null) return data;
            try { data = File.Exists(FilePath) ? JsonSerializer.Deserialize<WorkspaceFile>(File.ReadAllText(FilePath), AppSettings.Json) : null; }
            catch { }
            data ??= new WorkspaceFile();
            if (data.Items.Count == 0) data.Items.Add(new Workspace { Name = DefaultName });
            if (data.Items.All(w => w.Name != data.Current)) data.Current = data.Items[0].Name;
            return data;
        }
    }

    public static IReadOnlyList<Workspace> All => Data.Items;
    public static Workspace Current => Data.Items.First(w => w.Name == Data.Current);
    /// <summary>Есть ли больше одного пространства (иначе в интерфейсе их не показываем).</summary>
    public static bool InUse => Data.Items.Count > 1;

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(AppEnv.DataDir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Data, AppSettings.Json));
            File.Move(tmp, FilePath, true);
        }
        catch { }
    }

    public static Workspace Create(string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > 30) throw new ArgumentException("Название — от 1 до 30 символов");
        if (Data.Items.Any(w => w.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Такое пространство уже есть");
        var w = new Workspace { Name = name, Color = Colors[Data.Items.Count % Colors.Length] };
        Data.Items.Add(w);
        Save();
        return w;
    }

    public static void Rename(Workspace w, string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > 30) throw new ArgumentException("Название — от 1 до 30 символов");
        if (Data.Items.Any(x => x != w && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Такое пространство уже есть");
        bool cur = Data.Current == w.Name;
        w.Name = name;
        if (cur) Data.Current = name;
        Save();
    }

    public static void Delete(Workspace w)
    {
        if (Data.Items.Count <= 1 || Data.Current == w.Name) return;
        Data.Items.Remove(w);
        Save();
    }

    /// <summary>Запомнить вкладки текущего пространства и сделать текущим другое.</summary>
    public static void SwitchTo(Workspace target, Session currentTabs)
    {
        Current.Session = currentTabs;
        Data.Current = target.Name;
        Save();
    }
}
