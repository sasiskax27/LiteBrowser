using System.Runtime.InteropServices;
using System.Text;

namespace LiteBrowser;

/// <summary>
/// Онлайн-голос (Google), если в Windows нет голоса нужного языка — например, русского.
/// Включается только с согласия: текст страницы уходит в Google. Звук играет системный проигрыватель Windows (MCI) — без библиотек.
/// Следующий кусок скачивается, пока звучит текущий, поэтому пауз между предложениями почти нет.
/// </summary>
sealed class OnlineTts : IDisposable
{
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    static extern int mciSendString(string command, StringBuilder? ret, int retLen, IntPtr hwnd);

    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(20) };
    readonly List<string> chunks;
    readonly string lang;
    readonly string dir = Path.Combine(Path.GetTempPath(), "LiteBrowserTts-" + Guid.NewGuid().ToString("N")[..8]);
    readonly CancellationTokenSource cts = new();
    volatile bool paused;
    const string Alias = "litetts";
    public event Action? Finished;
    public event Action<string>? Failed;

    public OnlineTts(IEnumerable<string> parts, string lang)
    {
        this.lang = lang.Length >= 2 ? lang[..2] : "ru";
        chunks = Split(parts).ToList();
    }

    /// <summary>Куски не длиннее 190 символов (ограничение сервиса), по границам предложений и слов.</summary>
    internal static IEnumerable<string> Split(IEnumerable<string> parts)
    {
        foreach (var raw in parts)
        {
            var p = raw.Trim();
            while (p.Length > 190)
            {
                int cut = p.LastIndexOfAny(new[] { '.', '!', '?', ';', ',', ' ' }, 189);
                if (cut < 60) cut = 189;
                yield return p[..(cut + 1)].Trim();
                p = p[(cut + 1)..].Trim();
            }
            if (p.Length > 0) yield return p;
        }
    }

    public void Start() => _ = Task.Run(Run);

    async Task Run()
    {
        Directory.CreateDirectory(dir);
        try
        {
            Task<string?>? next = chunks.Count > 0 ? Fetch(0) : null;
            for (int i = 0; i < chunks.Count && !cts.IsCancellationRequested; i++)
            {
                var file = await next!;
                next = i + 1 < chunks.Count ? Fetch(i + 1) : null;
                if (file == null) continue;
                Play(file);
                try { File.Delete(file); } catch { }
            }
            if (!cts.IsCancellationRequested) Finished?.Invoke();
        }
        catch (Exception ex) { if (!cts.IsCancellationRequested) Failed?.Invoke(ex.Message); }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    async Task<string?> Fetch(int i)
    {
        var url = $"https://translate.google.com/translate_tts?ie=UTF-8&client=tw-ob&tl={lang}&q={Uri.EscapeDataString(chunks[i])}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/157.0 Safari/537.36");
        using var resp = await http.SendAsync(req, cts.Token);
        if ((int)resp.StatusCode == 429) throw new InvalidOperationException(L.T("Онлайн-голос временно недоступен (слишком много запросов)"));
        resp.EnsureSuccessStatusCode();
        var path = Path.Combine(dir, $"{i}.mp3");
        await File.WriteAllBytesAsync(path, await resp.Content.ReadAsByteArrayAsync(cts.Token), cts.Token);
        return path;
    }

    void Play(string file)
    {
        mciSendString($"open \"{file}\" type mpegvideo alias {Alias}", null, 0, IntPtr.Zero);
        mciSendString($"play {Alias}", null, 0, IntPtr.Zero);
        var sb = new StringBuilder(64);
        while (!cts.IsCancellationRequested)
        {
            Thread.Sleep(80);
            if (paused) continue;
            sb.Clear();
            mciSendString($"status {Alias} mode", sb, sb.Capacity, IntPtr.Zero);
            var mode = sb.ToString();
            if (mode is "stopped" or "" || mode.StartsWith("not")) break;
        }
        mciSendString($"close {Alias}", null, 0, IntPtr.Zero);
    }

    public void Pause() { paused = true; mciSendString($"pause {Alias}", null, 0, IntPtr.Zero); }
    public void Resume() { paused = false; mciSendString($"resume {Alias}", null, 0, IntPtr.Zero); }

    public void Dispose()
    {
        cts.Cancel();
        mciSendString($"stop {Alias}", null, 0, IntPtr.Zero);
        mciSendString($"close {Alias}", null, 0, IntPtr.Zero);
    }
}
