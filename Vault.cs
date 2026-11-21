using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LiteBrowser;

sealed class PasswordEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Origin { get; set; } = "";   // https://site.com
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime Updated { get; set; } = DateTime.Now;
}

/// <summary>Зашифрованный файл хранилища. Внутри только шифротекст — без мастер-пароля ничего не прочитать.</summary>
sealed class VaultFile
{
    public int Version { get; set; } = 1;
    public string Salt { get; set; } = "";
    public int Iterations { get; set; }
    public string Nonce { get; set; } = "";
    public string Tag { get; set; } = "";
    public string Data { get; set; } = "";
}

/// <summary>
/// Хранилище паролей.
/// Ключ = PBKDF2-SHA256(мастер-пароль, случайная соль, 600 000 итераций), шифрование — AES-256-GCM
/// (GCM ещё и проверяет целостность: неверный пароль или подмена файла = ошибка расшифровки).
/// Ключ живёт только в памяти, пока хранилище открыто, и затирается при блокировке.
/// </summary>
static class Vault
{
    const int Iterations = 600_000;
    static readonly byte[] Aad = Encoding.UTF8.GetBytes("LiteBrowserVault/1");
    static readonly object sync = new();
    static byte[]? key;
    static byte[] salt = Array.Empty<byte>();
    static List<PasswordEntry> entries = new();
    static int failedAttempts;
    static DateTime lockedUntil;

    public static DateTime LastUsed { get; private set; } = DateTime.Now;
    public static event Action? Changed;

    /// <summary>Только для самотеста: другая папка хранилища.</summary>
    public static string? DirOverride { get; set; }
    static string Dir => DirOverride ?? AppEnv.DataDir;
    static string FilePath => Path.Combine(Dir, "vault.json");
    public static bool Exists => File.Exists(FilePath);
    public static bool Unlocked { get { lock (sync) return key != null; } }
    public static int Count { get { lock (sync) return entries.Count; } }

    static byte[] Derive(string master, byte[] s, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(master), s, iterations, HashAlgorithmName.SHA256, 32);

    static void Raise() => Changed?.Invoke();
    static void Touch() => LastUsed = DateTime.Now;

    public static void Create(string master)
    {
        if (master.Length < 8) throw new ArgumentException("Мастер-пароль должен быть не короче 8 символов");
        if (Exists) throw new InvalidOperationException("Хранилище уже создано");
        var s = RandomNumberGenerator.GetBytes(16);
        var k = Derive(master, s, Iterations);
        lock (sync) { salt = s; key = k; entries = new(); SaveLocked(); }
        Touch();
        Raise();
    }

    /// <summary>Открывает хранилище. Медленно (~0,5 с) — специально, чтобы пароль нельзя было быстро подобрать.</summary>
    public static (bool ok, string? error) Unlock(string master)
    {
        if (DateTime.Now < lockedUntil)
            return (false, $"Слишком много попыток. Подождите {(int)(lockedUntil - DateTime.Now).TotalSeconds + 1} с");
        var file = JsonSerializer.Deserialize<VaultFile>(File.ReadAllText(FilePath), AppSettings.Json)
                   ?? throw new InvalidDataException("Файл хранилища повреждён");
        var s = Convert.FromBase64String(file.Salt);
        var k = Derive(master, s, file.Iterations);
        try
        {
            var cipher = Convert.FromBase64String(file.Data);
            var plain = new byte[cipher.Length];
            using (var aes = new AesGcm(k, 16))
                aes.Decrypt(Convert.FromBase64String(file.Nonce), cipher, Convert.FromBase64String(file.Tag), plain, Aad);
            var list = JsonSerializer.Deserialize<List<PasswordEntry>>(plain, AppSettings.Json) ?? new();
            CryptographicOperations.ZeroMemory(plain);
            lock (sync) { key = k; salt = s; entries = list; }
            failedAttempts = 0;
            Touch();
            Raise();
            return (true, null);
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(k);
            if (++failedAttempts >= 5) { lockedUntil = DateTime.Now.AddSeconds(30); failedAttempts = 0; }
            return (false, "Неверный мастер-пароль");
        }
    }

    public static void Lock()
    {
        lock (sync)
        {
            if (key == null) return;
            CryptographicOperations.ZeroMemory(key);
            key = null;
            entries = new();
        }
        Raise();
    }

    static void SaveLocked()
    {
        if (key == null) throw new InvalidOperationException("Хранилище заблокировано");
        var plain = JsonSerializer.SerializeToUtf8Bytes(entries, AppSettings.Json);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, cipher, tag, Aad);
        CryptographicOperations.ZeroMemory(plain);
        var file = new VaultFile
        {
            Salt = Convert.ToBase64String(salt), Iterations = Iterations,
            Nonce = Convert.ToBase64String(nonce), Tag = Convert.ToBase64String(tag), Data = Convert.ToBase64String(cipher),
        };
        Directory.CreateDirectory(Dir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(file, AppSettings.Json));
        File.Move(tmp, FilePath, true);
    }

    public static void ChangeMaster(string oldMaster, string newMaster)
    {
        if (newMaster.Length < 8) throw new ArgumentException("Новый мастер-пароль должен быть не короче 8 символов");
        var (ok, err) = Unlock(oldMaster);
        if (!ok) throw new UnauthorizedAccessException(err);
        var s = RandomNumberGenerator.GetBytes(16);
        var k = Derive(newMaster, s, Iterations);
        lock (sync)
        {
            if (key != null) CryptographicOperations.ZeroMemory(key);
            key = k; salt = s;
            SaveLocked();
        }
        Raise();
    }

    // ---- Записи ----

    public static List<PasswordEntry> All()
    {
        lock (sync) { Touch(); return entries.Select(Clone).ToList(); }
    }

    public static List<PasswordEntry> ForOrigin(string origin)
    {
        var host = PasswordTools.HostKey(origin);
        lock (sync) { Touch(); return entries.Where(e => PasswordTools.HostKey(e.Origin) == host).Select(Clone).ToList(); }
    }

    public static PasswordEntry? Get(string id)
    {
        lock (sync) { Touch(); var e = entries.FirstOrDefault(x => x.Id == id); return e == null ? null : Clone(e); }
    }

    static PasswordEntry Clone(PasswordEntry e) => new()
    {
        Id = e.Id, Origin = e.Origin, Username = e.Username, Password = e.Password, Created = e.Created, Updated = e.Updated,
    };

    /// <summary>Добавляет или обновляет запись (по id, иначе по сайту + логину).</summary>
    public static void Upsert(string? id, string origin, string username, string password)
    {
        lock (sync)
        {
            var e = id != null ? entries.FirstOrDefault(x => x.Id == id) : null;
            e ??= entries.FirstOrDefault(x => PasswordTools.HostKey(x.Origin) == PasswordTools.HostKey(origin) && x.Username == username);
            if (e == null) entries.Add(e = new PasswordEntry());
            e.Origin = origin; e.Username = username; e.Password = password; e.Updated = DateTime.Now;
            SaveLocked();
            Touch();
        }
        Raise();
    }

    public static int ImportMany(IEnumerable<(string origin, string username, string password)> items)
    {
        int n = 0;
        lock (sync)
        {
            foreach (var (origin, username, password) in items)
            {
                var e = entries.FirstOrDefault(x => PasswordTools.HostKey(x.Origin) == PasswordTools.HostKey(origin) && x.Username == username);
                if (e != null && e.Password == password) continue;
                if (e == null) entries.Add(e = new PasswordEntry());
                e.Origin = origin; e.Username = username; e.Password = password; e.Updated = DateTime.Now;
                n++;
            }
            if (n > 0) SaveLocked();
            Touch();
        }
        if (n > 0) Raise();
        return n;
    }

    public static void Delete(string id)
    {
        lock (sync)
        {
            if (entries.RemoveAll(x => x.Id == id) == 0) return;
            SaveLocked();
        }
        Raise();
    }
}

static class PasswordTools
{
    /// <summary>Источник страницы: схема + хост (+ порт).</summary>
    public static string? Origin(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("https" or "http")) return null;
        return u.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
    }

    /// <summary>Ключ сравнения сайтов: хост без www и порта.</summary>
    public static string HostKey(string originOrUrl)
    {
        var s = originOrUrl.Contains("://") ? originOrUrl : "https://" + originOrUrl;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u)) return originOrUrl.ToLowerInvariant();
        var h = u.Host.ToLowerInvariant();
        return h.StartsWith("www.") ? h[4..] : h;
    }

    public static string Host(string origin) => Uri.TryCreate(origin, UriKind.Absolute, out var u) ? u.Host : origin;

    public static string Generate(int length = 20, bool symbols = true)
    {
        length = Math.Clamp(length, 8, 128);
        const string lower = "abcdefghijkmnopqrstuvwxyz", upper = "ABCDEFGHJKLMNPQRSTUVWXYZ", digits = "23456789", sym = "!@#$%^&*-_=+?";
        var sets = symbols ? new[] { lower, upper, digits, sym } : new[] { lower, upper, digits };
        var all = string.Concat(sets);
        var chars = new char[length];
        for (int i = 0; i < length; i++) chars[i] = all[RandomNumberGenerator.GetInt32(all.Length)];
        // Гарантируем хотя бы по одному символу из каждой группы
        var positions = Enumerable.Range(0, length).OrderBy(_ => RandomNumberGenerator.GetInt32(int.MaxValue)).Take(sets.Length).ToArray();
        for (int i = 0; i < sets.Length; i++) chars[positions[i]] = sets[i][RandomNumberGenerator.GetInt32(sets[i].Length)];
        return new string(chars);
    }

    /// <summary>CSV из Chrome, Edge, Яндекса, Opera, Brave (name,url,username,password) и Firefox (url,username,password,…).</summary>
    public static List<(string origin, string username, string password)> ParseCsv(string text)
    {
        var rows = ReadCsv(text);
        var result = new List<(string, string, string)>();
        if (rows.Count < 2) return result;
        var head = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int iu = head.FindIndex(h => h is "url" or "origin" or "login_uri" or "website");
        int il = head.FindIndex(h => h is "username" or "login" or "login_username" or "user");
        int ip = head.FindIndex(h => h is "password" or "login_password");
        if (iu < 0 || ip < 0) throw new InvalidDataException("Не похоже на файл паролей: нет столбцов url и password");
        foreach (var r in rows.Skip(1))
        {
            if (r.Count <= Math.Max(iu, ip)) continue;
            var origin = Origin(r[iu].Trim());
            var pass = r[ip];
            if (origin == null || pass.Length == 0) continue;
            result.Add((origin, il >= 0 && il < r.Count ? r[il] : "", pass));
        }
        return result;
    }

    static List<List<string>> ReadCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString()); cell.Clear();
                if (row.Count > 1 || row[0].Length > 0) rows.Add(row);
                row = new List<string>();
            }
            else cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }

    static System.Windows.Forms.Timer? clipTimer;

    /// <summary>Копирует в буфер обмена и очищает его через 30 секунд (если там всё ещё это значение).</summary>
    public static void CopySecret(string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        Clipboard.SetText(value);
        clipTimer?.Stop();
        clipTimer = new System.Windows.Forms.Timer { Interval = 30_000 };
        clipTimer.Tick += (_, _) =>
        {
            clipTimer!.Stop();
            try { if (Clipboard.ContainsText() && Clipboard.GetText() == value) Clipboard.Clear(); } catch { }
        };
        clipTimer.Start();
    }
}
