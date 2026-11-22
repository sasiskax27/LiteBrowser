using System.Runtime.InteropServices;

namespace LiteBrowser;

/// <summary>
/// MP4 (H.264 + AAC) через Media Foundation, встроенный в Windows: никаких сторонних кодеков и библиотек.
/// COM-интерфейсы вызываем по номерам методов в таблице (как в заголовках Windows SDK) — без больших обёрток.
/// Видео подаётся кадрами RGB32 (сверху вниз), звук — PCM 16 бит, 48 кГц, стерео.
/// </summary>
sealed class Mp4Writer : IDisposable
{
    public const int SampleRate = 48000, Channels = 2;
    readonly object gate = new();
    IntPtr writer;
    int videoStream = -1, audioStream = -1;
    public int Width { get; }
    public int Height { get; }
    public int Fps { get; }
    public bool HasAudio => audioStream >= 0;
    bool finished;

    // ---------- Media Foundation ----------
    [DllImport("mfplat.dll")] static extern int MFStartup(int version, int flags);
    [DllImport("mfplat.dll")] static extern int MFShutdown();
    [DllImport("mfplat.dll")] static extern int MFCreateMediaType(out IntPtr type);
    [DllImport("mfplat.dll")] static extern int MFCreateSample(out IntPtr sample);
    [DllImport("mfplat.dll")] static extern int MFCreateMemoryBuffer(int max, out IntPtr buffer);
    [DllImport("mfplat.dll")] static extern int MFCreateAttributes(out IntPtr attrs, int size);
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)] static extern int MFCreateSinkWriterFromURL(string url, IntPtr byteStream, IntPtr attrs, out IntPtr writer);

    static readonly Guid MT_MAJOR = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5"),
        MT_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e"), MT_INTERLACE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd"),
        MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d"), MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0"),
        MT_PAR = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6"), MT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6"),
        MT_CHANNELS = new("37e48bf5-645e-4c5b-89de-ada9e29b696a"), MT_RATE = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba"),
        MT_BITS = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669"), MT_BYTES_PER_SEC = new("1aab75c8-cfef-451c-ab95-ac034b8e1731"),
        MT_BLOCK = new("322de230-9eeb-43bd-ab7a-ff412251541d"), HW_TRANSFORMS = new("a634a91c-822b-41b9-a494-4de4643612b0"),
        Video = new("73646976-0000-0010-8000-00AA00389B71"), Audio = new("73647561-0000-0010-8000-00AA00389B71"),
        H264 = new("34363248-0000-0010-8000-00AA00389B71"), RGB32 = new("00000016-0000-0010-8000-00AA00389B71"),
        AAC = new("00001610-0000-0010-8000-00AA00389B71"), PCM = new("00000001-0000-0010-8000-00AA00389B71");

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnV(IntPtr s);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnGuidGuid(IntPtr s, ref Guid k, ref Guid v);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnGuidU32(IntPtr s, ref Guid k, int v);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnGuidU64(IntPtr s, ref Guid k, long v);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnAddStream(IntPtr s, IntPtr type, out int index);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnSetInput(IntPtr s, int index, IntPtr type, IntPtr attrs);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnWrite(IntPtr s, int index, IntPtr sample);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnI64(IntPtr s, long v);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnPtr(IntPtr s, IntPtr p);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnLock(IntPtr s, out IntPtr data, out int max, out int cur);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnI32(IntPtr s, int v);

    static T M<T>(IntPtr obj, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size));
    static void Ok(int hr, string what) { if (hr < 0) throw new COMException("Media Foundation: " + what, hr); }

    // IMFAttributes: SetUINT32 21, SetUINT64 22, SetGUID 24
    static void SetGuid(IntPtr a, Guid k, Guid v) => Ok(M<FnGuidGuid>(a, 24)(a, ref k, ref v), "SetGUID");
    static void SetU32(IntPtr a, Guid k, int v) => Ok(M<FnGuidU32>(a, 21)(a, ref k, v), "SetUINT32");
    static void SetU64(IntPtr a, Guid k, long v) => Ok(M<FnGuidU64>(a, 22)(a, ref k, v), "SetUINT64");
    static long Pair(int hi, int lo) => ((long)hi << 32) | (uint)lo;

    public Mp4Writer(string path, int width, int height, int fps, bool audio)
    {
        Width = width & ~1; Height = height & ~1; Fps = fps;
        Ok(MFStartup(0x20070, 0), "MFStartup");
        IntPtr attrs = IntPtr.Zero;
        try
        {
            Ok(MFCreateAttributes(out attrs, 1), "attributes");
            SetU32(attrs, HW_TRANSFORMS, 1); // кодировщик видеокарты, если есть
            Ok(MFCreateSinkWriterFromURL(path, IntPtr.Zero, attrs, out writer), "SinkWriter");
        }
        finally { if (attrs != IntPtr.Zero) Marshal.Release(attrs); }

        // Видео: на выходе H.264, на входе RGB32
        int bitrate = Math.Clamp(Width * Height * fps / 6, 1_500_000, 12_000_000);
        videoStream = AddStream(t =>
        {
            SetGuid(t, MT_MAJOR, Video); SetGuid(t, MT_SUBTYPE, H264); SetU32(t, MT_BITRATE, bitrate); SetU32(t, MT_INTERLACE, 2);
            SetU64(t, MT_FRAME_SIZE, Pair(Width, Height)); SetU64(t, MT_FRAME_RATE, Pair(fps, 1)); SetU64(t, MT_PAR, Pair(1, 1));
        }, t =>
        {
            SetGuid(t, MT_MAJOR, Video); SetGuid(t, MT_SUBTYPE, RGB32); SetU32(t, MT_INTERLACE, 2);
            SetU64(t, MT_FRAME_SIZE, Pair(Width, Height)); SetU64(t, MT_FRAME_RATE, Pair(fps, 1)); SetU64(t, MT_PAR, Pair(1, 1));
            SetU32(t, MT_STRIDE, Width * 4); // строки сверху вниз
        });
        if (audio)
        {
            try
            {
                audioStream = AddStream(t =>
                {
                    SetGuid(t, MT_MAJOR, Audio); SetGuid(t, MT_SUBTYPE, AAC); SetU32(t, MT_BITS, 16);
                    SetU32(t, MT_RATE, SampleRate); SetU32(t, MT_CHANNELS, Channels); SetU32(t, MT_BYTES_PER_SEC, 24000); // 192 кбит/с
                }, t =>
                {
                    SetGuid(t, MT_MAJOR, Audio); SetGuid(t, MT_SUBTYPE, PCM); SetU32(t, MT_BITS, 16);
                    SetU32(t, MT_RATE, SampleRate); SetU32(t, MT_CHANNELS, Channels);
                    SetU32(t, MT_BLOCK, Channels * 2); SetU32(t, MT_BYTES_PER_SEC, SampleRate * Channels * 2);
                });
            }
            catch { audioStream = -1; } // без звука, но видео пишем
        }
        Ok(M<FnV>(writer, 5)(writer), "BeginWriting");
    }

    int AddStream(Action<IntPtr> output, Action<IntPtr> input)
    {
        IntPtr o = IntPtr.Zero, i = IntPtr.Zero;
        try
        {
            Ok(MFCreateMediaType(out o), "media type"); output(o);
            Ok(M<FnAddStream>(writer, 3)(writer, o, out int index), "AddStream");
            Ok(MFCreateMediaType(out i), "media type"); input(i);
            Ok(M<FnSetInput>(writer, 4)(writer, index, i, IntPtr.Zero), "SetInputMediaType");
            return index;
        }
        finally { if (o != IntPtr.Zero) Marshal.Release(o); if (i != IntPtr.Zero) Marshal.Release(i); }
    }

    void Write(int stream, ReadOnlySpan<byte> data, long time, long duration)
    {
        IntPtr buf = IntPtr.Zero, sample = IntPtr.Zero;
        try
        {
            Ok(MFCreateMemoryBuffer(data.Length, out buf), "buffer");
            Ok(M<FnLock>(buf, 3)(buf, out var ptr, out _, out _), "Lock");
            unsafe { data.CopyTo(new Span<byte>((void*)ptr, data.Length)); }
            M<FnV>(buf, 4)(buf);                                   // Unlock
            Ok(M<FnI32>(buf, 6)(buf, data.Length), "SetCurrentLength");
            Ok(MFCreateSample(out sample), "sample");
            Ok(M<FnPtr>(sample, 42)(sample, buf), "AddBuffer");
            Ok(M<FnI64>(sample, 36)(sample, time), "SetSampleTime");
            Ok(M<FnI64>(sample, 38)(sample, duration), "SetSampleDuration");
            lock (gate)
            {
                if (finished) return;
                Ok(M<FnWrite>(writer, 6)(writer, stream, sample), "WriteSample");
            }
        }
        finally { if (sample != IntPtr.Zero) Marshal.Release(sample); if (buf != IntPtr.Zero) Marshal.Release(buf); }
    }

    /// <summary>Кадр номер index (RGB32, Width×Height, сверху вниз).</summary>
    public void WriteVideo(ReadOnlySpan<byte> rgb32, long index)
    {
        long dur = 10_000_000L / Fps;
        Write(videoStream, rgb32, index * dur, dur);
    }

    /// <summary>Звук: PCM 16 бит стерео; startFrame — номер первого сэмпла от начала записи.</summary>
    public void WriteAudio(ReadOnlySpan<byte> pcm, long startFrame)
    {
        if (audioStream < 0 || pcm.Length == 0) return;
        int frames = pcm.Length / (Channels * 2);
        Write(audioStream, pcm, startFrame * 10_000_000L / SampleRate, frames * 10_000_000L / SampleRate);
    }

    public void Finish()
    {
        lock (gate)
        {
            if (finished) return;
            finished = true;
            try { M<FnV>(writer, 11)(writer); } catch { } // Finalize — дописывает индекс MP4
        }
    }

    public void Dispose()
    {
        Finish();
        if (writer != IntPtr.Zero) { Marshal.Release(writer); writer = IntPtr.Zero; }
        try { MFShutdown(); } catch { }
    }
}
