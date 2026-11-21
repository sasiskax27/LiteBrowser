using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LiteBrowser;

sealed class BrowserProfileInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>
/// Профили: «Работа», «Личное» и т. д. У каждого — своя папка данных: закладки, история, пароли, настройки,
/// cookies и входы в аккаунты полностью раздельно. Профиль открывается отдельным окном (и отдельным процессом).
/// </summary>
static class Profiles
{
    public static readonly string RootDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiteBrowser");
    static string ListFile => Path.Combine(RootDir, "profiles.json");

    /// <summary>Имя текущего профиля (пусто — основной).</summary>
    public static string Current => Environment.GetEnvironmentVariable("LITEBROWSER_PROFILE") ?? "";

    public static List<BrowserProfileInfo> List()
    {
        try { return File.Exists(ListFile) ? JsonSerializer.Deserialize<List<BrowserProfileInfo>>(File.ReadAllText(ListFile), AppSettings.Json) ?? new() : new(); }
        catch { return new(); }
    }

    static void Save(List<BrowserProfileInfo> list)
    {
        Directory.CreateDirectory(RootDir);
        File.WriteAllText(ListFile, JsonSerializer.Serialize(list, AppSettings.Json));
    }

    public static BrowserProfileInfo Create(string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > 40) throw new ArgumentException("Название профиля — от 1 до 40 символов");
        var list = List();
        if (list.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Такой профиль уже есть");
        var id = Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9а-яё]+", "-").Trim('-');
        if (id.Length == 0) id = "profile";
        id = id + "-" + Guid.NewGuid().ToString("N")[..6];
        var p = new BrowserProfileInfo { Id = id, Name = name };
        list.Add(p);
        Save(list);
        return p;
    }

    public static void Delete(string id)
    {
        var list = List();
        list.RemoveAll(p => p.Id == id);
        Save(list);
        try { Directory.Delete(Path.Combine(RootDir, "profiles", id), true); } catch { }
    }

    /// <summary>Открыть профиль в новом окне. null — основной профиль.</summary>
    public static void Open(BrowserProfileInfo? p)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        if (p == null)
        {
            psi.Environment.Remove("LITEBROWSER_DATA");
            psi.Environment.Remove("LITEBROWSER_PROFILE");
        }
        else
        {
            psi.Environment["LITEBROWSER_DATA"] = Path.Combine(RootDir, "profiles", p.Id);
            psi.Environment["LITEBROWSER_PROFILE"] = p.Name;
        }
        Process.Start(psi);
    }
}
