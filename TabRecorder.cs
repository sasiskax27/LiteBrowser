using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace LiteBrowser;

/// <summary>
/// Запись видео вкладки: кадры берём из движка (DevTools «screencast»), сайт об этом не знает и ничего не может подменить.
/// Основной формат — MP4 (H.264) со звуком браузера через Media Foundation Windows.
/// Если Media Foundation недоступен (урезанные редакции Windows N) — AVI с кадрами JPEG, без звука.
/// Кадры приходят, только когда страница меняется, поэтому повторяем последний с постоянной частотой.
/// </summary>
sealed class TabRecorder : IDisposable
{
    public const int AviFps = 12, Mp4Fps = 15;
    const long MaxBytes = 1_500_000_000;          // ~1,5 ГБ — дальше AVI 1.0 не стоит
    public static readonly TimeSpan MaxLength = TimeSpan.FromMinutes(30);

    readonly CoreWebView2 core;
    FileStream fs = null!;
    BinaryWriter w = null!;
    readonly List<(long offset, int size)> index = new();
    readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 / AviFps };
    readonly DateTime started = DateTime.Now;
    readonly TaskCompletionSource firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
    volatile byte[]? last;
    int width, height;
    long moviStart, riffSizePos, moviSizePos, framesPos, lengthPos, avihSizePos, strhSizePos, strfSizePos;
    volatile bool stopped;

    // MP4
    Mp4Writer? mp4;
    ProcessAudioCapture? audio;
    Thread? videoThread;
    long mp4Frames;
    byte[] rgb = Array.Empty<byte>();

    public string FilePath { get; private set; } = "";
    public TimeSpan Elapsed => DateTime.Now - started;
    public long Frames => mp4 != null ? mp4Frames : index.Count;
    public bool IsMp4 => mp4 != null;
    public bool HasAudio { get; private set; }
    /// <summary>Почему нет звука (пусто — звук пишется).</summary>
    public string AudioNote { get; private set; } = "";
    /// <summary>Запись остановилась сама (лимит длины/размера) — окно покажет, куда сохранено.</summary>
    public event Action<TabRecorder>? AutoStopped;

    TabRecorder(CoreWebView2 core)
    {
        this.core = core;
        timer.Tick += (_, _) => Tick();
    }

    public static string DefaultFolder()
    {
        var dir = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (string.IsNullOrEmpty(dir)) dir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(dir, "LiteBrowser");
    }

    public static async Task<TabRecorder> StartAsync(CoreWebView2 core, string? folder = null, bool preferMp4 = true, bool withAudio = true)
    {
        var dir = folder ?? DefaultFolder();
        Directory.CreateDirectory(dir);
        var name = Path.Combine(dir, $"{L.T("Запись вкладки")} {DateTime.Now:yyyy-MM-dd HH-mm-ss}");
        var r = new TabRecorder(core);
        core.GetDevToolsProtocolEventReceiver("Page.screencastFrame").DevToolsProtocolEventReceived += r.OnFrame;
        await core.CallDevToolsProtocolMethodAsync("Page.startScreencast",
            JsonSerializer.Serialize(new { format = "jpeg", quality = 80, maxWidth = 1920, maxHeight = 1080, everyNthFrame = 1 }));
        // Размер видео узнаём по первому кадру
        await Task.WhenAny(r.firstFrame.Task, Task.Delay(4000));
        if (r.last == null) { await r.StopAsync(); throw new InvalidOperationException(L.T("страница не отрисовалась")); }
        if (preferMp4)
        {
            try { r.StartMp4(name + ".mp4", withAudio); return r; }
            catch { r.mp4?.Dispose(); r.mp4 = null; try { File.Delete(name + ".mp4"); } catch { } }
        }
        r.FilePath = name + ".avi";
        r.fs = new FileStream(r.FilePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        r.w = new BinaryWriter(r.fs);
        r.WriteHeader();
        r.timer.Start();
        return r;
    }

    void StartMp4(string path, bool withAudio)
    {
        // Чётные размеры (требование H.264), не больше Full HD
        int vw = Math.Min(width, 1920) & ~1, vh = Math.Min(height, 1080) & ~1;
        mp4 = new Mp4Writer(path, vw, vh, Mp4Fps, withAudio);
        FilePath = path;
        rgb = new byte[mp4.Width * mp4.Height * 4];
        if (withAudio && mp4.HasAudio)
        {
            // Звук движка браузера (все вкладки; процессы движка — дочерние у его основного процесса)
            audio = ProcessAudioCapture.TryStart((pcm, at) => { try { mp4?.WriteAudio(pcm, at); } catch { } }, out var err, (int)core.BrowserProcessId);
            if (audio == null) AudioNote = err; else HasAudio = true;
        }
        else if (withAudio) AudioNote = L.T("кодировщик звука недоступен");
        videoThread = new Thread(VideoLoop) { IsBackground = true, Name = "LiteBrowser video" };
        videoThread.Start();
    }

    void VideoLoop()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        byte[]? shown = null;
        while (!stopped)
        {
            try
            {
                long due = sw.ElapsedMilliseconds * Mp4Fps / 1000 + 1;
                while (mp4Frames < due && !stopped)
                {
                    var cur = last;
                    if (cur != null && cur != shown) { Decode(cur); shown = cur; }
                    mp4!.WriteVideo(rgb, mp4Frames++);
                }
                if (Elapsed > MaxLength) { AutoStopped?.Invoke(this); return; }
            }
            catch { }
            Thread.Sleep(10);
        }
    }

    /// <summary>JPEG → RGB32 нужного размера (если окно меняли — кадр масштабируется).</summary>
    void Decode(byte[] jpeg)
    {
        int vw = mp4!.Width, vh = mp4.Height;
        using var ms = new MemoryStream(jpeg);
        using var src = Image.FromStream(ms, false, false);
        using var bmp = new Bitmap(vw, vh, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.Clear(Color.Black);
            float k = Math.Min((float)vw / src.Width, (float)vh / src.Height);
            int dw = (int)(src.Width * k), dh = (int)(src.Height * k);
            g.DrawImage(src, (vw - dw) / 2, (vh - dh) / 2, dw, dh);
        }
        var data = bmp.LockBits(new Rectangle(0, 0, vw, vh), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        try
        {
            for (int y = 0; y < vh; y++)
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, rgb, y * vw * 4, vw * 4);
        }
        finally { bmp.UnlockBits(data); }
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
            var bytes = Convert.FromBase64String(root.GetProperty("data").GetString() ?? "");
            if (width == 0 && JpegSize(bytes) is { } sz) (width, height) = sz;
            last = bytes;
            firstFrame.TrySetResult();
        }
        catch { }
    }

    void Tick()
    {
        var cur = last;
        if (stopped || cur == null || w == null) return;
        // Каждый тик — кадр: пауза на странице становится паузой в видео, а не «перемоткой»
        long off = fs.Position;
        w.Write(Encoding.ASCII.GetBytes("00dc"));
        w.Write(cur.Length);
        w.Write(cur);
        if ((cur.Length & 1) == 1) w.Write((byte)0);
        index.Add((off - moviStart, cur.Length));
        if (fs.Length > MaxBytes || Elapsed > MaxLength) { timer.Stop(); AutoStopped?.Invoke(this); }
    }

    public async Task<string> StopAsync()
    {
        if (stopped) return FilePath;
        stopped = true;
        timer.Stop();
        try { core.GetDevToolsProtocolEventReceiver("Page.screencastFrame").DevToolsProtocolEventReceived -= OnFrame; } catch { }
        try { await core.CallDevToolsProtocolMethodAsync("Page.stopScreencast", "{}"); } catch { }
        await Task.Run(FinishAll);
        return FilePath;
    }

    void FinishAll()
    {
        if (mp4 != null)
        {
            try { videoThread?.Join(2000); } catch { }
            audio?.Dispose(); audio = null;
            mp4.Dispose();
            if (mp4Frames == 0) try { File.Delete(FilePath); } catch { }
        }
        else if (w != null) Finish();
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
        w.Write(1_000_000 / AviFps);   // мкс на кадр
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
        w.Write(1); w.Write(AviFps);   // scale, rate → 12 кадров/с
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
        if (!stopped) { stopped = true; timer.Stop(); try { FinishAll(); } catch { } }
        timer.Dispose();
    }
}
