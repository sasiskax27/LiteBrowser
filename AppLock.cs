using System.Security.Cryptography;
using System.Text;

namespace LiteBrowser;

/// <summary>
/// Пароль (PIN) на браузер: спрашивается при запуске и после блокировки Windows (Win+L).
/// Хранится только соль и хеш PBKDF2 — сам PIN нигде не записан.
/// </summary>
static class AppLock
{
    public static bool Enabled => AppEnv.Settings.LockPinHash.Length > 0;

    public static string Hash(string pin, byte[]? salt = null)
    {
        salt ??= RandomNumberGenerator.GetBytes(16);
        var h = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, 200_000, HashAlgorithmName.SHA256, 32);
        return Convert.ToBase64String(salt) + ":" + Convert.ToBase64String(h);
    }

    public static bool Check(string pin)
    {
        var parts = AppEnv.Settings.LockPinHash.Split(':');
        if (parts.Length != 2) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[0]);
            var want = Convert.FromBase64String(parts[1]);
            var got = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, 200_000, HashAlgorithmName.SHA256, 32);
            return CryptographicOperations.FixedTimeEquals(want, got);
        }
        catch { return false; }
    }

    /// <summary>Показать окно ввода PIN. true — PIN верный; false — пользователь закрыл окно.</summary>
    public static bool Prompt(IWin32Window? owner = null)
    {
        using var f = new LockForm();
        return (owner == null ? f.ShowDialog() : f.ShowDialog(owner)) == DialogResult.OK;
    }
}

/// <summary>Окно блокировки браузера: тёмное, с полем PIN и паузой после неверных попыток.</summary>
sealed class LockForm : Form
{
    readonly TextBox box = new() { UseSystemPasswordChar = true, BorderStyle = BorderStyle.FixedSingle, TextAlign = HorizontalAlignment.Center, MaxLength = 32 };
    readonly Label info = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleCenter };
    readonly Button ok = new() { FlatStyle = FlatStyle.Flat };
    int fails;
    DateTime lockedUntil = DateTime.MinValue;
    static int S(float v) => Theme.S(v);

    public LockForm()
    {
        Text = "LiteBrowser";
        Icon = Theme.MakeAppIcon();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        BackColor = Theme.Toolbar;
        ForeColor = Theme.Text;
        ClientSize = new Size(S(340), S(220));
        var title = new Label { Text = L.T("Браузер заблокирован"), AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Font = Theme.GetFont("Segoe UI", 13f, FontStyle.Bold), ForeColor = Theme.Text };
        title.SetBounds(0, S(20), ClientSize.Width, S(32));
        info.Text = L.T("Введите PIN-код, чтобы открыть LiteBrowser");
        info.Font = Theme.GetFont("Segoe UI", 9.5f);
        info.ForeColor = Theme.TextDim;
        info.SetBounds(S(10), S(54), ClientSize.Width - S(20), S(36));
        box.Font = new Font("Segoe UI", 14f);
        box.BackColor = Theme.Field;
        box.ForeColor = Theme.Text;
        box.SetBounds(S(70), S(98), S(200), S(34));
        ok.Text = L.T("Открыть");
        ok.BackColor = Theme.Accent;
        ok.ForeColor = Color.FromArgb(13, 16, 32);
        ok.Font = Theme.GetFont("Segoe UI", 10f, FontStyle.Bold);
        ok.FlatAppearance.BorderSize = 0;
        ok.SetBounds(S(70), S(148), S(200), S(38));
        ok.Click += (_, _) => TryUnlock();
        Controls.AddRange(new Control[] { title, info, box, ok });
        AcceptButton = ok;
        Shown += (_, _) => { Activate(); box.Focus(); };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { Theme.StyleTitleBar(Handle, Theme.Toolbar); } catch { }
    }

    void TryUnlock()
    {
        if (DateTime.Now < lockedUntil) { info.Text = L.T("Подождите ") + (int)Math.Ceiling((lockedUntil - DateTime.Now).TotalSeconds) + L.T(" с"); return; }
        if (AppLock.Check(box.Text)) { DialogResult = DialogResult.OK; Close(); return; }
        fails++;
        box.Clear();
        // После 5 ошибок — растущая пауза, чтобы PIN нельзя было быстро подобрать
        if (fails >= 5) { lockedUntil = DateTime.Now.AddSeconds(Math.Min(300, 15 * (fails - 4))); info.Text = L.T("Слишком много попыток — подождите немного"); }
        else info.Text = L.T("Неверный PIN-код");
        info.ForeColor = Theme.Danger;
    }
}
