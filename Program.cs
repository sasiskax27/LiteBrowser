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
        bool restore = args.Contains("--restore");
        bool incognito = args.Contains("--incognito");
        string? url = args.FirstOrDefault(a => !a.StartsWith("--"));

        // Если браузер уже открыт — передаём ему ссылку и выходим
        using var mutex = new Mutex(true, "LiteBrowser-" + Environment.UserName, out bool first);
        if (!first && !restore && SingleInstance.Send((incognito ? "incognito" : "normal") + "\n" + (url ?? "")))
            return;

        ApplicationConfiguration.Initialize();
        AppEnv.Init();
        Application.Run(new BrowserApp(url, incognito, restore));
    }
}

static class SingleInstance
{
    static string PipeName => "LiteBrowser-" + Environment.UserName;

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
        flush.Tick += (_, _) => Store.Flush();
        flush.Start();
        SingleInstance.Listen(m => invoker.BeginInvoke(() => OnRemote(m)));

        List<string>? tabs = null;
        int active = 0;
        if (url == null && !incognito && (restore || AppEnv.Settings.RestoreSession))
        {
            var s = Store.LoadSession();
            if (s is { Tabs.Count: > 0 }) { tabs = s.Tabs; active = s.Active; }
        }
        OpenWindow(incognito, url, tabs, active);

        if (AppEnv.Settings.BlockAds && AppEnv.Settings.ExtendedBlocklist)
            _ = Blocklist.UpdateAsync(AppEnv.DataDir, AppEnv.Settings, force: false);
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

    public MainForm OpenWindow(bool incognito, string? url = null, List<string>? tabs = null, int active = 0)
    {
        var f = new MainForm(incognito, url, tabs, active);
        windows.Add(f);
        f.Activated += (_, _) => lastActive = f;
        f.FormClosed += (_, _) =>
        {
            windows.Remove(f);
            if (lastActive == f) lastActive = windows.LastOrDefault();
            if (windows.Count > 0) return;
            Store.Flush();
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
    static string Exe => Environment.ProcessPath!;

    public static void CreateShortcuts()
    {
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
