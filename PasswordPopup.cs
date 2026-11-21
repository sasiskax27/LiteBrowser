namespace LiteBrowser;

/// <summary>Всплывающее окно «Сохранить пароль?» под адресной строкой.</summary>
sealed class PasswordPopup : Form
{
    enum Mode { Save, Update, Unlock, Create }

    readonly Label title = new(), site = new(), note = new(), userLabel = new(), passLabel = new(), masterLabel = new();
    readonly TextBox user = new(), pass = new(), master = new();
    readonly Button eye = new(), save = new(), never = new(), later = new();
    Mode mode;
    string origin = "";
    bool busy;

    public Action? OpenManager;
    public Action<string>? NeverForSite;
    public event Action? Saved;

    static int S(float v) => Theme.S(v);

    public PasswordPopup()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.Menu;
        ForeColor = Theme.Text;
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
        Deactivate += (_, _) => { if (!busy) Hide(); };

        foreach (var tb in new[] { user, pass, master })
        {
            tb.BorderStyle = BorderStyle.FixedSingle;
            tb.BackColor = Theme.Field;
            tb.ForeColor = Theme.Text;
        }
        pass.UseSystemPasswordChar = true;
        master.UseSystemPasswordChar = true;
        master.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; save.PerformClick(); } };

        Style(save, primary: true);
        Style(never, primary: false);
        Style(later, primary: false);
        Style(eye, primary: false);
        eye.Text = "";
        eye.Click += (_, _) => { pass.UseSystemPasswordChar = !pass.UseSystemPasswordChar; };
        never.Text = "Никогда для сайта";
        later.Text = "Не сейчас";
        later.Click += (_, _) => Hide();
        never.Click += (_, _) => { NeverForSite?.Invoke(origin); Hide(); };
        save.Click += async (_, _) => await OnSave();

        note.ForeColor = Theme.TextDim;
        site.ForeColor = Theme.TextDim;
        userLabel.Text = "Логин";
        passLabel.Text = "Пароль";
        masterLabel.Text = "Мастер-пароль, чтобы открыть хранилище";
        foreach (var l in new[] { userLabel, passLabel, masterLabel }) l.ForeColor = Theme.TextDim;

        Controls.AddRange(new Control[] { title, site, userLabel, user, passLabel, pass, eye, masterLabel, master, note, save, never, later });
    }

    static void Style(Button b, bool primary)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 0;
        b.BackColor = primary ? Theme.Accent : Theme.Hover;
        b.ForeColor = primary ? Color.FromArgb(13, 16, 32) : Theme.Text;
        b.Cursor = Cursors.Hand;
    }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { Theme.StylePopup(Handle); } catch { }
    }

    public void ShowFor(Control anchor, string origin, string username, string password)
    {
        this.origin = origin;
        bool exists = Vault.Exists, unlocked = Vault.Unlocked;
        if (!exists) mode = Mode.Create;
        else if (!unlocked) mode = Mode.Unlock;
        else mode = Vault.ForOrigin(origin).Any(x => x.Username == username) ? Mode.Update : Mode.Save;

        title.Text = mode switch
        {
            Mode.Create => "Сохранять пароли в LiteBrowser?",
            Mode.Update => "Обновить сохранённый пароль?",
            _ => "Сохранить пароль?",
        };
        site.Text = PasswordTools.Host(origin);
        user.Text = username;
        pass.Text = password;
        pass.UseSystemPasswordChar = true;
        master.Text = "";
        note.Text = mode == Mode.Create
            ? "Создайте менеджер паролей с мастер-паролем. Пароли хранятся только на этом компьютере в зашифрованном виде."
            : "";
        save.Text = mode switch { Mode.Create => "Создать хранилище", Mode.Update => "Обновить", _ => "Сохранить" };
        DoLayout();

        var p = anchor.PointToScreen(new Point(anchor.Width, anchor.Height + S(6)));
        var screen = Screen.FromControl(anchor).WorkingArea;
        Location = new Point(Math.Max(screen.Left + 8, Math.Min(p.X - Width, screen.Right - Width - 8)), p.Y);
        if (!Visible) Show(anchor.FindForm());
        Activate();
        if (mode == Mode.Unlock) master.Focus(); else save.Focus();
    }

    void DoLayout()
    {
        int x = S(18), w = S(344), y = S(16);
        Font = Theme.GetFont("Segoe UI", 9.5f);
        title.Font = Theme.GetFont("Segoe UI", 11.5f, FontStyle.Bold);
        eye.Font = Theme.GetFont(Theme.IconFont, 10f);
        title.SetBounds(x, y, w, S(26)); y += S(26);
        site.SetBounds(x, y, w, S(20)); y += S(28);

        bool fields = mode != Mode.Create;
        foreach (var c in new Control[] { userLabel, user, passLabel, pass, eye }) c.Visible = fields;
        if (fields)
        {
            userLabel.SetBounds(x, y, w, S(18)); y += S(19);
            user.SetBounds(x, y, w, S(28)); y += S(36);
            passLabel.SetBounds(x, y, w, S(18)); y += S(19);
            pass.SetBounds(x, y, w - S(40), S(28));
            eye.SetBounds(x + w - S(34), y - S(1), S(34), S(30)); y += S(38);
        }
        bool unlock = mode == Mode.Unlock;
        masterLabel.Visible = master.Visible = unlock;
        if (unlock)
        {
            masterLabel.SetBounds(x, y, w, S(18)); y += S(19);
            master.SetBounds(x, y, w, S(28)); y += S(36);
        }
        note.Visible = note.Text.Length > 0;
        if (note.Visible) { note.SetBounds(x, y, w, S(40)); y += S(44); }

        never.Visible = mode != Mode.Create;
        later.SetBounds(x + w - S(100), y, S(100), S(34));
        save.SetBounds(later.Left - S(8) - S(150), y, S(150), S(34));
        never.SetBounds(x, y, S(140), S(34));
        if (!never.Visible) save.SetBounds(x, y, w - S(108), S(34));
        y += S(34) + S(16);
        ClientSize = new Size(w + 2 * x, y);
    }

    async Task OnSave()
    {
        if (busy) return;
        if (mode == Mode.Create) { Hide(); OpenManager?.Invoke(); return; }
        busy = true;
        save.Enabled = false;
        try
        {
            if (mode == Mode.Unlock)
            {
                note.Text = "Открываю хранилище…";
                DoLayout();
                var m = master.Text;
                var (ok, err) = await Task.Run(() => Vault.Unlock(m));
                if (!ok) { note.Text = err ?? "Не удалось открыть"; DoLayout(); master.SelectAll(); master.Focus(); return; }
            }
            Vault.Upsert(null, origin, user.Text.Trim(), pass.Text);
            Hide();
            Saved?.Invoke();
        }
        catch (Exception ex)
        {
            note.Text = ex.Message;
            DoLayout();
        }
        finally
        {
            busy = false;
            save.Enabled = true;
        }
    }
}
