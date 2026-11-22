using System.Diagnostics;

namespace LiteBrowser;

/// <summary>Замер этапов запуска. Пишет в файл, только если задана переменная LITEBROWSER_TRACE (путь к файлу).</summary>
static class StartupTrace
{
    static readonly string? file = Environment.GetEnvironmentVariable("LITEBROWSER_TRACE");
    static readonly DateTime start = Process.GetCurrentProcess().StartTime;
    static readonly List<string> lines = new();

    public static void Mark(string what)
    {
        if (file == null) return;
        lock (lines)
        {
            lines.Add($"{(DateTime.Now - start).TotalMilliseconds,7:0} мс  {what}");
            try { File.WriteAllLines(file, lines); } catch { }
        }
    }
}
