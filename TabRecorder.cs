using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace LiteBrowser;

/// <summary>
/// Запись видео вкладки: кадры берём из движка (DevTools «screencast»), сайт об этом не знает и ничего не может подменить.
/// Пишем AVI с кадрами JPEG (MJPEG) — открывается в «Кино и ТВ», VLC, любом редакторе. Без звука.
/// Кадры приходят только когда страница меняется, поэтому раз в 1/12 секунды повторяем последний.
/// </summary>
sealed class TabRecorder : IDisposable
{
    public const int Fps = 12;
    const long MaxBytes = 1_500_000_000;          // ~1,5 ГБ — дальше AVI 1.0 не стоит
    public static readonly TimeSpan MaxLength = TimeSpan.FromMinutes(30);

    readonly CoreWebView2 core;
    readonly FileStream fs;
    readonly BinaryWriter w;
    readonly List<(long offset, int size)> index = new();
    readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 / Fps };
    readonly DateTime started = DateTime.Now;
    byte[]? last;
    int width, height;
    long moviStart, riffSizePos, moviSizePos, framesPos, lengthPos, avihSizePos, strhSizePos, strfSizePos;
    bool stopped;

    public string FilePath { get; }
    public TimeSpan Elapsed => DateTime.Now - started;
    public int Frames => index.Count;
    /// <summary>Запись остановилась сама (лимит длины/размера) — окно покажет, куда сохранено.</summary>
    public event Action<TabRecorder>? AutoStopped;

    TabRecorder(CoreWebView2 core, string path)
    {
        this.core = core;
        FilePath = path;
        fs = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        w = new BinaryWriter(fs);
        timer.Tick += (_, _) => Tick();
    }

    public static string DefaultFolder()
    {
        var dir = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (string.IsNullOrEmpty(dir)) dir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(dir, "LiteBrowser");
    }

    public static async Task<TabRecorder> StartAsync(CoreWebView2 core, string? folder = null)
    {
        var dir = folder ?? DefaultFolder();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{L.T("Запись вкладки")} {DateTime.Now:yyyy-MM-dd HH-mm-ss}.avi");
        var r = new TabRecorder(core, path);
        r.WriteHeader();
        core.GetDevToolsProtocolEventReceiver("Page.screencastFrame").DevToolsProtocolEventReceived += r.OnFrame;
        await core.CallDevToolsProtocolMethodAsync("Page.startScreencast",
            JsonSerializer.Serialize(new { format = "jpeg", quality = 72, maxWidth = 1600, maxHeight = 1000, everyNthFrame = 1 }));
        r.timer.Start();
        return r;
    }

    void OnFrame(object? s, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        if (stopped) return;
        try
        {
            using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
            var root = doc.RootElement;
            int session = root.GetProperty("sessionId").GetInt32();
            _ = core.CallDevToolsProtocolMethodAsync("Page.screencastFrameAck", JsonSerializer.Serialize(new { sessionId = session }));
            last = Convert.FromBase64String(root.GetProperty("data").GetString() ?? "");
            if (width == 0 && root.TryGetProperty("metadata", out var m))
            {
                width = (int)Math.Round(m.GetProperty("deviceWidth").GetDouble());
                height = (int)Math.Round(m.GetProperty("deviceHeight").GetDouble());
                if (JpegSize(last) is { } sz) (width, height) = sz;
            }
        }
        catch { }
    }

    void Tick()
    {
        if (stopped || last == null) return;
        // Каждый тик — кадр: пауза на странице становится паузой в видео, а не «перемоткой»
        long off = fs.Position;
        w.Write(Encoding.ASCII.GetBytes("00dc"));
        w.Write(last.Length);
        w.Write(last);
        if ((last.Length & 1) == 1) w.Write((byte)0);
        index.Add((off - moviStart, last.Length));
        if (fs.Length > MaxBytes || Elapsed > MaxLength) { timer.Stop(); AutoStopped?.Invoke(this); }
    }

    public async Task<string> StopAsync()
    {
        if (stopped) return FilePath;
        stopped = true;
        timer.Stop();
        try { core.GetDevToolsProtocolEventReceiver("Page.screencastFrame").DevToolsProtocolEventReceived -= OnFrame; } catch { }
        try { await core.CallDevToolsProtocolMethodAsync("Page.stopScreencast", "{}"); } catch { }
        Finish();
        return FilePath;
    }

    // ---------------- AVI (RIFF) ----------------

    void Fourcc(string s) => w.Write(Encoding.ASCII.GetBytes(s));
    long SizePlaceholder() { long p = fs.Position; w.Write(0); return p; }

    void WriteHeader()
    {
        Fourcc("RIFF"); riffSizePos = SizePlaceholder(); Fourcc("AVI ");
        Fourcc("LIST"); long hdrl = SizePlaceholder(); Fourcc("hdrl");
        Fourcc("avih"); w.Write(56);
        avihSizePos = fs.Position;
        w.Write(1_000_000 / Fps);   // мкс на кадр
        w.Write(0);                 // макс. байт/с
        w.Write(0);                 // выравнивание
        w.Write(0x10);              // AVIF_HASINDEX
        framesPos = fs.Position; w.Write(0); // всего кадров
        w.Write(0);                 // начальные кадры
        w.Write(1);                 // потоков
        w.Write(0);                 // буфер
        w.Write(0); w.Write(0);     // ширина, высота (допишем)
        w.Write(0); w.Write(0); w.Write(0); w.Write(0);
        Fourcc("LIST"); long strl = SizePlaceholder(); Fourcc("strl");
        Fourcc("strh"); w.Write(56);
        strhSizePos = fs.Position;
        Fourcc("vids"); Fourcc("MJPG");
        w.Write(0); w.Write((short)0); w.Write((short)0); w.Write(0);
        w.Write(1); w.Write(Fps);   // scale, rate → 12 кадров/с
        w.Write(0);
        lengthPos = fs.Position; w.Write(0); // длина в кадрах
        w.Write(0); w.Write(-1); w.Write(0);
        w.Write((short)0); w.Write((short)0); w.Write((short)0); w.Write((short)0); // rcFrame (допишем)
        Fourcc("strf"); w.Write(40);
        strfSizePos = fs.Position;
        w.Write(40); w.Write(0); w.Write(0); w.Write((short)1); w.Write((short)24);
        Fourcc("MJPG"); w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
        Patch(strl, fs.Position - strl - 4);
        Patch(hdrl, fs.Position - hdrl - 4);
        Fourcc("LIST"); moviSizePos = SizePlaceholder(); moviStart = fs.Position; Fourcc("movi");
    }

    void Patch(long pos, long value)
    {
        long here = fs.Position;
        fs.Position = pos; w.Write((int)value);
        fs.Position = here;
    }

    void Finish()
    {
        try
        {
            Patch(moviSizePos, fs.Position - moviSizePos - 4);
            Fourcc("idx1"); w.Write(index.Count * 16);
            foreach (var (off, size) in index) { Fourcc("00dc"); w.Write(0x10); w.Write((int)off); w.Write(size); }
            Patch(riffSizePos, fs.Position - 8);
            Patch(framesPos, index.Count);
            Patch(lengthPos, index.Count);
            // Размер кадра
            long here = fs.Position;
            fs.Position = avihSizePos + 32; w.Write(width); w.Write(height);
            fs.Position = strhSizePos + 52; w.Write((short)width); w.Write((short)height);
            fs.Position = strfSizePos + 4; w.Write(width); w.Write(height);
            fs.Position = strfSizePos + 20; w.Write(width * height * 3);
            fs.Position = here;
            w.Flush();
        }
        finally { w.Dispose(); }
        // Пустая запись (страница так и не отрисовалась) — не оставляем мусор
        if (index.Count == 0) try { File.Delete(FilePath); } catch { }
    }

    /// <summary>Размер картинки из заголовка JPEG (маркер SOF).</summary>
    static (int w, int h)? JpegSize(byte[] b)
    {
        int i = 2;
        while (i + 9 < b.Length)
        {
            if (b[i] != 0xFF) { i++; continue; }
            byte m = b[i + 1];
            int len = (b[i + 2] << 8) | b[i + 3];
            if (m is >= 0xC0 and <= 0xCF && m is not (0xC4 or 0xC8 or 0xCC))
                return ((b[i + 7] << 8) | b[i + 8], (b[i + 5] << 8) | b[i + 6]);
            i += 2 + len;
        }
        return null;
    }

    public void Dispose()
    {
        if (!stopped) { stopped = true; timer.Stop(); try { Finish(); } catch { } }
        timer.Dispose();
    }
}
