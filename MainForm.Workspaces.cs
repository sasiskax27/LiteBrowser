namespace LiteBrowser;

/// <summary>Рабочие пространства в окне: меню, переключение, название в заголовке.</summary>
sealed partial class MainForm
{
    /// <summary>Пространства есть только в основном окне (не в инкогнито и не в дополнительных окнах).</summary>
    bool WorkspacesHere => !Incognito && BrowserApp.Instance.IsMainWindow(this);

    void SwitchWorkspace(Workspace target)
    {
        if (!WorkspacesHere || target == Workspaces.Current) return;
        Workspaces.SwitchTo(target, GetSession());
        CloseFind();
        // Убираем текущие вкладки и открываем вкладки другого пространства
        foreach (var t in tabs.ToList())
        {
            content.Controls.Remove(t.View);
            t.View.Dispose();
            t.Icon?.Dispose();
        }
        tabs.Clear();
        active = null;
        var s = target.Session;
        if (s.Tabs.Count > 0) RestoreTabs(s.Tabs, s.Info, s.Active);
        else _ = AddTab(homeUrl);
        UpdateTitle();
        strip.Invalidate();
    }

    void NewWorkspace()
    {
        var name = InputDialog.Ask(this, "Название нового пространства (например «Учёба» или «Работа»)", "");
        if (string.IsNullOrWhiteSpace(name)) return;
        try { SwitchWorkspace(Workspaces.Create(name)); }
        catch (ArgumentException ex) { MessageBox.Show(this, ex.Message, "Рабочие пространства"); }
    }

    void RenameWorkspace()
    {
        var w = Workspaces.Current;
        var name = InputDialog.Ask(this, "Новое название пространства", w.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        try { Workspaces.Rename(w, name); UpdateTitle(); }
        catch (ArgumentException ex) { MessageBox.Show(this, ex.Message, "Рабочие пространства"); }
    }

    void DeleteWorkspace(Workspace w)
    {
        if (MessageBox.Show(this, $"Удалить пространство «{w.Name}» и его вкладки?\nЗакладки и история останутся.", "Рабочие пространства",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
            Workspaces.Delete(w);
    }

    ToolStripMenuItem WorkspacesMenu()
    {
        var root = new ToolStripMenuItem("Рабочие пространства") { ForeColor = Theme.Text, Padding = new Padding(6, 5, 6, 5), DropDown = Theme.NewMenu() };
        var cur = Workspaces.Current;
        foreach (var w in Workspaces.All)
        {
            var ws = w;
            var count = w == cur ? tabs.Count : w.Session.Tabs.Count;
            var it = Theme.Item((w == cur ? "✓  " : "     ") + w.Name + $"  ·  {count} вкл.", () => SwitchWorkspace(ws));
            try { it.ForeColor = ColorTranslator.FromHtml(w.Color); } catch { }
            root.DropDownItems.Add(it);
        }
        root.DropDownItems.Add(new ToolStripSeparator());
        root.DropDownItems.Add(Theme.Item("Новое пространство…", NewWorkspace));
        root.DropDownItems.Add(Theme.Item("Переименовать текущее…", RenameWorkspace));
        var others = Workspaces.All.Where(w => w != cur).ToList();
        if (others.Count > 0)
        {
            var del = new ToolStripMenuItem("Удалить") { ForeColor = Theme.Text, Padding = new Padding(6, 5, 6, 5), DropDown = Theme.NewMenu() };
            foreach (var w in others) { var ws = w; del.DropDownItems.Add(Theme.Item(w.Name, () => DeleteWorkspace(ws))); }
            root.DropDownItems.Add(del);
        }
        return root;
    }

    string WorkspaceSuffix => WorkspacesHere && Workspaces.InUse ? " — " + Workspaces.Current.Name : "";
}

/// <summary>Автогруппировка вкладок по темам.</summary>
sealed partial class MainForm
{
    void AutoGroupTabs()
    {
        var candidates = tabs.Where(t => !t.Pinned && t.Group == null && TabUrl(t).StartsWith("http")).ToList();
        var plan = TabGrouping.Plan(candidates.Select(TabUrl).ToList());
        if (plan.Count == 0)
        {
            MessageBox.Show(this, "Не нашлось вкладок на одну тему — группировать нечего.", "Группы вкладок");
            return;
        }
        var byName = Groups.ToDictionary(g => g.Name, g => g, StringComparer.OrdinalIgnoreCase);
        foreach (var (i, name) in plan.OrderBy(p => p.Key))
        {
            if (!byName.TryGetValue(name, out var g))
            {
                var used = Groups.Select(x => x.Color).ToHashSet();
                var color = TabGroup.Palette.FirstOrDefault(c => !used.Contains(c), TabGroup.Palette[byName.Count % TabGroup.Palette.Length]);
                byName[name] = g = new TabGroup { Name = name, Color = color };
            }
            AddToGroup(candidates[i], g);
        }
        strip.Invalidate();
        UpdateUi(force: true);
    }

    void UngroupAll()
    {
        foreach (var t in tabs) t.Group = null;
        strip.Invalidate();
    }
}
