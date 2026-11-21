using System.Text;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Web;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace LiteBrowser;

/// <summary>Поиск по странице и подсказки адресной строки.</summary>
sealed partial class MainForm
{
    // ================= Поиск по странице =================

    void PlaceFindBar()
    {
        if (findBar == null) return;
        findBar.Location = new Point(content.Width - findBar.Width - Theme.S(20), Theme.S(8));
    }

    void ShowFind()
    {
        if (active?.Core == null) return;
        findBar.ApplyTheme();
        PlaceFindBar();
        findBar.Visible = true;
        findBar.BringToFront();
        findBar.Box.Focus();
        findBar.Box.SelectAll();
        if (findBar.Box.Text.Length > 0) _ = RunFind(findBar.Box.Text);
    }

    async Task RunFind(string term)
    {
        var core = active?.Core;
        if (core == null) return;
        if (term.Length == 0) { core.Find.Stop(); findBar.SetCount(0, 0); return; }
        var env = await AppEnv.GetEnvironment();
        var o = env.CreateFindOptions();
        o.FindTerm = term;
        o.SuppressDefaultFindDialog = true;
        o.ShouldHighlightAllMatches = true;
        await core.Find.StartAsync(o);
        findBar.SetCount(core.Find.ActiveMatchIndex + 1, core.Find.MatchCount);
    }

    void CloseFind()
    {
        if (!findBar.Visible) return;
        try { active?.Core?.Find.Stop(); } catch { }
        findBar.Visible = false;
        active?.View.Focus();
    }

    // ================= Подсказки адресной строки =================

    void UpdateSuggestions()
    {
        if (!address.Box.Focused) { suggest.Hide(); return; }
        var q = address.Box.Text.Trim();
        if (q.Length == 0 || q.StartsWith("lite://")) { suggest.Hide(); return; }
        var engine = SearchEngines.Get(Cfg.Engine);
        bool looksUrl = !q.Contains(' ') && q.Contains('.') && !q.EndsWith('.');
        var list = new List<SuggestPopup.Item>();
        if (looksUrl) list.Add(new("", q, "перейти на сайт", ToUrl(q)));
        list.Add(new("", q, "поиск в " + engine.Name, engine.Url + Uri.EscapeDataString(q)));
        var seen = new HashSet<string>(list.Select(x => x.Url));
        foreach (var b in Store.Bookmarks.Where(b => b.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || b.Url.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(3))
            if (seen.Add(b.Url)) list.Add(new("", b.Title, b.Url, b.Url));
        if (!Incognito)
            foreach (var h in Store.SearchHistory(q, 60).Where(h => h.Url.Length < 500).DistinctBy(h => h.Url).Take(6))
            {
                if (list.Count >= 8) break;
                if (seen.Add(h.Url)) list.Add(new("", h.Title.Length > 0 ? h.Title : h.Url, h.Url, h.Url));
            }
        suggest.ShowItems(address, list);

        suggestCts?.Cancel();
        if (!Cfg.SearchSuggestions || Incognito || looksUrl) return;
        var cts = suggestCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            await Task.Delay(180, cts.Token);
            var remote = await RemoteSuggest.Get(engine.Id, q, cts.Token);
            if (cts.IsCancellationRequested || remote.Count == 0) return;
            BeginInvoke(() =>
            {
                if (cts.IsCancellationRequested || address.Box.Text.Trim() != q || !address.Box.Focused) return;
                var merged = list.Take(2).ToList();
                foreach (var r in remote.Where(r => !r.Equals(q, StringComparison.OrdinalIgnoreCase)).Take(4))
                    merged.Add(new("", r, "поиск", engine.Url + Uri.EscapeDataString(r)));
                merged.AddRange(list.Skip(2).Take(Math.Max(0, 9 - merged.Count)));
                suggest.ShowItems(address, merged);
            });
        });
    }

}
