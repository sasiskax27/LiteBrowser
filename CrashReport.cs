using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace LiteBrowser;

/// <summary>
/// Отчёты о сбоях: ошибка записывается в файл в папке данных браузера (папка crashes), браузер продолжает работать.
/// В отчёте — версия, Windows, движок и текст ошибки. Ни адресов страниц, ни имени пользователя (путь к профилю заменяется на %USERPROFILE%).
/// Никуда сам не отправляется: пользователь может открыть его и, если хочет, создать обращение на GitHub.
/// </summary>
static class CrashReport
{
    static int shownThisSession;
    static string Dir => Path.Combine(AppEnv.DataDir, "crashes");

    public static void Install()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        // Ошибка в интерфейсе — записываем и работаем дальше
        Application.ThreadException += (_, e) => { var f = Write(e.Exception, "ui"); Notify(f); };
        // Ошибка в фоновом потоке — процесс завершится; отчёт успеем записать, при следующем запуске предложим его
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) Write(ex, "fatal"); };
        // Забытая ошибка в задаче — только в отчёт
        TaskScheduler.UnobservedTaskException += (_, e) => { Write(e.Exception, "task"); e.SetObserved(); };
    }

    /// <summary>Записать отчёт. Возвращает путь к файлу (или null, если записать не удалось).</summary>
    public static string? Write(Exception ex, string kind, string? extra = null)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            // Храним 20 последних отчётов
            foreach (var old in new DirectoryInfo(Dir).GetFiles("crash-*.txt").OrderByDescending(f => f.LastWriteTimeUtc).Skip(19)) try { old.Delete(); } catch { }
            var path = Path.Combine(Dir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}-{kind}.txt");
            File.WriteAllText(path, Build(ex, kind, extra), new UTF8Encoding(false));
            return path;
        }
        catch { return null; }
    }

    public static string Build(Exception ex, string kind, string? extra = null)
    {
        string engine = "";
        try { engine = AppEnv.GetEnvironment() is { IsCompletedSuccessfully: true } t ? t.Result.BrowserVersionString : ""; } catch { }
        var sb = new StringBuilder();
        sb.AppendLine($"LiteBrowser {AppEnv.Version} ({(Updater.IsSelfContained ? "full" : "small")})");
        sb.AppendLine($"Windows: {Environment.OSVersion.Version} {RuntimeInformation.OSArchitecture}, .NET {Environment.Version}");
        sb.AppendLine($"Движок: WebView2 {engine}");
        sb.AppendLine($"Тип: {kind}, время: {DateTime.Now:yyyy-MM-dd HH:mm:ss}, работал: {(DateTime.Now - Process.GetCurrentProcess().StartTime):hh\\:mm\\:ss}");
        if (extra != null) sb.AppendLine(extra);
        sb.AppendLine();
        sb.AppendLine(Sanitize(ex.ToString()));
        return sb.ToString();
    }

    /// <summary>Убираем личное: путь к профилю пользователя и имя пользователя.</summary>
    public static string Sanitize(string s)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 3) s = s.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        var user = Environment.UserName;
        if (user.Length > 2) s = System.Text.RegularExpressions.Regex.Replace(s, $@"(?<![\w]){System.Text.RegularExpressions.Regex.Escape(user)}(?![\w])", "%USERNAME%", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return s;
    }

    /// <summary>
    /// Отчёт о сбое, из-за которого браузер закрылся в прошлый раз (ещё не показанный).
    /// Найденный файл переименовывается в «…-seen», чтобы не предлагать его снова.
    /// </summary>
    public static string? TakeUnseenFatal()
    {
        try
        {
            if (!Directory.Exists(Dir)) return null;
            var f = new DirectoryInfo(Dir).GetFiles("crash-*-fatal.txt").OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
            if (f == null) return null;
            var seen = Path.ChangeExtension(f.FullName, null) + "-seen.txt";
            f.MoveTo(seen, true);
            return seen;
        }
        catch { return null; }
    }

    static void Notify(string? file)
    {
        // Не надоедаем: одно окно за сеанс, дальше — только запись в файл
        if (file == null || Interlocked.Increment(ref shownThisSession) > 1) return;
        try { Ask(file, L.T("В LiteBrowser произошла ошибка, но он продолжает работать.")); } catch { }
    }

    /// <summary>Окно «Произошла ошибка»: открыть отчёт или сообщить разработчику (откроется страница обращения — отправляете вы сами).</summary>
    public static void Ask(string file, string message)
    {
        var r = MessageBox.Show(message + "\n\n" + L.T("Отчёт сохранён на этом компьютере (без адресов сайтов и личных данных). Открыть страницу, чтобы сообщить о проблеме разработчику? Отчёт будет вставлен в обращение — вы увидите его перед отправкой."),
            "LiteBrowser", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (r == DialogResult.Yes) OpenIssue(file);
        else if (r == DialogResult.No) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
    }

    /// <summary>Страница нового обращения на GitHub с отчётом (обрезанным, чтобы ссылка не была слишком длинной).</summary>
    public static string IssueUrl(string report)
    {
        var lines = report.Split('\n');
        var title = "Сбой: " + (lines.FirstOrDefault(l => l.Contains("Exception"))?.Trim() ?? "ошибка");
        if (title.Length > 120) title = title[..120];
        var body = report.Length > 5500 ? report[..5500] + "\n…" : report;
        body = "**Что делали перед сбоем:**\n\n\n**Отчёт:**\n```\n" + body + "\n```";
        return $"https://github.com/{Updater.Repo}/issues/new?title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(body)}";
    }

    static void OpenIssue(string file)
    {
        var url = IssueUrl(File.ReadAllText(file));
        // Открываем в самом браузере, если окно есть; иначе — в системном
        var w = Application.OpenForms.OfType<MainForm>().FirstOrDefault(f => !f.Incognito);
        if (w != null) w.OpenFromOutside(url);
        else Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
