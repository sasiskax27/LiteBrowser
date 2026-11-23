using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace LiteBrowser;

/// <summary>
/// Распознавание текста на картинке встроенным в Windows 10/11 движком (Windows.Media.Ocr) — без интернета.
/// WinRT вызывается напрямую по таблицам методов, без пакета Windows SDK (exe не растёт).
/// Языки — те, что установлены в Windows (обычно русский и английский).
/// </summary>
static class WinOcr
{
    [DllImport("combase.dll")] static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string s, int len, out IntPtr h);
    [DllImport("combase.dll")] static extern int WindowsDeleteString(IntPtr h);
    [DllImport("combase.dll")] static extern IntPtr WindowsGetStringRawBuffer(IntPtr h, out uint len);
    [DllImport("combase.dll")] static extern int RoGetActivationFactory(IntPtr cls, ref Guid iid, out IntPtr factory);
    [DllImport("combase.dll")] static extern int RoInitialize(int type);

    static readonly Guid IID_CryptoBufferStatics = new("320B7E22-3CB0-4CDF-8663-1D28910065EB");
    static readonly Guid IID_SoftwareBitmapStatics = new("DF0385DB-672F-4A9D-806E-C2442F343E86");
    static readonly Guid IID_OcrEngineStatics = new("5BFFA85A-3384-3540-9940-699120D428A8");
    static readonly Guid IID_AsyncInfo = new("00000036-0000-0000-C000-000000000046");

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnFromBytes(IntPtr s, uint len, IntPtr data, out IntPtr buffer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnCreateBitmap(IntPtr s, IntPtr buffer, int format, int w, int h, out IntPtr bmp);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnOut(IntPtr s, out IntPtr o);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnIn(IntPtr s, IntPtr i, out IntPtr o);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnOutInt(IntPtr s, out int v);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnOutUInt(IntPtr s, out uint v);

    static T M<T>(IntPtr o, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(o), slot * IntPtr.Size));

    static IntPtr Factory(string cls, Guid iid)
    {
        WindowsCreateString(cls, cls.Length, out var h);
        try { return RoGetActivationFactory(h, ref iid, out var f) == 0 ? f : IntPtr.Zero; }
        finally { WindowsDeleteString(h); }
    }

    static void Ok(int hr, string what) { if (hr < 0) throw new COMException("OCR: " + what, hr); }

    /// <summary>Есть ли в Windows движок OCR и хотя бы один язык.</summary>
    public static bool Supported
    {
        get
        {
            var f = Factory("Windows.Media.Ocr.OcrEngine", IID_OcrEngineStatics);
            if (f == IntPtr.Zero) return false;
            Marshal.Release(f);
            return true;
        }
    }

    /// <summary>Распознать текст. Строки — через перевод строки.</summary>
    public static async Task<string> RecognizeAsync(Bitmap image)
    {
        // Движок не принимает картинки больше MaxImageDimension (обычно 2600) — уменьшаем
        Bitmap? scaled = null;
        IntPtr crypto = IntPtr.Zero, buffer = IntPtr.Zero, sbStatics = IntPtr.Zero, bmp = IntPtr.Zero, ocrStatics = IntPtr.Zero, engine = IntPtr.Zero,
               op = IntPtr.Zero, info = IntPtr.Zero, result = IntPtr.Zero, text = IntPtr.Zero;
        try
        {
            ocrStatics = Factory("Windows.Media.Ocr.OcrEngine", IID_OcrEngineStatics);
            if (ocrStatics == IntPtr.Zero) throw new PlatformNotSupportedException(L.T("В этой Windows нет распознавания текста"));
            Ok(M<FnOutUInt>(ocrStatics, 6)(ocrStatics, out uint maxDim), "MaxImageDimension");
            var src = image;
            // Мелкий текст распознаётся лучше, если картинку увеличить (но не больше предела движка)
            double k = Math.Min(maxDim / (double)Math.Max(image.Width, image.Height), image.Height < 300 ? 2.5 : 1.0);
            if (Math.Abs(k - 1) > 0.01)
            {
                scaled = new Bitmap(Math.Max(1, (int)(image.Width * k)), Math.Max(1, (int)(image.Height * k)), PixelFormat.Format32bppArgb);
                using var g = Graphics.FromImage(scaled);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(image, 0, 0, scaled.Width, scaled.Height);
                src = scaled;
            }
            int w = src.Width, h = src.Height;
            var data = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                // Пиксели → IBuffer (CryptographicBuffer.CreateFromByteArray) → SoftwareBitmap (BGRA8)
                crypto = Factory("Windows.Security.Cryptography.CryptographicBuffer", IID_CryptoBufferStatics);
                if (crypto == IntPtr.Zero) throw new PlatformNotSupportedException("CryptographicBuffer");
                var bytes = new byte[w * h * 4];
                for (int y = 0; y < h; y++) Marshal.Copy(data.Scan0 + y * data.Stride, bytes, y * w * 4, w * 4);
                var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                try { Ok(M<FnFromBytes>(crypto, 9)(crypto, (uint)bytes.Length, pin.AddrOfPinnedObject(), out buffer), "CreateFromByteArray"); }
                finally { pin.Free(); }
            }
            finally { src.UnlockBits(data); }
            sbStatics = Factory("Windows.Graphics.Imaging.SoftwareBitmap", IID_SoftwareBitmapStatics);
            if (sbStatics == IntPtr.Zero) throw new PlatformNotSupportedException("SoftwareBitmap");
            Ok(M<FnCreateBitmap>(sbStatics, 9)(sbStatics, buffer, 87 /* Bgra8 */, w, h, out bmp), "CreateCopyFromBuffer");
            // Движок по языкам профиля Windows
            Ok(M<FnOut>(ocrStatics, 10)(ocrStatics, out engine), "TryCreateFromUserProfileLanguages");
            if (engine == IntPtr.Zero) throw new InvalidOperationException(L.T("В Windows не установлен язык для распознавания текста"));
            Ok(M<FnIn>(engine, 6)(engine, bmp, out op), "RecognizeAsync");
            var aiid = IID_AsyncInfo;
            Ok(Marshal.QueryInterface(op, ref aiid, out info), "IAsyncInfo");
            var status = M<FnOutInt>(info, 7);
            for (int i = 0; i < 300; i++)
            {
                status(info, out int st);
                if (st == 1) break;
                if (st is 2 or 3) throw new InvalidOperationException(L.T("Распознавание не удалось"));
                await Task.Delay(50);
            }
            Ok(M<FnOut>(op, 8)(op, out result), "GetResults");
            // IOcrResult: Lines 6, TextAngle 7, Text 8
            Ok(M<FnOut>(result, 8)(result, out text), "Text");
            var raw = WindowsGetStringRawBuffer(text, out uint len);
            var full = raw == IntPtr.Zero ? "" : Marshal.PtrToStringUni(raw, (int)len);
            return LinesFrom(result) ?? full;
        }
        finally
        {
            scaled?.Dispose();
            if (text != IntPtr.Zero) WindowsDeleteString(text);
            foreach (var p in new[] { result, info, op, engine, ocrStatics, bmp, sbStatics, buffer, crypto })
                if (p != IntPtr.Zero) Marshal.Release(p);
        }
    }

    /// <summary>Текст по строкам (у Text все строки слиты через пробел). null — не получилось, берём Text.</summary>
    static string? LinesFrom(IntPtr result)
    {
        IntPtr lines = IntPtr.Zero;
        try
        {
            if (M<FnOut>(result, 6)(result, out lines) < 0 || lines == IntPtr.Zero) return null;
            // IVectorView<OcrLine>: GetAt 6, get_Size 7
            if (M<FnOutUInt>(lines, 7)(lines, out uint n) < 0) return null;
            var getAt = Marshal.GetDelegateForFunctionPointer<FnGetAt>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(lines), 6 * IntPtr.Size));
            var sb = new System.Text.StringBuilder();
            for (uint i = 0; i < n; i++)
            {
                if (getAt(lines, i, out var line) < 0 || line == IntPtr.Zero) continue;
                try
                {
                    // IOcrLine: Words 6, Text 7
                    if (M<FnOut>(line, 7)(line, out var t) >= 0 && t != IntPtr.Zero)
                    {
                        var raw = WindowsGetStringRawBuffer(t, out uint len);
                        sb.AppendLine(Marshal.PtrToStringUni(raw, (int)len));
                        WindowsDeleteString(t);
                    }
                }
                finally { Marshal.Release(line); }
            }
            return sb.ToString().TrimEnd();
        }
        catch { return null; }
        finally { if (lines != IntPtr.Zero) Marshal.Release(lines); }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnGetAt(IntPtr s, uint index, out IntPtr item);
}
