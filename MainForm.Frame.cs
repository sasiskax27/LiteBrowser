using System.Runtime.InteropServices;

namespace LiteBrowser;

/// <summary>
/// Своя рамка окна, как в Chrome и Edge: заголовка Windows нет, вкладки стоят на его месте,
/// кнопки «свернуть / развернуть / закрыть» — на полосе вкладок. Края окна по-прежнему тянутся,
/// окно таскается за пустое место полосы, двойной клик разворачивает, в Windows 11 есть раскладки окон.
/// </summary>
sealed partial class MainForm
{
    const int WM_NCCALCSIZE = 0x83, WM_NCHITTEST = 0x84, WM_NCMOUSEMOVE = 0xA0, WM_NCLBUTTONDOWN = 0xA1, WM_NCLBUTTONUP = 0xA2, WM_NCMOUSELEAVE = 0x2A2;
    const int HTCLIENT = 1, HTCAPTION = 2, HTMAXBUTTON = 9, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14;

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct TRACKMOUSEEVENT { public int cbSize; public uint dwFlags; public IntPtr hwndTrack; public uint dwHoverTime; }
    [DllImport("user32.dll")] static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT e);

    /// <summary>Своя рамка — когда вкладки сверху и окно не во весь экран.</summary>
    bool CustomFrame => !Cfg.VerticalTabs && FormBorderStyle == FormBorderStyle.Sizable && IsHandleCreated;

    int FrameThickness()
    {
        uint dpi = GetDpiForWindow(Handle);
        return GetSystemMetricsForDpi(33 /*SM_CYFRAME*/, dpi) + GetSystemMetricsForDpi(92 /*SM_CXPADDEDBORDER*/, dpi);
    }

    /// <summary>Пересчитать рамку окна (после смены вкладок сверху/слева или полноэкранного режима).</summary>
    void RefreshFrame()
    {
        if (!IsHandleCreated) return;
        strip.CaptionButtons = CustomFrame;
        strip.WindowMaximized = WindowState == FormWindowState.Maximized;
        SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020); // NOSIZE|NOMOVE|NOZORDER|NOACTIVATE|FRAMECHANGED
        strip.Invalidate();
    }

    void SetupFrame()
    {
        strip.MinimizeClicked += () => WindowState = FormWindowState.Minimized;
        strip.CloseClicked += Close;
        Resize += (_, _) =>
        {
            bool max = WindowState == FormWindowState.Maximized;
            if (strip.WindowMaximized != max) { strip.WindowMaximized = max; strip.Invalidate(); }
        };
        HandleCreated += (_, _) => BeginInvoke(RefreshFrame);
        // Для проверок: окно за пределами экрана, не мешает пользователю (снимок делается PrintWindow)
        if (Environment.GetEnvironmentVariable("LITEBROWSER_OFFSCREEN") != null)
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(-5000, 0, 1280, 800);
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (CustomFrame)
        {
            switch (m.Msg)
            {
                case WM_NCCALCSIZE when m.WParam != IntPtr.Zero:
                {
                    // Оставляем края для изменения размера, а заголовок отдаём под вкладки
                    var r = Marshal.PtrToStructure<RECT>(m.LParam);
                    int top = r.Top;
                    base.WndProc(ref m);
                    var r2 = Marshal.PtrToStructure<RECT>(m.LParam);
                    r2.Top = top + (WindowState == FormWindowState.Maximized ? FrameThickness() : 0);
                    Marshal.StructureToPtr(r2, m.LParam, false);
                    m.Result = IntPtr.Zero;
                    return;
                }
                case WM_NCHITTEST:
                {
                    base.WndProc(ref m);
                    if ((int)m.Result != HTCLIENT || !strip.Visible) return;
                    var screen = new Point((short)(m.LParam.ToInt64() & 0xFFFF), (short)((m.LParam.ToInt64() >> 16) & 0xFFFF));
                    var sp = strip.PointToClient(screen);
                    if (!strip.ClientRectangle.Contains(sp)) return;
                    switch (strip.NcHit(sp))
                    {
                        case 1: m.Result = (IntPtr)HTCAPTION; break;
                        case 2: m.Result = (IntPtr)HTMAXBUTTON; break;
                        case 3: m.Result = (IntPtr)(sp.X < Theme.S(10) ? HTTOPLEFT : sp.X > strip.Width - Theme.S(10) ? HTTOPRIGHT : HTTOP); break;
                    }
                    return;
                }
                // «Развернуть» — неклиентская кнопка (так Windows 11 показывает раскладки окон); нажатие обрабатываем сами
                case WM_NCLBUTTONDOWN when (int)m.WParam == HTMAXBUTTON:
                    m.Result = IntPtr.Zero;
                    return;
                case WM_NCLBUTTONUP when (int)m.WParam == HTMAXBUTTON:
                    WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
                    m.Result = IntPtr.Zero;
                    return;
                case WM_NCMOUSEMOVE:
                    strip.HoverMax = (int)m.WParam == HTMAXBUTTON;
                    var tme = new TRACKMOUSEEVENT { cbSize = Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = 0x02 | 0x10 /*LEAVE|NONCLIENT*/, hwndTrack = Handle };
                    TrackMouseEvent(ref tme);
                    break;
                case WM_NCMOUSELEAVE:
                    strip.HoverMax = false;
                    break;
            }
        }
        base.WndProc(ref m);
    }
}
