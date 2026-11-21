using Microsoft.Web.WebView2.Core;

namespace LiteBrowser;

/// <summary>Разделённый экран: две вкладки рядом. Активная половина подсвечена, разделитель можно тянуть.</summary>
sealed partial class MainForm
{
    float splitRatio = 0.5f;
    bool splitDragging;

    BrowserTab? SplitPartner => active?.SplitWith is { } p && tabs.Contains(p) ? p : null;
    int SplitGapW => Theme.S(6);
    int SplitTop => Theme.S(2);

    (BrowserTab left, BrowserTab right)? SplitPair()
    {
        if (active == null || SplitPartner is not { } p) return null;
        return tabs.IndexOf(active) < tabs.IndexOf(p) ? (active, p) : (p, active);
    }

    Rectangle SplitGapRect()
    {
        int x = (int)(content.Width * splitRatio) - SplitGapW / 2;
        return new Rectangle(x, 0, SplitGapW, content.Height);
    }

    /// <summary>Показывает активную вкладку (и её пару, если экран разделён).</summary>
    void LayoutViews()
    {
        var pair = SplitPair();
        content.SuspendLayout();
        foreach (var t in tabs)
        {
            bool on = t == active || (pair != null && (t == pair.Value.left || t == pair.Value.right));
            if (on && pair != null)
            {
                var gap = SplitGapRect();
                t.View.Dock = DockStyle.None;
                t.View.Bounds = t == pair.Value.left
                    ? new Rectangle(0, SplitTop, gap.Left, content.Height - SplitTop)
                    : new Rectangle(gap.Right, SplitTop, content.Width - gap.Right, content.Height - SplitTop);
            }
            else if (t.View.Dock != DockStyle.Fill) t.View.Dock = DockStyle.Fill;
            t.View.Visible = on;
            if (on && t.Core == null && t != active) _ = InitTab(t);
            if (t.Core == null) continue;
            try { t.Core.MemoryUsageTargetLevel = on ? CoreWebView2MemoryUsageTargetLevel.Normal : CoreWebView2MemoryUsageTargetLevel.Low; }
            catch { }
        }
        content.ResumeLayout();
        content.Invalidate();
    }

    void SetupSplit()
    {
        content.Resize += (_, _) => { if (SplitPartner != null) LayoutViews(); };
        content.Paint += (_, e) =>
        {
            if (SplitPair() is not { } pair || active == null) return;
            // Подсветка активной половины и ручка разделителя
            var a = active.View.Bounds;
            using var accent = new SolidBrush(Theme.Accent);
            e.Graphics.FillRectangle(accent, a.Left, 0, a.Width, SplitTop);
            var gap = SplitGapRect();
            using var grip = new SolidBrush(splitDragging ? Theme.Accent : Theme.Line);
            int gh = Theme.S(36);
            e.Graphics.FillRectangle(grip, gap.Left + gap.Width / 2 - Theme.S(1), (content.Height - gh) / 2, Theme.S(2), gh);
        };
        content.MouseMove += (_, e) =>
        {
            if (SplitPartner == null) { content.Cursor = Cursors.Default; return; }
            if (splitDragging)
            {
                splitRatio = Math.Clamp(e.X / (float)Math.Max(1, content.Width), 0.2f, 0.8f);
                LayoutViews();
                return;
            }
            content.Cursor = SplitGapRect().Contains(e.Location) ? Cursors.VSplit : Cursors.Default;
        };
        content.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || SplitPartner == null) return;
            if (!SplitGapRect().Contains(e.Location)) return;
            if (e.Clicks == 2) { splitRatio = 0.5f; LayoutViews(); return; }
            splitDragging = true;
            content.Capture = true;
        };
        content.MouseUp += (_, _) => { if (splitDragging) { splitDragging = false; content.Capture = false; content.Invalidate(); } };
    }

    /// <summary>Клик внутри второй половины делает её активной (адресная строка показывает её адрес).</summary>
    void OnViewFocused(BrowserTab tab)
    {
        if (tab == active || SplitPartner != tab) return;
        active!.LastActive = DateTime.Now;
        active = tab;
        tab.LastActive = DateTime.Now;
        strip.Selected = tab;
        content.Invalidate();
        UpdateUi(force: true);
    }

    /// <summary>Разделить экран: текущая вкладка + вторая (недавняя или новая).</summary>
    void ToggleSplit()
    {
        if (active == null) return;
        if (SplitPartner != null) { Unsplit(); return; }
        var other = tabs.Where(t => t != active && t.SplitWith == null).OrderByDescending(t => t.LastActive).FirstOrDefault()
                    ?? CreateTab(homeUrl, select: false);
        SplitWith(active, other);
    }

    void SplitWith(BrowserTab a, BrowserTab b)
    {
        if (a == b) return;
        if (a.SplitWith is { } oa) oa.SplitWith = null;
        if (b.SplitWith is { } ob) ob.SplitWith = null;
        a.SplitWith = b;
        b.SplitWith = a;
        splitRatio = 0.5f;
        SelectTab(a);
        strip.Invalidate();
    }

    void Unsplit()
    {
        if (active?.SplitWith is { } p) { p.SplitWith = null; active.SplitWith = null; }
        LayoutViews();
        strip.Invalidate();
        active?.View.Focus();
    }

    void SwapSplit()
    {
        if (SplitPair() is not { } pair) return;
        int i = tabs.IndexOf(pair.left), j = tabs.IndexOf(pair.right);
        (tabs[i], tabs[j]) = (tabs[j], tabs[i]);
        LayoutViews();
        strip.Invalidate();
    }
}
