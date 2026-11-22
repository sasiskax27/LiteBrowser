using System.Runtime.InteropServices;

namespace LiteBrowser;

/// <summary>
/// Звук только самого браузера (основной процесс движка WebView2 и его дочерние), а не всего компьютера:
/// «петля процесса» WASAPI (Windows 10 2004+). Музыка из других программ и уведомления в запись не попадут.
/// Отдаёт PCM 16 бит, 48 кГц, стерео; тишину, когда звука нет, досыпает сама — чтобы звук не «уезжал» от видео.
/// </summary>
sealed class ProcessAudioCapture : IDisposable
{
    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceCompletionHandler { void ActivateCompleted(IntPtr operation); }

    [ComVisible(true)]
    sealed class Handler : IActivateAudioInterfaceCompletionHandler
    {
        public readonly ManualResetEventSlim Done = new();
        public int Hr = -1;
        public IntPtr Client;
        public void ActivateCompleted(IntPtr op)
        {
            try
            {
                // IActivateAudioInterfaceAsyncOperation::GetActivateResult — слот 3
                M<FnResult>(op, 3)(op, out int hr, out IntPtr unk);
                Hr = hr;
                if (hr >= 0 && unk != IntPtr.Zero)
                {
                    var iid = IID_AudioClient;
                    Marshal.QueryInterface(unk, ref iid, out Client);
                    Marshal.Release(unk);
                }
            }
            catch { }
            finally { Done.Set(); }
        }
    }

    [DllImport("Mmdevapi.dll", CharSet = CharSet.Unicode)]
    static extern int ActivateAudioInterfaceAsync(string path, ref Guid riid, IntPtr activationParams, IntPtr handler, out IntPtr op);
    [DllImport("kernel32.dll")] static extern IntPtr CreateEvent(IntPtr a, bool manual, bool initial, IntPtr name);
    [DllImport("kernel32.dll")] static extern int WaitForSingleObject(IntPtr h, int ms);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    static readonly Guid IID_AudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    static readonly Guid IID_CaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnResult(IntPtr s, out int hr, out IntPtr unk);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnInit(IntPtr s, int share, int flags, long buffer, long period, IntPtr fmt, IntPtr session);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnV(IntPtr s);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnPtr(IntPtr s, IntPtr p);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnService(IntPtr s, ref Guid iid, out IntPtr obj);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnGetBuffer(IntPtr s, out IntPtr data, out int frames, out int flags, out long pos, out long qpc);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnI32(IntPtr s, int v);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnOutI32(IntPtr s, out int v);

    static T M<T>(IntPtr obj, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size));

    IntPtr client, capture, evt;
    Thread? thread;
    volatile bool running;
    readonly Action<byte[], long> sink;
    readonly System.Diagnostics.Stopwatch clock = new();
    long written; // сколько сэмплов (кадров) уже отдали
    public int RealPackets, SilentPackets, PaddedChunks;

    const int Rate = Mp4Writer.SampleRate, Ch = Mp4Writer.Channels, BlockAlign = Ch * 2;

    readonly int targetPid;
    ProcessAudioCapture(Action<byte[], long> sink, int pid) { this.sink = sink; targetPid = pid; }

    /// <summary>Начать захват. sink(данные, номер первого сэмпла) вызывается из фонового потока. null — захват недоступен.</summary>
    public static ProcessAudioCapture? TryStart(Action<byte[], long> sink, out string error, int pid = 0)
    {
        error = "";
        var cap = new ProcessAudioCapture(sink, pid > 0 ? pid : Environment.ProcessId);
        try { cap.Init(); cap.Start(); return cap; }
        catch (Exception ex) { error = ex.Message; cap.Dispose(); return null; }
    }

    void Init()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) throw new PlatformNotSupportedException("нужна Windows 10 2004 или новее");
        // AUDIOCLIENT_ACTIVATION_PARAMS: тип «петля процесса», наш PID, включая дочерние процессы (движок WebView2)
        IntPtr pars = Marshal.AllocHGlobal(12), prop = Marshal.AllocHGlobal(24);
        var handler = new Handler();
        IntPtr pHandler = IntPtr.Zero, op = IntPtr.Zero;
        try
        {
            Marshal.WriteInt32(pars, 0, 1);
            Marshal.WriteInt32(pars, 4, targetPid);
            Marshal.WriteInt32(pars, 8, 0); // PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
            for (int i = 0; i < 24; i += 4) Marshal.WriteInt32(prop, i, 0);
            Marshal.WriteInt16(prop, 0, 65);           // VT_BLOB
            Marshal.WriteInt32(prop, 8, 12);
            Marshal.WriteIntPtr(prop, 16, pars);
            pHandler = Marshal.GetComInterfaceForObject(handler, typeof(IActivateAudioInterfaceCompletionHandler));
            var iid = IID_AudioClient;
            int hr = ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, prop, pHandler, out op);
            if (hr < 0) throw new COMException("ActivateAudioInterfaceAsync", hr);
            if (!handler.Done.Wait(5000)) throw new TimeoutException("звук не ответил");
            if (handler.Hr < 0 || handler.Client == IntPtr.Zero) throw new COMException("активация звука", handler.Hr);
            client = handler.Client;
        }
        finally
        {
            if (op != IntPtr.Zero) Marshal.Release(op);
            if (pHandler != IntPtr.Zero) Marshal.Release(pHandler);
            Marshal.FreeHGlobal(prop); Marshal.FreeHGlobal(pars);
        }

        // WAVEFORMATEX: PCM 48 кГц, 16 бит, стерео — система сама сконвертирует (AUTOCONVERTPCM)
        IntPtr fmt = Marshal.AllocHGlobal(18);
        try
        {
            Marshal.WriteInt16(fmt, 0, 1); Marshal.WriteInt16(fmt, 2, Ch); Marshal.WriteInt32(fmt, 4, Rate);
            Marshal.WriteInt32(fmt, 8, Rate * BlockAlign); Marshal.WriteInt16(fmt, 12, BlockAlign); Marshal.WriteInt16(fmt, 14, 16); Marshal.WriteInt16(fmt, 16, 0);
            const int LOOPBACK = 0x00020000, EVENTCALLBACK = 0x00040000, AUTOCONVERT = unchecked((int)0x80000000), SRC_QUALITY = 0x08000000;
            int hr = M<FnInit>(client, 3)(client, 0, LOOPBACK | EVENTCALLBACK, 2_000_000, 0, fmt, IntPtr.Zero);
            if (hr < 0) throw new COMException("IAudioClient.Initialize", hr);
        }
        finally { Marshal.FreeHGlobal(fmt); }
        evt = CreateEvent(IntPtr.Zero, false, false, IntPtr.Zero);
        M<FnPtr>(client, 13)(client, evt);                       // SetEventHandle
        var ciid = IID_CaptureClient;
        int h2 = M<FnService>(client, 14)(client, ref ciid, out capture);
        if (h2 < 0) throw new COMException("GetService(IAudioCaptureClient)", h2);
    }

    void Start()
    {
        int hr = M<FnV>(client, 10)(client);                       // Start
        if (hr < 0) throw new COMException("IAudioClient.Start", hr);
        running = true;
        clock.Start();
        thread = new Thread(Loop) { IsBackground = true, Name = "LiteBrowser audio" };
        thread.Start();
    }

    void Loop()
    {
        var getNext = M<FnOutI32>(capture, 5);
        var getBuf = M<FnGetBuffer>(capture, 3);
        var release = M<FnI32>(capture, 4);
        while (running)
        {
            WaitForSingleObject(evt, 40);
            try
            {
                while (running && getNext(capture, out int packet) >= 0 && packet > 0)
                {
                    if (getBuf(capture, out var data, out int frames, out int flags, out _, out _) < 0) break;
                    var bytes = new byte[frames * BlockAlign];
                    if ((flags & 0x2) == 0 && data != IntPtr.Zero) { Marshal.Copy(data, bytes, 0, bytes.Length); RealPackets++; } else SilentPackets++; // 0x2 — тишина
                    release(capture, frames);
                    Emit(bytes);
                }
                // Когда в браузере тихо, система ничего не присылает — досыпаем тишину по часам
                long due = clock.ElapsedTicks * Rate / System.Diagnostics.Stopwatch.Frequency;
                if (due - written > Rate / 5) { PaddedChunks++; } if (due - written > Rate / 5) Emit(new byte[(int)(due - written - Rate / 20) * BlockAlign]);
            }
            catch { }
        }
    }

    void Emit(byte[] pcm)
    {
        if (pcm.Length == 0) return;
        sink(pcm, written);
        written += pcm.Length / BlockAlign;
    }

    public void Dispose()
    {
        running = false;
        try { thread?.Join(500); } catch { }
        if (client != IntPtr.Zero) { try { M<FnV>(client, 11)(client); } catch { } }   // Stop
        if (capture != IntPtr.Zero) { Marshal.Release(capture); capture = IntPtr.Zero; }
        if (client != IntPtr.Zero) { Marshal.Release(client); client = IntPtr.Zero; }
        if (evt != IntPtr.Zero) { CloseHandle(evt); evt = IntPtr.Zero; }
    }
}
