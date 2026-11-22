using System.Text;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Web;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace LiteBrowser;

/// <summary>Свои страницы браузера живут на виртуальном адресе https://lite.home/ и лежат внутри exe.</summary>
static class LitePages
{
    public const string Host = "lite.home";
    public const string Prefix = "https://lite.home/";
    public const string Home = Prefix + "home.html";
    public const string Settings = Prefix + "settings.html";
    public const string History = Prefix + "history.html";
    public const string Blocked = Prefix + "blocked.html";
    public const string Passwords = Prefix + "passwords.html";
    public const string ExtensionsPage = Prefix + "extensions.html";
    public const string DownloadsPage = Prefix + "downloads.html";
    public const string BookmarksPage = Prefix + "bookmarks.html";
    public const string Ai = Prefix + "ai.html";
    public const string Notes = Prefix + "notes.html";
    public const string Security = Prefix + "security.html";
    public const string DevTools = Prefix + "devtools.html";
    public const string OfflinePage = Prefix + "offline.html";

    public static CoreWebView2WebResourceResponse Response(CoreWebView2Environment env, string uri)
    {
        var path = new Uri(uri).AbsolutePath.TrimStart('/');
        if (path.Length == 0) path = "home.html";
        if (path == "wallpaper")
        {
            // Обои стартовой страницы из папки данных браузера
            var wp = Path.Combine(AppEnv.DataDir, "wallpaper.img");
            if (!File.Exists(wp)) return env.CreateWebResourceResponse(null, 404, "Not Found", "");
            var bytes = File.ReadAllBytes(wp);
            string mime = bytes.Length > 3 && bytes[0] == 0x89 && bytes[1] == 'P' ? "image/png"
                : bytes.Length > 3 && bytes[0] == 'R' && bytes[1] == 'I' ? "image/webp"
                : bytes.Length > 3 && bytes[0] == 'G' && bytes[1] == 'I' ? "image/gif" : "image/jpeg";
            return env.CreateWebResourceResponse(new MemoryStream(bytes), 200, "OK", $"Content-Type: {mime}\r\nCache-Control: no-store");
        }
        using var s = typeof(LitePages).Assembly.GetManifestResourceStream("wwwroot/" + path);
        if (s == null) return env.CreateWebResourceResponse(null, 404, "Not Found", "");
        var ms = new MemoryStream();
        s.CopyTo(ms);
        if (path.EndsWith(".html") && L.En)
        {
            // Английский интерфейс: подключаем переводчик страниц (внешний скрипт — CSP запрещает встроенные)
            var html = System.Text.Encoding.UTF8.GetString(ms.ToArray());
            html = html.Replace("</body>", "<script src=\"i18n.js?lang=en\"></script></body>");
            ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(html));
        }
        if (path == "common.css")
        {
            // Цвет акцента и светлая тема — дописываем переменные к общему CSS
            var extra = System.Text.Encoding.UTF8.GetBytes(Theme.PageCss());
            ms.Write(extra, 0, extra.Length);
        }
        ms.Position = 0;
        string type = Path.GetExtension(path) switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".svg" => "image/svg+xml",
            _ => "application/octet-stream",
        };
        return env.CreateWebResourceResponse(ms, 200, "OK",
            $"Content-Type: {type}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff");
    }

    /// <summary>Как показать адрес своей страницы в адресной строке.</summary>
    public static string? Display(string src)
    {
        if (src.StartsWith(Home, StringComparison.Ordinal)) return "";
        if (src.StartsWith(Settings, StringComparison.Ordinal)) return "lite://settings";
        if (src.StartsWith(History, StringComparison.Ordinal)) return "lite://history";
        if (src.StartsWith(Passwords, StringComparison.Ordinal)) return "lite://passwords";
        if (src.StartsWith(ExtensionsPage, StringComparison.Ordinal)) return "lite://extensions";
        if (src.StartsWith(DownloadsPage, StringComparison.Ordinal)) return "lite://downloads";
        if (src.StartsWith(BookmarksPage, StringComparison.Ordinal)) return "lite://bookmarks";
        if (src.StartsWith(Notes, StringComparison.Ordinal)) return "lite://notes";
        if (src.StartsWith(Ai, StringComparison.Ordinal)) return "lite://ai";
        if (src.StartsWith(Security, StringComparison.Ordinal)) return "lite://security";
        if (src.StartsWith(DevTools, StringComparison.Ordinal)) return "lite://devtools";
        if (src.StartsWith(OfflinePage, StringComparison.Ordinal)) return "lite://offline";
        if (src.StartsWith(Blocked, StringComparison.Ordinal))
            return HttpUtility.ParseQueryString(new Uri(src).Query)["url"] ?? "lite://blocked";
        return null;
    }
}

/// <summary>Строка меню масштаба: [−] 100% [+] Сброс.</summary>
sealed class ZoomRow : Control
{
    readonly Func<int> value;
    int hover = -1; // 0 — минус, 1 — плюс, 2 — сброс
    public event Action<int>? Step;
    public event Action? Reset;

    public ZoomRow(Func<int> value)
    {
        this.value = value;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        BackColor = Theme.Menu;
        Size = new Size(Theme.S(310), Theme.S(46));
    }

    Rectangle Minus => new(Theme.S(150), (Height - Theme.S(30)) / 2, Theme.S(30), Theme.S(30));
    Rectangle Value => new(Minus.Right, 0, Theme.S(56), Height);
    Rectangle Plus => new(Value.Right, Minus.Y, Theme.S(30), Theme.S(30));
    Rectangle ResetR => new(Plus.Right + Theme.S(6), Minus.Y, Width - Plus.Right - Theme.S(12), Theme.S(30));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        var text = Theme.GetFont("Segoe UI", 9.5f);
        TextRenderer.DrawText(g, "Масштаб страницы", text, new Rectangle(Theme.S(12), 0, Theme.S(140), Height), Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        var icon = Theme.GetFont(Theme.IconFont, 9f);
        void Circle(Rectangle r, int i, string glyph)
        {
            using var b = new SolidBrush(hover == i ? Theme.Pressed : Theme.Hover);
            g.FillEllipse(b, r);
            TextRenderer.DrawText(g, glyph, icon, r, Theme.Text, Theme.Center);
        }
        Circle(Minus, 0, "");
        Circle(Plus, 1, "");
        TextRenderer.DrawText(g, value() + "%", Theme.GetFont("Segoe UI", 10.5f, FontStyle.Bold), Value, Theme.Text, Theme.Center);
        TextRenderer.DrawText(g, "Сброс", text, ResetR, hover == 2 ? Theme.Text : Theme.Accent, Theme.Center);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = Minus.Contains(e.Location) ? 0 : Plus.Contains(e.Location) ? 1 : ResetR.Contains(e.Location) ? 2 : -1;
        Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
        if (h != hover) { hover = h; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (hover == 0) Step?.Invoke(-1);
        else if (hover == 1) Step?.Invoke(1);
        else if (hover == 2) Reset?.Invoke();
        base.OnMouseDown(e);
    }
}
