using System.Security.Cryptography;
using System.Text;

namespace LiteBrowser;

/// <summary>
/// Контейнеры: вкладки с отдельными cookies и данными сайтов — например, два аккаунта VK или Google рядом, без выхода из одного.
/// Каждый контейнер — свой профиль движка WebView2; вкладка контейнера отмечена цветной полоской.
/// </summary>
sealed partial class MainForm
{
    static readonly Color[] ContainerColors =
    {
        Color.FromArgb(79, 209, 139), Color.FromArgb(255, 159, 74), Color.FromArgb(255, 111, 181), Color.FromArgb(124, 156, 255),
        Color.FromArgb(255, 214, 74), Color.FromArgb(63, 193, 201), Color.FromArgb(176, 124, 255), Color.FromArgb(255, 107, 107),
    };

    internal static Color ContainerColor(string name)
    {
        int i = Cfg.Containers.IndexOf(name);
        return ContainerColors[(i < 0 ? Math.Abs(name.GetHashCode()) : i) % ContainerColors.Length];
    }

    /// <summary>Имя профиля движка: только латиница и цифры, поэтому берём хеш названия.</summary>
    internal static string ContainerProfile(string name) =>
        "c" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..16].ToLowerInvariant();

    async Task<BrowserTab> AddContainerTab(string? url, string container, bool select = true)
    {
        var t = CreateTab(url, select);
        t.Container = container;
        await InitTab(t);
        return t;
    }

    /// <summary>Подменю «Открыть в контейнере»: список контейнеров и «Новый контейнер…».</summary>
    ToolStripMenuItem ContainerMenu(string title, Func<string?> url)
    {
        var sub = Theme.Item(title, () => { }, null, "");
        foreach (var c in Cfg.Containers)
        {
            var name = c;
            var it = Theme.Item(name, () => _ = AddContainerTab(url(), name));
            it.Image = Swatch(ContainerColor(name));
            sub.DropDownItems.Add(it);
        }
        sub.DropDownItems.Add(new ToolStripSeparator());
        sub.DropDownItems.Add(Theme.Item(L.T("Новый контейнер…"), () =>
        {
            var name = InputDialog.Ask(this, L.T("Название контейнера (например, «Второй аккаунт»)"), "")?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 30) return;
            CopyAndSave(x => x.Containers = x.Containers.Append(name).Distinct().ToList());
            _ = AddContainerTab(url(), name);
        }, null, ""));
        return sub;
    }

    static Bitmap Swatch(Color c)
    {
        var b = new Bitmap(Theme.S(12), Theme.S(12));
        using var g = Graphics.FromImage(b);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var br = new SolidBrush(c);
        g.FillEllipse(br, 1, 1, b.Width - 2, b.Height - 2);
        return b;
    }
}
