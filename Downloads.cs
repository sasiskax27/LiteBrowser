using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Web.WebView2.Core;

namespace LiteBrowser;

enum DlState { InProgress, Completed, Interrupted }

sealed class DownloadItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Url { get; set; } = "";
    public string Path { get; set; } = "";
    public long Received { get; set; }
    public long Total { get; set; }
    public DlState State { get; set; }
    public bool Canceled { get; set; }
    public DateTime Time { get; set; } = DateTime.Now;
    [JsonIgnore] public string Name => System.IO.Path.GetFileName(Path);
    [JsonIgnore] public double Speed { get; set; }
    [JsonIgnore] public CoreWebView2DownloadOperation? Op { get; set; }
    [JsonIgnore] public CancellationTokenSource? Cts { get; set; }
    [JsonIgnore] public bool Paused { get; set; }
    // Прямая загрузка: части файла (начало, конец, сколько скачано) — для нескольких потоков и продолжения после паузы
    [JsonIgnore] internal List<long[]>? Segments { get; set; }
    [JsonIgnore] internal bool RangeOk { get; set; }
    [JsonIgnore] internal string? Referer { get; set; }
    [JsonIgnore] internal string? Cookies { get; set; }
    [JsonIgnore] public int Threads => Segments?.Count ?? 1;
    /// <summary>Можно ли поставить на паузу: загрузки движка — всегда, прямые — если сервер отдаёт части файла.</summary>
    [JsonIgnore] public bool CanPause => State == DlState.InProgress && (Op != null || RangeOk);
    long lastBytes;
    DateTime lastTime = DateTime.Now;

    static long L(object? v) { try { return v == null ? 0 : Convert.ToInt64(v); } catch { return 0; } }

    public void Cancel()
    {
        Paused = false;
        if (Op != null) Op.Cancel();
        Cts?.Cancel();
        // Прямая загрузка на паузе — потоков уже нет, убираем недокачанный файл сами
        if (Op == null && State == DlState.InProgress && Cts is { IsCancellationRequested: true } && Segments != null)
        {
            State = DlState.Interrupted; Canceled = true;
            try { File.Delete(Path); } catch { }
        }
    }

    public void Pause()
    {
        if (!CanPause || Paused) return;
        Paused = true;
        Speed = 0;
        if (Op != null) { try { Op.Pause(); } catch { } }
        else Cts?.Cancel();
    }

    public void Resume()
    {
        if (!Paused) return;
        Paused = false;
        if (Op != null) { try { if (Op.CanResume) Op.Resume(); } catch { } }
        else Downloads.ResumeDirect(this);
    }

    public void UpdateFromOp()
    {
        if (Op == null) return;
        Received = L(Op.BytesReceived);
        Total = L(Op.TotalBytesToReceive);
        Path = Op.ResultFilePath;
        State = Op.State switch
        {
            CoreWebView2DownloadState.Completed => DlState.Completed,
            CoreWebView2DownloadState.Interrupted => DlState.Interrupted,
            _ => DlState.InProgress,
        };
        Canceled = State == DlState.Interrupted && Op.InterruptReason.ToString() == "UserCanceled";
        Tick();
    }

    public void Tick()
    {
        var now = DateTime.Now;
        double dt = (now - lastTime).TotalSeconds;
        if (dt < 0.5) return;
        Speed = Speed * 0.5 + (Received - lastBytes) / dt * 0.5;
        lastBytes = Received;
        lastTime = now;
    }
}

/// <summary>Общий для всех окон список загрузок; история сохраняется в downloads.json.</summary>
static class Downloads
{
    public static readonly List<DownloadItem> Items = new();
    public static event Action? Changed;
    static bool loaded;
    static string FilePath => System.IO.Path.Combine(AppEnv.DataDir, "downloads.json");
    public static string DefaultFolder => FolderOverride ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    /// <summary>Для самотеста: не трогаем папку «Загрузки» пользователя.</summary>
    internal static string? FolderOverride;

    public static void Load()
    {
        if (loaded) return;
        loaded = true;
        try
        {
            if (!File.Exists(FilePath)) return;
            var list = JsonSerializer.Deserialize<List<DownloadItem>>(File.ReadAllText(FilePath), AppSettings.Json) ?? new();
            foreach (var d in list) if (d.State == DlState.InProgress) d.State = DlState.Interrupted;
            Items.AddRange(list.Take(500));
        }
        catch { }
    }

    static void Save()
    {
        try
        {
            var done = Items.Where(i => i.State != DlState.InProgress).Take(500).ToList();
            File.WriteAllText(FilePath, JsonSerializer.Serialize(done, AppSettings.Json));
        }
        catch { }
    }

    static void Raise(bool save = false)
    {
        if (save) Save();
        Changed?.Invoke();
    }

    static System.Windows.Forms.Timer? watchdog;

    public static void Track(CoreWebView2DownloadOperation op)
    {
        var it = new DownloadItem { Op = op, Url = op.Uri };
        it.UpdateFromOp();
        Items.Insert(0, it);
        AppEnv.Log($"Track: state={op.State} total={op.TotalBytesToReceive}");
        op.BytesReceivedChanged += (_, _) => { try { it.UpdateFromOp(); Raise(); } catch (Exception ex) { AppEnv.Log("Bytes ERR " + ex); } };
        op.StateChanged += (_, _) => { AppEnv.Log($"State: {op.State} {op.InterruptReason}"); Check(it); };
        Raise();
        // Сигнал о завершении от движка приходит не всегда — проверяем загрузки сами раз в секунду
        if (watchdog == null)
        {
            watchdog = new System.Windows.Forms.Timer { Interval = 1000 };
            watchdog.Tick += (_, _) =>
            {
                var active = Items.Where(i => i.Op != null && i.State == DlState.InProgress).ToList();
                foreach (var a in active) Check(a);
                if (!Items.Any(i => i.Op != null && i.State == DlState.InProgress)) watchdog.Stop();
            };
        }
        watchdog.Start();
    }

    /// <summary>Обновить состояние загрузки; если все байты получены и файл уже лежит на месте — она завершена.</summary>
    static void Check(DownloadItem it)
    {
        var before = it.State;
        try { it.UpdateFromOp(); } catch (Exception ex) { AppEnv.Log("Check ERR " + ex.Message); }
        if (it.State == DlState.InProgress && it.Total > 0 && it.Received >= it.Total && it.Path.Length > 0)
        {
            try
            {
                // Пока файл качается, Chromium пишет во временный .crdownload; итоговый файл появляется только после завершения
                var fi = new FileInfo(it.Path);
                if (fi.Exists && fi.Length == it.Total)
                {
                    it.State = DlState.Completed;
                    AppEnv.Log("Check: завершена по файлу на диске");
                }
            }
            catch { }
        }
        if (it.State != before) Raise(save: true);
    }

    /// <summary>Скачивание файла напрямую (видео и аудио со страницы). Cookies и Referer — от страницы, чтобы сайт отдал файл.</summary>
    public static DownloadItem StartDirect(string url, string? referer, string? cookies, string? suggestedName)
    {
        var name = MakeName(url, suggestedName);
        Directory.CreateDirectory(DefaultFolder);
        var path = Unique(System.IO.Path.Combine(DefaultFolder, name));
        var it = new DownloadItem { Url = url, Path = path, State = DlState.InProgress, Referer = referer, Cookies = cookies };
        Items.Insert(0, it);
        Raise();
        _ = Task.Run(() => RunDirect(it));
        return it;
    }

    internal static void ResumeDirect(DownloadItem it)
    {
        if (it.State != DlState.InProgress) return;
        _ = Task.Run(() => RunDirect(it));
    }

    /// <summary>Больше этого — качаем в несколько потоков (если сервер отдаёт части файла).</summary>
    internal const long MultiThreadMin = 8 * 1024 * 1024;
    internal const int MaxThreads = 4;

    static HttpClient MakeClient()
    {
        var s = AppEnv.Settings;
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None };
        if (s.ProxyMode == "all") handler.Proxy = new WebProxy($"{(s.ProxyType == "http" ? "http" : "socks5")}://{s.ProxyHost}:{s.ProxyPort}");
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    static HttpRequestMessage Req(DownloadItem it, long? from = null, long? to = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, it.Url);
        req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/157.0 Safari/537.36");
        if (it.Referer != null) req.Headers.Referrer = new Uri(it.Referer);
        if (!string.IsNullOrEmpty(it.Cookies)) req.Headers.TryAddWithoutValidation("Cookie", it.Cookies);
        if (from != null) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(from, to);
        return req;
    }

    /// <summary>
    /// Прямая загрузка: узнаём размер и поддержку частей (Range), большие файлы делим на 4 части и качаем параллельно
    /// в один файл; после паузы докачиваем только недостающее.
    /// </summary>
    static async Task RunDirect(DownloadItem it)
    {
        it.Cts = new CancellationTokenSource();
        var ct = it.Cts.Token;
        try
        {
            using var http = MakeClient();
            if (it.Segments == null)
            {
                // Пробный запрос первого байта: есть ли части и какой размер
                using var probe = await http.SendAsync(Req(it, 0, 0), HttpCompletionOption.ResponseHeadersRead, ct);
                long total = 0;
                if (probe.StatusCode == HttpStatusCode.PartialContent && probe.Content.Headers.ContentRange?.Length is long len)
                { it.RangeOk = true; total = len; }
                else if (probe.IsSuccessStatusCode) total = probe.Content.Headers.ContentLength ?? 0;
                else probe.EnsureSuccessStatusCode();
                it.Total = total;
                int parts = it.RangeOk && total >= MultiThreadMin ? MaxThreads : 1;
                it.Segments = new();
                if (it.RangeOk && total > 0)
                {
                    long chunk = total / parts;
                    for (int i = 0; i < parts; i++)
                    {
                        long start = i * chunk, end = i == parts - 1 ? total - 1 : (i + 1) * chunk - 1;
                        it.Segments.Add(new[] { start, end, 0L });
                    }
                }
                else it.Segments.Add(new[] { 0L, -1L, 0L });
            }

            using (var handle = File.OpenHandle(it.Path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read))
            {
                var lastUi = DateTime.Now;
                void Progress()
                {
                    it.Received = it.Segments!.Sum(x => x[2]);
                    if ((DateTime.Now - lastUi).TotalMilliseconds > 250) { lastUi = DateTime.Now; it.Tick(); Ui(() => Raise()); }
                }
                async Task Part(long[] seg)
                {
                    bool ranged = it.RangeOk;
                    long from = seg[0] + seg[2];
                    if (seg[1] >= 0 && from > seg[1]) return; // эта часть уже скачана
                    using var req = ranged ? Req(it, from, seg[1] >= 0 ? seg[1] : null) : Req(it);
                    using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                    resp.EnsureSuccessStatusCode();
                    if (ranged && resp.StatusCode != HttpStatusCode.PartialContent) throw new IOException("сервер перестал отдавать части файла");
                    if (!ranged) { seg[2] = 0; }
                    await using var src = await resp.Content.ReadAsStreamAsync(ct);
                    var buf = new byte[131072];
                    int n;
                    while ((n = await src.ReadAsync(buf, ct)) > 0)
                    {
                        long pos = seg[0] + seg[2];
                        if (seg[1] >= 0) n = (int)Math.Min(n, seg[1] - pos + 1);
                        if (n <= 0) break;
                        await RandomAccess.WriteAsync(handle, buf.AsMemory(0, n), pos, ct);
                        seg[2] += n;
                        Progress();
                    }
                }
                await Task.WhenAll(it.Segments!.Select(Part));
                it.Received = it.Segments!.Sum(x => x[2]);
                if (it.Total > 0 && it.Received < it.Total) throw new IOException("файл скачан не полностью");
                RandomAccess.SetLength(handle, it.Received);
            }
            it.State = DlState.Completed;
            it.Speed = 0;
            DownloadGuard.MarkOfTheWeb(it.Path, it.Url);
        }
        catch (Exception)
        {
            if (it.Paused) { Ui(() => Raise()); return; } // пауза: файл и прогресс остаются, продолжим с того же места
            it.State = DlState.Interrupted;
            it.Canceled = it.Cts!.IsCancellationRequested;
            try { File.Delete(it.Path); } catch { }
        }
        Ui(() => Raise(save: true));
    }

    static void Ui(Action a)
    {
        var f = Application.OpenForms.Count > 0 ? Application.OpenForms[0] : null;
        if (f != null && f.IsHandleCreated) f.BeginInvoke(a); else a();
    }

    static string MakeName(string url, string? suggested)
    {
        var name = suggested;
        if (string.IsNullOrWhiteSpace(name))
        {
            try { name = System.IO.Path.GetFileName(new Uri(url).AbsolutePath); } catch { }
        }
        if (string.IsNullOrWhiteSpace(name)) name = "download";
        foreach (var c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        if (name.Length > 150) name = name[..150];
        if (!System.IO.Path.HasExtension(name)) name += ".mp4";
        return name;
    }

    static string Unique(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = System.IO.Path.GetDirectoryName(path)!;
        var stem = System.IO.Path.GetFileNameWithoutExtension(path);
        var ext = System.IO.Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            var p = System.IO.Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(p)) return p;
        }
    }

    public static bool AnyActive => Items.Any(i => i.State == DlState.InProgress);

    /// <summary>Общий прогресс активных загрузок: -1 — нет активных.</summary>
    public static float Progress
    {
        get
        {
            var active = Items.Where(i => i.State == DlState.InProgress).ToList();
            if (active.Count == 0) return -1;
            long total = active.Sum(i => i.Total), got = active.Sum(i => i.Received);
            return total > 0 ? Math.Clamp((float)got / total, 0, 1) : 0.15f;
        }
    }

    public static DownloadItem? Get(string id) => Items.FirstOrDefault(i => i.Id == id);

    public static void Remove(string id)
    {
        Items.RemoveAll(i => i.Id == id && i.State != DlState.InProgress);
        Raise(save: true);
    }

    public static void Clear()
    {
        Items.RemoveAll(i => i.State != DlState.InProgress);
        Raise(save: true);
    }

    public static void Open(DownloadItem it)
    {
        if (it.State == DlState.Completed && File.Exists(it.Path))
            Process.Start(new ProcessStartInfo(it.Path) { UseShellExecute = true });
    }

    public static void ShowInFolder(DownloadItem? it)
    {
        if (it != null && File.Exists(it.Path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{it.Path}\"") { UseShellExecute = true });
        else
        {
            var dir = it != null && it.Path.Length > 0 ? System.IO.Path.GetDirectoryName(it.Path)! : DefaultFolder;
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{(Directory.Exists(dir) ? dir : DefaultFolder)}\"") { UseShellExecute = true });
        }
    }
}

/// <summary>Всплывающее окно последних загрузок.</summary>
sealed class DownloadsPopup : Form
{
    Font nameFont => Theme.GetFont("Segoe UI", 9.5f, FontStyle.Bold);
    Font infoFont => Theme.GetFont("Segoe UI", 8.5f);
    Font headFont => Theme.GetFont("Segoe UI", 11f, FontStyle.Bold);
    Font iconFont => Theme.GetFont(Theme.IconFont, 11f);
    Font bigIcon => Theme.GetFont(Theme.IconFont, 16f);
    Font linkFont => Theme.GetFont("Segoe UI", 9f);
    int hoverRow = -1, hoverButton = -1;
    bool hoverFolder, hoverAll;
    public DateTime HiddenAt { get; private set; }
    public Action? OpenAll;

    static int S(float v) => Theme.S(v);
    int HeadH => S(48);
    int RowH => S(64);
    const int MaxRows = 6;

    public DownloadsPopup()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.Menu;
        DoubleBuffered = true;
        KeyPreview = true;
        Deactivate += (_, _) => HidePopup();
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) HidePopup(); };
        Downloads.Changed += OnChanged;
    }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { Theme.StylePopup(Handle); } catch { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Downloads.Changed -= OnChanged;
        base.Dispose(disposing);
    }

    void OnChanged()
    {
        if (!Visible) return;
        ResizeToContent();
        Invalidate();
    }

    void HidePopup()
    {
        if (!Visible) return;
        HiddenAt = DateTime.Now;
        Hide();
    }

    void ResizeToContent()
    {
        int rows = Math.Max(1, Math.Min(MaxRows, Downloads.Items.Count));
        Size = new Size(S(400), HeadH + rows * RowH + S(44));
    }

    public void ShowBelow(Control anchor)
    {
        BackColor = Theme.Menu;
        ResizeToContent();
        var p = anchor.PointToScreen(new Point(anchor.Width, anchor.Height + S(6)));
        var screen = Screen.FromControl(anchor).WorkingArea;
        Location = new Point(Math.Max(screen.Left + 8, Math.Min(p.X - Width, screen.Right - Width - 8)), p.Y);
        if (!Visible) Show(anchor.FindForm());
        Activate();
        Invalidate();
    }

    Rectangle RowRect(int i) => new(S(6), HeadH + i * RowH, Width - S(12), RowH - S(4));
    Rectangle ButtonRect(int i) { var r = RowRect(i); return new Rectangle(r.Right - S(40), r.Y + (r.Height - S(32)) / 2, S(32), S(32)); }
    Rectangle FolderLink => new(Width - S(150), S(12), S(140), S(24));
    Rectangle AllLink => new(S(16), Height - S(38), Width - S(32), S(30));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        TextRenderer.DrawText(g, "Загрузки", headFont, new Rectangle(S(16), 0, S(200), HeadH), Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, "Открыть папку", linkFont, FolderLink, hoverFolder ? Theme.Text : Theme.Accent, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, "Все загрузки (Ctrl+J)", linkFont, AllLink, hoverAll ? Theme.Text : Theme.Accent, Theme.Center);

        if (Downloads.Items.Count == 0)
        {
            TextRenderer.DrawText(g, "Пока ничего не скачано", infoFont, new Rectangle(0, HeadH, Width, RowH), Theme.TextDim, Theme.Center);
            return;
        }

        for (int i = 0; i < Math.Min(MaxRows, Downloads.Items.Count); i++)
        {
            var it = Downloads.Items[i];
            var r = RowRect(i);
            if (i == hoverRow)
            {
                using var p = Theme.Round(r, S(10));
                using var b = new SolidBrush(Theme.Hover);
                g.FillPath(b, p);
            }
            bool done = it.State == DlState.Completed, active = it.State == DlState.InProgress;
            bool missing = done && !File.Exists(it.Path);
            TextRenderer.DrawText(g, "", bigIcon, new Rectangle(r.X + S(8), r.Y, S(36), r.Height), done && !missing ? Theme.Accent : active ? Theme.Text : Theme.TextDim, Theme.Center);
            int tx = r.X + S(52), tw = r.Width - S(52) - S(48);
            TextRenderer.DrawText(g, it.Name, nameFont, new Rectangle(tx, r.Y + S(10), tw, S(20)), it.Canceled || missing ? Theme.TextDim : Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            string info = active
                ? (it.Total > 0 ? $"{Theme.FormatSize(it.Received)} из {Theme.FormatSize(it.Total)}" : Theme.FormatSize(it.Received)) + (it.Speed > 1 ? $" · {Theme.FormatSize((long)it.Speed)}/с" : "")
                : missing ? "Файл удалён или перемещён"
                : done ? $"{Theme.FormatSize(Math.Max(it.Total, it.Received))} · нажмите, чтобы открыть"
                : it.Canceled ? "Отменено" : "Ошибка загрузки";
            TextRenderer.DrawText(g, info, infoFont, new Rectangle(tx, r.Y + S(31), tw, S(18)), !active && !done && !it.Canceled ? Theme.Danger : Theme.TextDim,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            if (active)
            {
                var bar = new RectangleF(tx, r.Bottom - S(9), tw, S(3));
                using var track = new SolidBrush(Theme.Line);
                g.FillRectangle(track, bar);
                float p = it.Total > 0 ? Math.Clamp((float)it.Received / it.Total, 0, 1) : 0.3f;
                using var fill = new LinearGradientBrush(new RectangleF(bar.X - 1, bar.Y, bar.Width + 2, bar.Height), Theme.Accent, Theme.Accent2, 0f);
                g.FillRectangle(fill, bar.X, bar.Y, bar.Width * p, bar.Height);
            }
            if (active || (done && !missing))
            {
                var br = ButtonRect(i);
                if (i == hoverRow && hoverButton == i)
                {
                    using var b = new SolidBrush(Theme.Pressed);
                    g.FillEllipse(b, br);
                }
                TextRenderer.DrawText(g, active ? "" : "", iconFont, br, Theme.Text, Theme.Center);
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int row = -1, btn = -1;
        for (int i = 0; i < Math.Min(MaxRows, Downloads.Items.Count); i++)
        {
            if (!RowRect(i).Contains(e.Location)) continue;
            row = i;
            if (ButtonRect(i).Contains(e.Location)) btn = i;
        }
        bool hf = FolderLink.Contains(e.Location), ha = AllLink.Contains(e.Location);
        Cursor = row >= 0 || hf || ha ? Cursors.Hand : Cursors.Default;
        if (row != hoverRow || btn != hoverButton || hf != hoverFolder || ha != hoverAll)
        {
            hoverRow = row; hoverButton = btn; hoverFolder = hf; hoverAll = ha;
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        try
        {
            if (hoverFolder) { Downloads.ShowInFolder(Downloads.Items.FirstOrDefault()); HidePopup(); return; }
            if (hoverAll) { HidePopup(); OpenAll?.Invoke(); return; }
            if (hoverRow < 0 || hoverRow >= Downloads.Items.Count) return;
            var it = Downloads.Items[hoverRow];
            if (it.State == DlState.InProgress) { if (hoverButton == hoverRow) it.Cancel(); return; }
            if (it.State != DlState.Completed || !File.Exists(it.Path)) return;
            if (hoverButton == hoverRow) Downloads.ShowInFolder(it); else Downloads.Open(it);
            HidePopup();
        }
        catch (Exception ex) { MessageBox.Show(this, "Не удалось открыть: " + ex.Message, "Загрузки"); }
    }
}
