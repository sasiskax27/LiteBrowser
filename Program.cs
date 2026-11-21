using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace LiteBrowser;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Диагностика переноса: только читает данные других браузеров и пишет сводку в файл
        if (args.Length == 2 && args[0] == "--import-check")
        {
            var lines = new List<string>();
            foreach (var p in Importer.Scan())
            {
                try { lines.Add($"{p.Browser} / {p.Profile}: закладок {Importer.ReadBookmarks(p).Count}, истории {Importer.ReadHistory(p).Count}"); }
                catch (Exception ex) { lines.Add($"{p.Browser} / {p.Profile}: ошибка — {ex.Message}"); }
            }
            File.WriteAllLines(args[1], lines.Count > 0 ? lines : new List<string> { "браузеры не найдены" });
            return;
        }

        // Самотест хранилища паролей во временной папке
        if (args.Length == 2 && args[0] == "--vault-selftest")
        {
            var log = new List<string>();
            void Check(string name, bool ok) => log.Add((ok ? "OK   " : "FAIL ") + name);
            var dir = Path.Combine(Path.GetTempPath(), "LiteVaultTest-" + Guid.NewGuid().ToString("N"));
            Vault.DirOverride = dir;
            try
            {
                Vault.Create("correct horse 1");
                Vault.Upsert(null, "https://example.com", "user@mail.ru", "S3cret-Пароль!");
                var raw = File.ReadAllText(Path.Combine(dir, "vault.json"));
                Check("в файле нет пароля открытым текстом", !raw.Contains("S3cret") && !raw.Contains("user@mail.ru") && !raw.Contains("example.com"));
                Vault.Lock();
                Check("после блокировки записи недоступны", !Vault.Unlocked && Vault.Count == 0);
                Check("неверный мастер-пароль не открывает", !Vault.Unlock("wrong password").ok);
                Check("верный мастер-пароль открывает", Vault.Unlock("correct horse 1").ok);
                var e = Vault.ForOrigin("https://www.example.com/login").FirstOrDefault();
                Check("пароль расшифрован верно и найден по сайту", e?.Password == "S3cret-Пароль!" && e.Username == "user@mail.ru");
                var bytes = File.ReadAllBytes(Path.Combine(dir, "vault.json"));
                var json = System.Text.Encoding.UTF8.GetString(bytes);
                var f = System.Text.Json.JsonSerializer.Deserialize<VaultFile>(json, AppSettings.Json)!;
                var data = Convert.FromBase64String(f.Data); data[0] ^= 1; f.Data = Convert.ToBase64String(data);
                File.WriteAllText(Path.Combine(dir, "vault.json"), System.Text.Json.JsonSerializer.Serialize(f, AppSettings.Json));
                Vault.Lock();
                Check("подменённый файл не открывается", !Vault.Unlock("correct horse 1").ok);
                File.WriteAllBytes(Path.Combine(dir, "vault.json"), bytes);
                Vault.ChangeMaster("correct horse 1", "new master 22");
                Vault.Lock();
                Check("после смены старый пароль не подходит", !Vault.Unlock("correct horse 1").ok);
                Check("после смены новый пароль подходит", Vault.Unlock("new master 22").ok && Vault.Count == 1);
                var csv = PasswordTools.ParseCsv("name,url,username,password,note\r\nvk,https://vk.com/login,ivan,\"pa,ss\"\"1\",\r\n");
                Check("CSV Chrome разобран", csv.Count == 1 && csv[0].origin == "https://vk.com" && csv[0].password == "pa,ss\"1");
                var ff = PasswordTools.ParseCsv("\"url\",\"username\",\"password\",\"httpRealm\"\n\"https://github.com\",\"me\",\"x1\",\"\"\n");
                Check("CSV Firefox разобран", ff.Count == 1 && ff[0].origin == "https://github.com");
                var bwExport = PasswordTools.ExportCsv(Vault.All());
                var back = PasswordTools.ParseCsv(bwExport);
                Check("экспорт в формат Bitwarden читается обратно", back.Count == 1 && back[0].password == "S3cret-Пароль!" && bwExport.StartsWith("folder,favorite,type,name"));
                var bw = PasswordTools.ParseCsv("folder,favorite,type,name,notes,fields,reprompt,login_uri,login_username,login_password,login_totp\nSocial,,login,VK,,,0,https://vk.com,ivan,pw123,\n");
                Check("CSV из Bitwarden разобран", bw.Count == 1 && bw[0].origin == "https://vk.com" && bw[0].username == "ivan" && bw[0].password == "pw123");
                Check("TOTP по тестовому вектору RFC 6238", Totp.CodeAt("otpauth://totp/x?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ&digits=8", 59, out _) == "94287082"
                                                       && Totp.CodeAt("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", 1111111109, out _) == "081804");
                var sf = Path.Combine(dir, "sync.lbsync");
                Sync.WriteRemote(sf, "sync-pass-123", new SyncBundle { Bookmarks = { new Bookmark { Title = "VK", Url = "https://vk.com/" } }, Vault = "{}" });
                var sraw = File.ReadAllText(sf);
                var sb = Sync.ReadRemote(sf, "sync-pass-123");
                Check("синхронизация: файл зашифрован и читается обратно", !sraw.Contains("vk.com") && sb?.Bookmarks.Count == 1 && sb.Bookmarks[0].Url == "https://vk.com/");
                bool syncWrong = false; try { Sync.ReadRemote(sf, "wrong-password"); } catch (UnauthorizedAccessException) { syncWrong = true; }
                Check("синхронизация: неверный пароль не подходит", syncWrong);
                var g = PasswordTools.Generate(20);
                Check("генератор: 20 символов, все группы", g.Length == 20 && g.Any(char.IsUpper) && g.Any(char.IsLower) && g.Any(char.IsDigit) && g.Any(c => !char.IsLetterOrDigit(c)));
            }
            catch (Exception ex) { log.Add("FAIL исключение: " + ex); }
            finally { Vault.Lock(); try { Directory.Delete(dir, true); } catch { } }
            File.WriteAllLines(args[1], log);
            return;
        }

        // Самотест блокировщика рекламы на настоящих списках
        if (args.Length == 2 && args[0] == "--adblock-selftest")
        {
            var log = new List<string>();
            void Check(string name, bool ok) => log.Add((ok ? "OK   " : "FAIL ") + name);
            try
            {
                var s = new AppSettings();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                AdBlock.UpdateAsync(s, force: true).GetAwaiter().GetResult();
                log.Add($"загрузка и разбор: {sw.ElapsedMilliseconds} мс");
                foreach (var l in AdBlock.Lists)
                    log.Add($"     {l.Name}: правил {AdBlock.Status[l.Id].Rules}, ошибка: {AdBlock.Status[l.Id].Error ?? "нет"}");
                var e = AdBlock.Engine;
                log.Add($"     сетевых правил {e.NetworkRules}, скрытия {e.CosmeticRules}, общий CSS {e.GenericCss.Length / 1024} КБ");
                Check("doubleclick.net блокируется", AdBlock.ShouldBlock("securepubads.g.doubleclick.net", "example.com"));
                Check("google-analytics блокируется (EasyPrivacy)", AdBlock.ShouldBlock("www.google-analytics.com", "example.com"));
                Check("Яндекс.Метрика блокируется", AdBlock.ShouldBlock("mc.yandex.ru", "lenta.ru"));
                Check("vk.com на vk.com не блокируется", !AdBlock.ShouldBlock("vk.com", "vk.com"));
                Check("youtube.com не блокируется", !AdBlock.ShouldBlock("www.youtube.com", "www.youtube.com"));
                Check("github.com не блокируется", !AdBlock.ShouldBlock("github.githubassets.com", "github.com"));
                Check("yandex.ru не блокируется", !AdBlock.ShouldBlock("yandex.ru", "yandex.ru"));
                Check("есть правила скрытия для youtube.com", AdBlock.PageScript("www.youtube.com").Contains("lite-ab-s"));
                log.Add($"     правил по адресам: {AdBlock.PatternRules}");
                bool B(string url, string page, ResType t = ResType.Script) => AdBlock.ShouldBlock(url, new Uri(url).Host, page, t);
                Check("yandex.ru/ads/system/context.js блокируется", B("https://yandex.ru/ads/system/context.js", "lenta.ru"));
                Check("an.yandex.ru/system/context.js блокируется", B("https://an.yandex.ru/system/context.js", "lenta.ru"));
                Check("ads.adfox.ru блокируется", B("https://ads.adfox.ru/12345/getCode?p1=a", "lenta.ru"));
                Check("googletagservices gpt.js блокируется", B("https://securepubads.g.doubleclick.net/tag/js/gpt.js", "rbc.ru"));
                Check("баннер по пути /banners/ блокируется", B("https://cdn.example-news.ru/banners/top_728x90.gif", "example-news.ru", ResType.Image) ||
                                                            B("https://example.com/ads/banner.gif", "example.com", ResType.Image));
                Check("картинки lenta.ru не блокируются", !B("https://icdn.lenta.ru/images/2026/11/21/photo.jpg", "lenta.ru", ResType.Image));
                Check("скрипт lenta.ru не блокируется", !B("https://lenta.ru/static/js/app.min.js", "lenta.ru"));
                Check("yastatic.net (jQuery Яндекса) не блокируется", !B("https://yastatic.net/jquery/3.3.1/jquery.min.js", "lenta.ru"));
                Check("видео youtube не блокируется", !B("https://rr1---sn-abc.googlevideo.com/videoplayback?id=1", "www.youtube.com", ResType.Media));
                Check("vk.com api не блокируется", !B("https://api.vk.com/method/users.get", "vk.com", ResType.Xhr));
                Check("mail.ru почта не блокируется", !B("https://e.mail.ru/inbox/", "e.mail.ru", ResType.Xhr));
                sw.Restart();
                var urls = new[] { "https://lenta.ru/static/js/app.min.js", "https://icdn.lenta.ru/images/2026/a/b/c.jpg", "https://yandex.ru/ads/system/context.js",
                                   "https://www.youtube.com/s/player/abc/base.js", "https://api.vk.com/method/wall.get?owner_id=1&v=5.131" };
                for (int i = 0; i < 20_000; i++) { var u = urls[i % urls.Length]; AdBlock.ShouldBlock(u + "?r=" + i, new Uri(u).Host, "lenta.ru", ResType.Script); }
                log.Add($"скорость по адресам (с первой компиляцией правил): 20 000 проверок за {sw.ElapsedMilliseconds} мс");
                sw.Restart();
                for (int i = 0; i < 20_000; i++) { var u = urls[i % urls.Length]; AdBlock.ShouldBlock(u + "?r=" + i, new Uri(u).Host, "lenta.ru", ResType.Script); }
                log.Add($"скорость по адресам (повторно): 20 000 проверок за {sw.ElapsedMilliseconds} мс");
                sw.Restart();
                for (int i = 0; i < 100_000; i++) AdBlock.ShouldBlock("cdn" + (i % 50) + ".static.example-site.com", "example-site.com");
                log.Add($"скорость: 100 000 проверок за {sw.ElapsedMilliseconds} мс");
            }
            catch (Exception ex) { log.Add("FAIL исключение: " + ex); }
            File.WriteAllLines(args[1], log);
            return;
        }

        if (args.Length == 4 && args[0] == "--download-test")
        {
            ApplicationConfiguration.Initialize();
            DownloadSelfTest.Run(args[1], args[2], args[3] == "handled");
            return;
        }

        if (args.Length == 2 && args[0] == "--ext-selftest")
        {
            ApplicationConfiguration.Initialize();
            ExtensionSelfTest.Run(args[1]);
            return;
        }

        bool restore = args.Contains("--restore");
        bool incognito = args.Contains("--incognito");
        string? url = args.FirstOrDefault(a => !a.StartsWith("--"));

        // Если браузер уже открыт — передаём ему ссылку и выходим
        using var mutex = new Mutex(true, AppEnv.InstanceName, out bool first);
        if (!first && !restore && SingleInstance.Send((incognito ? "incognito" : "normal") + "\n" + (url ?? "")))
            return;

        // Убираем старую версию, оставшуюся после обновления
        try { File.Delete(Path.ChangeExtension(Environment.ProcessPath!, ".old")); } catch { }

        ApplicationConfiguration.Initialize();
        AppEnv.Init();
        Theme.ApplyPalette(AppEnv.Settings.UiTheme, AppEnv.Settings.Accent);
        Application.Run(new BrowserApp(url, incognito, restore));
    }
}

static class SingleInstance
{
    static string PipeName => AppEnv.InstanceName;

    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);

    public static bool Send(string message)
    {
        try
        {
            AllowSetForegroundWindow(-1);
            using var c = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            c.Connect(1500);
            using var w = new StreamWriter(c);
            w.Write(message);
            w.Flush();
            return true;
        }
        catch { return false; }
    }

    public static void Listen(Action<string> onMessage)
    {
        new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var s = new NamedPipeServerStream(PipeName, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                    s.WaitForConnection();
                    using var r = new StreamReader(s);
                    var msg = r.ReadToEnd();
                    if (msg.Length < 10000) onMessage(msg);
                }
                catch { Thread.Sleep(500); }
            }
        }) { IsBackground = true, Name = "LiteBrowser pipe" }.Start();
    }
}

/// <summary>Управляет окнами: приложение живёт, пока открыто хоть одно окно.</summary>
sealed class BrowserApp : ApplicationContext
{
    public static BrowserApp Instance { get; private set; } = null!;
    readonly List<MainForm> windows = new();
    readonly System.Windows.Forms.Timer flush = new() { Interval = 3000 };
    readonly Control invoker = new();
    MainForm? lastActive;

    public BrowserApp(string? url, bool incognito, bool restore)
    {
        Instance = this;
        _ = invoker.Handle;
        flush.Tick += (_, _) =>
        {
            Store.Flush();
            // Автоблокировка менеджера паролей
            int min = AppEnv.Settings.AutoLockMinutes;
            if (min > 0 && Vault.Unlocked && (DateTime.Now - Vault.LastUsed).TotalMinutes >= min) Vault.Lock();
        };
        flush.Start();
        // Блокируем пароли, когда пользователь блокирует Windows (Win+L)
        SystemEvents.SessionSwitch += (_, e) => { if (e.Reason == SessionSwitchReason.SessionLock) Vault.Lock(); };
        SingleInstance.Listen(m => invoker.BeginInvoke(() => OnRemote(m)));

        List<string>? tabs = null;
        int active = 0;
        Session? session = null;
        if (url == null && !incognito && (restore || AppEnv.Settings.RestoreSession))
        {
            var s = Store.LoadSession();
            if (s is { Tabs.Count: > 0 }) { tabs = s.Tabs; active = s.Active; session = s; }
        }
        var first = OpenWindow(incognito, url, tabs, active, session);
        _ = first.RunStartupChecks();
        _ = first.RunStartupSync();

        if (AppEnv.Settings.BlockAds && AppEnv.Settings.ExtendedBlocklist)
            _ = Blocklist.UpdateAsync(AppEnv.DataDir, AppEnv.Settings, force: false);
        _ = AdBlock.InitAsync(AppEnv.Settings);
    }

    void OnRemote(string msg)
    {
        var parts = msg.Split('\n', 2);
        bool inc = parts[0] == "incognito";
        string? url = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null;
        if (inc) { OpenWindow(true, url); return; }
        var target = lastActive is { IsDisposed: false, Incognito: false } ? lastActive : windows.FirstOrDefault(w => !w.Incognito);
        if (target != null) target.OpenFromOutside(url);
        else OpenWindow(false, url);
    }

    public MainForm OpenWindow(bool incognito, string? url = null, List<string>? tabs = null, int active = 0, Session? session = null)
    {
        var f = new MainForm(incognito, url, tabs, active) { StartSession = session };
        windows.Add(f);
        f.Activated += (_, _) => lastActive = f;
        f.FormClosed += (_, _) =>
        {
            windows.Remove(f);
            if (lastActive == f) lastActive = windows.LastOrDefault();
            if (windows.Count > 0) return;
            Store.Flush();
            Sync.PushOnExit();
            ExitThread();
        };
        f.Show();
        return f;
    }

    /// <summary>Перезапуск с сохранением вкладок (нужен после смены прокси).</summary>
    public void Restart()
    {
        var main = windows.FirstOrDefault(w => !w.Incognito);
        if (main != null) Store.SaveSession(main.GetSession());
        Store.Flush();
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--restore") { UseShellExecute = false });
        foreach (var w in windows.ToList()) w.Close();
    }
}

/// <summary>Ярлыки и регистрация браузером по умолчанию (только для текущего пользователя).</summary>
static class SystemIntegration
{
    static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "LiteBrowser");
    static string Exe => Path.Combine(InstallDir, "LiteBrowser.exe");

    public static bool IsInstalled => Environment.ProcessPath!.Equals(Exe, StringComparison.OrdinalIgnoreCase);

    /// <summary>Если браузер запущен, например, из «Загрузок» — копируем его в папку программ, чтобы ярлыки и ссылки не сломались.</summary>
    public static void EnsureInstalled()
    {
        if (IsInstalled) return;
        Directory.CreateDirectory(InstallDir);
        if (File.Exists(Exe))
        {
            var old = Path.ChangeExtension(Exe, ".old");
            try { if (File.Exists(old)) File.Delete(old); File.Move(Exe, old); } catch { }
        }
        File.Copy(Environment.ProcessPath!, Exe, true);
    }

    public static bool IsDefaultBrowser()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
            return (k?.GetValue("ProgId") as string) == "LiteBrowserURL";
        }
        catch { return false; }
    }

    public static void CreateShortcuts()
    {
        EnsureInstalled();
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell недоступен");
        dynamic shell = Activator.CreateInstance(shellType)!;
        foreach (var dir in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                     Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                 })
        {
            dynamic lnk = shell.CreateShortcut(Path.Combine(dir, "LiteBrowser.lnk"));
            lnk.TargetPath = Exe;
            lnk.WorkingDirectory = Path.GetDirectoryName(Exe);
            lnk.Description = "LiteBrowser — лёгкий и безопасный браузер";
            lnk.IconLocation = Exe + ",0";
            lnk.Save();
        }
    }

    public static void RegisterAsBrowser()
    {
        EnsureInstalled();
        if (!File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "LiteBrowser.lnk")))
            try { CreateShortcuts(); } catch { }
        string exe = Exe, open = $"\"{exe}\" \"%1\"", icon = exe + ",0";
        const string app = @"Software\Clients\StartMenuInternet\LiteBrowser";
        using (var k = Registry.CurrentUser.CreateSubKey(app)) k.SetValue("", "LiteBrowser");
        using (var k = Registry.CurrentUser.CreateSubKey(app + @"\DefaultIcon")) k.SetValue("", icon);
        using (var k = Registry.CurrentUser.CreateSubKey(app + @"\shell\open\command")) k.SetValue("", $"\"{exe}\"");
        using (var k = Registry.CurrentUser.CreateSubKey(app + @"\Capabilities"))
        {
            k.SetValue("ApplicationName", "LiteBrowser");
            k.SetValue("ApplicationDescription", "Лёгкий и безопасный браузер");
            k.SetValue("ApplicationIcon", icon);
        }
        using (var k = Registry.CurrentUser.CreateSubKey(app + @"\Capabilities\URLAssociations"))
        {
            k.SetValue("http", "LiteBrowserURL");
            k.SetValue("https", "LiteBrowserURL");
        }
        using (var k = Registry.CurrentUser.CreateSubKey(app + @"\Capabilities\FileAssociations"))
        {
            k.SetValue(".htm", "LiteBrowserHTML");
            k.SetValue(".html", "LiteBrowserHTML");
        }
        using (var k = Registry.CurrentUser.CreateSubKey(app + @"\Capabilities\StartMenu")) k.SetValue("StartMenuInternet", "LiteBrowser");
        using (var k = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications")) k.SetValue("LiteBrowser", app + @"\Capabilities");

        foreach (var (progId, name) in new[] { ("LiteBrowserURL", "LiteBrowser URL"), ("LiteBrowserHTML", "LiteBrowser HTML") })
        {
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + progId))
            {
                k.SetValue("", name);
                if (progId == "LiteBrowserURL") k.SetValue("URL Protocol", "");
            }
            using (var k = Registry.CurrentUser.CreateSubKey(@$"Software\Classes\{progId}\DefaultIcon")) k.SetValue("", icon);
            using (var k = Registry.CurrentUser.CreateSubKey(@$"Software\Classes\{progId}\shell\open\command")) k.SetValue("", open);
        }

        // Windows не даёт программам самим назначать браузер — открываем окно выбора
        Process.Start(new ProcessStartInfo("ms-settings:defaultapps?registeredAppUser=LiteBrowser") { UseShellExecute = true });
    }
}
