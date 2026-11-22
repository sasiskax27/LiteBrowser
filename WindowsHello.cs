using System.Runtime.InteropServices;

namespace LiteBrowser;

/// <summary>
/// Windows Hello (отпечаток, лицо или PIN Windows) — через системный UserConsentVerifier.
/// Вызываем WinRT напрямую (IUserConsentVerifierInterop), без пакета Windows SDK: exe не растёт на 20+ МБ.
/// </summary>
static class WindowsHello
{
    public enum Result { Verified = 0, DeviceNotPresent = 1, NotConfigured = 2, DisabledByPolicy = 3, DeviceBusy = 4, RetriesExhausted = 5, Canceled = 6, Error = 100 }

    const string ClassId = "Windows.Security.Credentials.UI.UserConsentVerifier";
    static readonly Guid IID_Interop = new("39E050C3-4E74-441A-8DC0-B81104DF949C");
    // IAsyncOperation<UserConsentVerificationResult>
    static readonly Guid IID_AsyncOpResult = new("FD596FFD-2318-558F-9DBE-D21DF43764A5");
    static readonly Guid IID_AsyncInfo = new("00000036-0000-0000-C000-000000000046");

    [DllImport("combase.dll", PreserveSig = true)]
    static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string s, int len, out IntPtr hstring);
    [DllImport("combase.dll")] static extern int WindowsDeleteString(IntPtr hstring);
    [DllImport("combase.dll")] static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);

    // Слоты таблиц методов: IInspectable — 0..5, дальше методы интерфейса
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int RequestForWindowFn(IntPtr self, IntPtr hwnd, IntPtr message, ref Guid riid, out IntPtr op);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int GetIntFn(IntPtr self, out int value);

    static T Slot<T>(IntPtr obj, int index) where T : Delegate
    {
        var vtbl = Marshal.ReadIntPtr(obj);
        return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(vtbl, index * IntPtr.Size));
    }

    /// <summary>Есть ли в системе сам компонент (Windows 10+). Настроен ли Hello у пользователя — станет ясно при проверке.</summary>
    public static bool Supported
    {
        get
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10)) return false;
            IntPtr cls = IntPtr.Zero, f = IntPtr.Zero;
            try
            {
                if (WindowsCreateString(ClassId, ClassId.Length, out cls) != 0) return false;
                var iid = IID_Interop;
                return RoGetActivationFactory(cls, ref iid, out f) == 0 && f != IntPtr.Zero;
            }
            catch { return false; }
            finally { if (f != IntPtr.Zero) Marshal.Release(f); if (cls != IntPtr.Zero) WindowsDeleteString(cls); }
        }
    }

    /// <summary>Показать окно Windows Hello поверх окна owner и дождаться ответа.</summary>
    public static async Task<Result> VerifyAsync(IntPtr owner, string message)
    {
        IntPtr cls = IntPtr.Zero, msg = IntPtr.Zero, factory = IntPtr.Zero, op = IntPtr.Zero, info = IntPtr.Zero;
        try
        {
            if (WindowsCreateString(ClassId, ClassId.Length, out cls) != 0) return Result.Error;
            var iid = IID_Interop;
            if (RoGetActivationFactory(cls, ref iid, out factory) != 0) return Result.Error;
            WindowsCreateString(message, message.Length, out msg);
            var riid = IID_AsyncOpResult;
            // IUserConsentVerifierInterop::RequestVerificationForWindowAsync — слот 6
            int hr = Slot<RequestForWindowFn>(factory, 6)(factory, owner, msg, ref riid, out op);
            if (hr != 0 || op == IntPtr.Zero) return Result.Error;
            var aiid = IID_AsyncInfo;
            if (Marshal.QueryInterface(op, ref aiid, out info) != 0) return Result.Error;
            var getStatus = Slot<GetIntFn>(info, 7);   // IAsyncInfo::get_Status
            // Ждём, пока пользователь ответит (окно системное — наш поток свободен)
            var until = DateTime.Now.AddMinutes(5);
            while (true)
            {
                getStatus(info, out int status);
                if (status == 1) break;                 // Completed
                if (status is 2 or 3) return status == 2 ? Result.Canceled : Result.Error;
                if (DateTime.Now > until) return Result.Canceled;
                await Task.Delay(100);
            }
            Slot<GetIntFn>(op, 8)(op, out int result); // IAsyncOperation<T>::GetResults
            return Enum.IsDefined(typeof(Result), result) ? (Result)result : Result.Error;
        }
        catch { return Result.Error; }
        finally
        {
            if (info != IntPtr.Zero) Marshal.Release(info);
            if (op != IntPtr.Zero) Marshal.Release(op);
            if (factory != IntPtr.Zero) Marshal.Release(factory);
            if (msg != IntPtr.Zero) WindowsDeleteString(msg);
            if (cls != IntPtr.Zero) WindowsDeleteString(cls);
        }
    }

    public static string Describe(Result r) => r switch
    {
        Result.Verified => L.T("Подтверждено"),
        Result.DeviceNotPresent => L.T("Нет устройства Windows Hello (сканера, камеры)"),
        Result.NotConfigured => L.T("Windows Hello не настроен: Параметры → Учётные записи → Варианты входа"),
        Result.DisabledByPolicy => L.T("Windows Hello отключён администратором"),
        Result.DeviceBusy => L.T("Устройство Windows Hello занято"),
        Result.RetriesExhausted => L.T("Слишком много попыток"),
        Result.Canceled => L.T("Отменено"),
        _ => L.T("Windows Hello недоступен"),
    };
}
