using System.Diagnostics;
using Microsoft.Web.WebView2.Core;

namespace LiteBrowser;

/// <summary>
/// Диспетчер задач браузера (Shift+Esc): сколько памяти занимает каждая вкладка и процессы движка, время запуска.
/// Память — «частная» (как в диспетчере задач Windows, столбец «Выделенная память»), обновляется раз в 2 секунды.
/// </summary>
sealed class TaskManagerForm : Form
{
    readonly ListView list = new() { View = View.Details, FullRowSelect = true, BorderStyle = BorderStyle.None, HeaderStyle = ColumnHeaderStyle.Nonclickable, MultiSelect = false };
    readonly Label total = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
    readonly Button closeBtn = new() { FlatStyle = FlatStyle.Flat }, freezeBtn = new() { FlatStyle = FlatStyle.Flat };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 2000 };
    bool busy;
    static TaskManagerForm? open;
    static int S(float v) => Theme.S(v);

    /// <summary>Открыть (или поднять уже открытый) диспетчер.</summary>
    public static void ShowFor(Form owner)
    {
        if (open is { IsDisposed: false }) { open.Activate(); return; }
        open = new TaskManagerForm();
        open.Show(owner);
    }

    TaskManagerForm()
    {
        Text = L.T("Диспетчер задач LiteBrowser");
        Icon = Theme.MakeAppIcon();
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        BackColor = Theme.Toolbar; ForeColor = Theme.Text;
        ClientSize = new Size(S(620), S(440));
        MinimumSize = new Size(S(420), S(260));
        list.BackColor = Theme.Field; list.ForeColor = Theme.Text;
        list.Font = Theme.GetFont("Segoe UI", 9.5f);
        list.Columns.Add(L.T("Задача"), S(360));
        list.Columns.Add(L.T("Память"), S(110), HorizontalAlignment.Right);
        list.Columns.Add(L.T("Процесс"), S(90), HorizontalAlignment.Right);
        // Заголовок таблицы — в цвет темы (стандартный всегда белый)
        list.OwnerDraw = true;
        list.DrawItem += (_, e) => e.DrawDefault = true;
        list.DrawSubItem += (_, e) => e.DrawDefault = true;
        list.DrawColumnHeader += (_, e) =>
        {
            using var bg = new SolidBrush(Theme.Toolbar);
            e.Graphics.FillRectangle(bg, e.Bounds);
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | (e.Header?.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left);
            var r = Rectangle.Inflate(e.Bounds, -S(6), 0);
            TextRenderer.DrawText(e.Graphics, e.Header?.Text, Theme.GetFont("Segoe UI", 9f, FontStyle.Bold), r, Theme.TextDim, flags);
        };
        total.Font = Theme.GetFont("Segoe UI", 9.5f);
        total.ForeColor = Theme.TextDim;
        foreach (var (b, t) in new[] { (closeBtn, L.T("Закрыть вкладку")), (freezeBtn, L.T("Заморозить")) })
        {
            b.Text = t; b.BackColor = Theme.Hover; b.ForeColor = Theme.Text; b.FlatAppearance.BorderSize = 0;
            b.Font = Theme.GetFont("Segoe UI", 9.5f); b.Enabled = false;
        }
        closeBtn.Click += (_, _) => { if (Selected() is { } x) { x.form.CloseTabFromManager(x.tab); _ = RefreshAsync(); } };
        freezeBtn.Click += async (_, _) => { if (Selected() is { } x) { await x.form.FreezeTabFromManager(x.tab); await RefreshAsync(); } };
        list.SelectedIndexChanged += (_, _) => { bool tab = Selected() != null; closeBtn.Enabled = freezeBtn.Enabled = tab; };
        list.DoubleClick += (_, _) => { if (Selected() is { } x) x.form.ShowTabFromManager(x.tab); };
        Controls.AddRange(new Control[] { list, total, closeBtn, freezeBtn });
        Resize += (_, _) => DoLayout();
        DoLayout();
        timer.Tick += async (_, _) => await RefreshAsync();
        Shown += async (_, _) => { await RefreshAsync(); timer.Start(); };
        FormClosed += (_, _) => { timer.Dispose(); open = null; };
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { Theme.StyleTitleBar(Handle, Theme.Toolbar); } catch { }
    }

    void DoLayout()
    {
        int pad = S(12), bh = S(34);
        list.SetBounds(pad, pad, ClientSize.Width - pad * 2, ClientSize.Height - pad * 3 - bh);
        int y = ClientSize.Height - pad - bh;
        freezeBtn.SetBounds(ClientSize.Width - pad - S(130), y, S(130), bh);
        closeBtn.SetBounds(freezeBtn.Left - S(8) - S(150), y, S(150), bh);
        total.SetBounds(pad, y, closeBtn.Left - pad * 2, bh);
    }

    (MainForm form, BrowserTab tab)? Selected() =>
        list.SelectedItems.Count == 1 && list.SelectedItems[0].Tag is ValueTuple<MainForm, BrowserTab> t ? t : null;

    static long Mem(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.PrivateMemorySize64; } catch { return 0; }
    }

    static string Mb(long bytes) => $"{bytes / 1048576.0:0} " + L.T("МБ");

    static string KindName(CoreWebView2ProcessKind k) => k switch
    {
        CoreWebView2ProcessKind.Browser => L.T("Движок: основной процесс"),
        CoreWebView2ProcessKind.Gpu => L.T("Движок: видеокарта (GPU)"),
        CoreWebView2ProcessKind.Utility => L.T("Движок: служебный (сеть, звук)"),
        CoreWebView2ProcessKind.Renderer => L.T("Движок: страница"),
        CoreWebView2ProcessKind.PpapiPlugin or CoreWebView2ProcessKind.PpapiBroker => L.T("Движок: плагин"),
        _ => L.T("Движок: прочее"),
    };

    async Task RefreshAsync()
    {
        if (busy || IsDisposed) return;
        busy = true;
        try
        {
            var env = await AppEnv.GetEnvironment();
            var infos = await env.GetProcessExtendedInfosAsync();
            var tabs = BrowserApp.Instance.AllTabs().ToList();
            var rows = new List<(string name, long mem, int pid, object? tag)>();
            long sum = 0;
            int self = Environment.ProcessId;
            long selfMem = Mem(self);
            sum += selfMem;
            rows.Add(("LiteBrowser — " + L.T("окна и вкладки"), selfMem, self, null));
            var usedTabs = new HashSet<BrowserTab>();
            foreach (var info in infos)
            {
                var pi = info.ProcessInfo;
                long mem = Mem(pi.ProcessId);
                sum += mem;
                if (pi.Kind == CoreWebView2ProcessKind.Renderer)
                {
                    // Процесс страницы: находим вкладки, чьи главные фреймы в нём живут
                    var owners = new List<(MainForm, BrowserTab)>();
                    foreach (var f in info.AssociatedFrameInfos)
                    {
                        var main = f; while (main.ParentFrameInfo != null) main = main.ParentFrameInfo;
                        foreach (var (form, tab) in tabs)
                        {
                            if (usedTabs.Contains(tab) || tab.Core == null) continue;
                            bool match;
                            try { match = tab.Core.FrameId == main.FrameId; } catch { match = tab.Core.Source == main.Source; }
                            if (match) { owners.Add((form, tab)); usedTabs.Add(tab); }
                        }
                    }
                    if (owners.Count == 0) { rows.Add((KindName(pi.Kind), mem, pi.ProcessId, null)); continue; }
                    // Несколько вкладок одного сайта делят процесс — делим память поровну и помечаем
                    foreach (var (form, tab) in owners)
                        rows.Add(((form.Incognito ? "🕶 " : "") + (tab.Title.Length > 0 ? tab.Title : tab.Core?.Source ?? "") + (owners.Count > 1 ? $"  ({L.T("общий процесс")})" : ""),
                                  mem / owners.Count, pi.ProcessId, (form, tab)));
                }
                else rows.Add((KindName(pi.Kind), mem, pi.ProcessId, null));
            }
            // Замороженные и ещё не загруженные вкладки памяти почти не занимают — показываем, чтобы было видно, что они есть
            foreach (var (form, tab) in tabs.Where(t => !usedTabs.Contains(t.tab)))
                rows.Add(("💤 " + tab.Title, 0, 0, (form, tab)));

            var sel = Selected()?.tab;
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var r in rows.OrderByDescending(r => r.tag != null).ThenByDescending(r => r.mem))
            {
                var it = new ListViewItem(new[] { r.name, r.mem > 0 ? Mb(r.mem) : "—", r.pid > 0 ? r.pid.ToString() : "" }) { Tag = r.tag };
                if (r.tag is ValueTuple<MainForm, BrowserTab> t && t.Item2 == sel) it.Selected = true;
                if (r.tag == null) it.ForeColor = Theme.TextDim;
                list.Items.Add(it);
            }
            list.EndUpdate();
            total.Text = L.T("Всего: ") + Mb(sum) + $" · {L.T("процессов")}: {infos.Count + 1}" +
                         (AppEnv.StartupMs > 0 ? $" · {L.T("запуск")}: {AppEnv.StartupMs / 1000.0:0.0} {L.T("с")}" : "");
        }
        catch (Exception ex) { total.Text = ex.Message; }
        finally { busy = false; }
    }
}
