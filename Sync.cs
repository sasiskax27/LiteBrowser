using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LiteBrowser;

sealed class SyncBundle
{
    public int Version { get; set; } = 1;
    public DateTime Modified { get; set; }
    public string Device { get; set; } = "";
    public List<Bookmark> Bookmarks { get; set; } = new();
    public List<HistoryEntry>? History { get; set; }
    public AppSettings? Settings { get; set; }
    public DateTime SettingsModified { get; set; }
    public string? Vault { get; set; }          // vault.json как есть — он уже зашифрован мастер-паролем
    public DateTime VaultModified { get; set; }
    public List<Workspace>? Workspaces { get; set; }
    public List<Highlight>? Highlights { get; set; }
    public List<SyncOfflinePage>? Offline { get; set; }
}

/// <summary>Сохранённая страница внутри пакета синхронизации (сам MHTML — в base64).</summary>
sealed class SyncOfflinePage
{
    public OfflinePage Info { get; set; } = new();
    public string Data { get; set; } = "";
}

sealed class SyncEnvelope
{
    public string Salt { get; set; } = "";
    public int Iterations { get; set; }
    public string Nonce { get; set; } = "";
    public string Tag { get; set; } = "";
    public string Data { get; set; } = "";
}

/// <summary>
/// Синхронизация через папку облачного диска (Google Диск, Яндекс Диск, OneDrive, Dropbox).
/// Всё лежит в одном файле, зашифрованном паролем синхронизации (AES-256-GCM + PBKDF2).
/// Сам пароль на компьютере хранится защищённым Windows (DPAPI) — прочитать его можно только из вашей учётной записи.
/// </summary>
static class Sync
{
    const int Iterations = 300_000;
    static readonly byte[] Aad = Encoding.UTF8.GetBytes("LiteBrowserSync/1");
    static string KeyFile => Path.Combine(AppEnv.DataDir, "sync.key");
    static string FileIn(string folder) => Path.Combine(folder, "LiteBrowser-sync.lbsync");
    public static bool Running { get; private set; }
    public static string Status { get; private set; } = "";

    public static bool HasPassword => File.Exists(KeyFile);

    public static void SetPassword(string password)
    {
        if (password.Length < 8) throw new ArgumentException("Пароль синхронизации — не короче 8 символов");
        Directory.CreateDirectory(AppEnv.DataDir);
        File.WriteAllBytes(KeyFile, ProtectedData.Protect(Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser));
    }

    public static void Forget() { try { File.Delete(KeyFile); } catch { } }

    static string? Password()
    {
        try { return HasPassword ? Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(KeyFile), null, DataProtectionScope.CurrentUser)) : null; }
        catch { return null; }
    }

    /// <summary>Папки облачных дисков, найденные на компьютере.</summary>
    public static List<(string name, string path)> CloudFolders()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var list = new List<(string, string)>
        {
            ("OneDrive", Environment.GetEnvironmentVariable("OneDrive") ?? Path.Combine(home, "OneDrive")),
            ("Яндекс Диск", Path.Combine(home, "YandexDisk")),
            ("Google Диск", Path.Combine(home, "Google Drive")),
            ("Google Диск", @"G:\My Drive"),
            ("Google Диск", @"G:\Мой диск"),
            ("Dropbox", Path.Combine(home, "Dropbox")),
        };
        return list.Where(x => Directory.Exists(x.Item2)).Select(x => (x.Item1, Path.Combine(x.Item2, "LiteBrowser"))).DistinctBy(x => x.Item2).ToList();
    }

    static byte[] Derive(string pw, byte[] salt, int iter) => Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pw), salt, iter, HashAlgorithmName.SHA256, 32);

    internal static SyncBundle? ReadRemote(string file, string pw)
    {
        if (!File.Exists(file)) return null;
        var env = JsonSerializer.Deserialize<SyncEnvelope>(File.ReadAllText(file), AppSettings.Json) ?? throw new InvalidDataException("Файл синхронизации повреждён");
        var key = Derive(pw, Convert.FromBase64String(env.Salt), env.Iterations);
        var cipher = Convert.FromBase64String(env.Data);
        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(Convert.FromBase64String(env.Nonce), cipher, Convert.FromBase64String(env.Tag), plain, Aad);
        }
        catch (CryptographicException) { throw new UnauthorizedAccessException("Неверный пароль синхронизации (на другом компьютере задан другой)"); }
        return JsonSerializer.Deserialize<SyncBundle>(plain, AppSettings.Json);
    }

    internal static void WriteRemote(string file, string pw, SyncBundle b)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var key = Derive(pw, salt, Iterations);
        var plain = JsonSerializer.SerializeToUtf8Bytes(b, AppSettings.Json);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, cipher, tag, Aad);
        var env = new SyncEnvelope
        {
            Salt = Convert.ToBase64String(salt), Iterations = Iterations, Nonce = Convert.ToBase64String(nonce),
            Tag = Convert.ToBase64String(tag), Data = Convert.ToBase64String(cipher),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(env, AppSettings.Json));
        File.Move(file + ".tmp", file, true);
    }

    static string SettingsFile => Path.Combine(AppEnv.DataDir, "settings.json");
    static string VaultFile => Path.Combine(AppEnv.DataDir, "vault.json");

    /// <summary>Скачать изменения с других компьютеров, объединить и выгрузить своё. Вызывать из UI-потока.</summary>
    public static async Task<string> RunAsync(bool pull = true, bool push = true)
    {
        var s = AppEnv.Settings;
        if (string.IsNullOrWhiteSpace(s.SyncFolder)) throw new InvalidOperationException("Не выбрана папка синхронизации");
        var pw = Password() ?? throw new InvalidOperationException("Не задан пароль синхронизации");
        if (Running) return Status;
        Running = true;
        try
        {
            var file = FileIn(s.SyncFolder);
            int added = 0;
            bool settingsApplied = false, vaultApplied = false;
            int offlineAdded = 0, notesAdded = 0;
            if (pull)
            {
                var remote = await Task.Run(() => ReadRemote(file, pw));
                if (remote != null)
                {
                    added += Store.ImportBookmarks(remote.Bookmarks.Select(b => (b.Title, b.Url)));
                    if (s.SyncHistory && remote.History != null) Store.ImportHistory(remote.History);
                    var localSettingsTime = File.Exists(SettingsFile) ? File.GetLastWriteTimeUtc(SettingsFile) : DateTime.MinValue;
                    if (remote.Settings != null && remote.SettingsModified > localSettingsTime.AddSeconds(5))
                    {
                        var r = remote.Settings;
                        // Настройки, которые зависят от компьютера, не трогаем
                        r.UiScale = s.UiScale; r.PageZoom = s.PageZoom; r.ProxyMode = s.ProxyMode; r.ProxyType = s.ProxyType;
                        r.ProxyHost = s.ProxyHost; r.ProxyPort = s.ProxyPort; r.ProxySites = s.ProxySites;
                        r.SyncEnabled = s.SyncEnabled; r.SyncFolder = s.SyncFolder; r.SyncHistory = s.SyncHistory; r.SyncLast = s.SyncLast;
                        r.SidePanelOpen = s.SidePanelOpen; r.SidePanelWidth = s.SidePanelWidth; r.DefaultPromptDismissed = s.DefaultPromptDismissed;
                        r.SkippedUpdate = s.SkippedUpdate; r.ImportOffered = s.ImportOffered;
                        r.LockPinHash = s.LockPinHash; r.TorEnabled = s.TorEnabled; r.TorBridges = s.TorBridges;
                        AppEnv.UpdateSettings(r);
                        s = AppEnv.Settings;
                        settingsApplied = true;
                    }
                    var localVaultTime = File.Exists(VaultFile) ? File.GetLastWriteTimeUtc(VaultFile) : DateTime.MinValue;
                    if (remote.Vault != null && remote.VaultModified > localVaultTime.AddSeconds(5))
                    {
                        Vault.Lock();
                        File.WriteAllText(VaultFile, remote.Vault);
                        File.SetLastWriteTimeUtc(VaultFile, remote.VaultModified);
                        vaultApplied = true;
                    }
                    if (remote.Workspaces != null) Workspaces.Merge(remote.Workspaces);
                    if (remote.Highlights != null) notesAdded = Store.MergeHighlights(remote.Highlights);
                    if (remote.Offline != null) offlineAdded = Offline.Import(remote.Offline);
                }
            }
            if (push)
            {
                Store.Flush();
                var bundle = new SyncBundle
                {
                    Modified = DateTime.UtcNow,
                    Device = Environment.MachineName,
                    Bookmarks = Store.Bookmarks.Select(b => new Bookmark { Title = b.Title, Url = b.Url }).ToList(),
                    History = s.SyncHistory ? Store.History.ToList() : null,
                    Settings = s,
                    SettingsModified = File.Exists(SettingsFile) ? File.GetLastWriteTimeUtc(SettingsFile) : DateTime.UtcNow,
                    Vault = File.Exists(VaultFile) ? File.ReadAllText(VaultFile) : null,
                    VaultModified = File.Exists(VaultFile) ? File.GetLastWriteTimeUtc(VaultFile) : DateTime.MinValue,
                    Workspaces = Workspaces.All.ToList(),
                    Highlights = Store.Highlights.ToList(),
                    Offline = Offline.Export(),
                };
                await Task.Run(() => WriteRemote(file, pw, bundle));
            }
            var parts = new List<string> { "Синхронизировано " + DateTime.Now.ToString("dd.MM HH:mm") };
            if (added > 0) parts.Add($"новых закладок: {added}");
            if (settingsApplied) parts.Add("настройки обновлены");
            if (vaultApplied) parts.Add("пароли обновлены");
            if (notesAdded > 0) parts.Add($"новых заметок: {notesAdded}");
            if (offlineAdded > 0) parts.Add($"сохранённых страниц: {offlineAdded}");
            Status = string.Join(" · ", parts);
            var last = DateTime.Now.ToString("dd.MM.yyyy HH:mm");
            var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(AppEnv.Settings, AppSettings.Json), AppSettings.Json)!;
            copy.SyncLast = last;
            AppEnv.UpdateSettings(copy);
            return Status;
        }
        catch (Exception ex)
        {
            Status = "Ошибка синхронизации: " + ex.Message;
            throw;
        }
        finally { Running = false; }
    }

    /// <summary>При выходе: только выгрузка, синхронно (браузер уже закрывается).</summary>
    public static void PushOnExit()
    {
        try
        {
            var s = AppEnv.Settings;
            if (!s.SyncEnabled || string.IsNullOrWhiteSpace(s.SyncFolder) || !HasPassword) return;
            // В фоновом потоке: иначе UI-поток ждал бы сам себя. Не дольше 20 секунд.
            Task.Run(() => RunAsync(pull: false, push: true)).Wait(TimeSpan.FromSeconds(20));
        }
        catch { }
    }
}
