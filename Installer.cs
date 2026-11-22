using System.Diagnostics;
using Microsoft.Win32;

namespace LiteBrowser;

/// <summary>
/// Установка без прав администратора: программа в %LOCALAPPDATA%\Programs\LiteBrowser, ярлыки,
/// и запись в «Параметры → Приложения» с кнопкой «Удалить».
/// </summary>
static class Installer
{
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\LiteBrowser";
    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "LiteBrowser");
    static string Exe => Path.Combine(InstallDir, "LiteBrowser.exe");
    static string DataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiteBrowser");

    /// <summary>Запись в списке установленных программ (обновляем версию при каждом запуске установленной копии).</summary>
    public static void RegisterUninstall()
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(UninstallKey);
            if ((k.GetValue("DisplayVersion") as string) == AppEnv.Version && (k.GetValue("UninstallString") as string)?.Contains(Exe) == true) return;
            k.SetValue("DisplayName", "LiteBrowser");
            k.SetValue("DisplayVersion", AppEnv.Version);
            k.SetValue("Publisher", "LiteBrowser");
            k.SetValue("DisplayIcon", Exe + ",0");
            k.SetValue("InstallLocation", InstallDir);
            k.SetValue("UninstallString", $"\"{Exe}\" --uninstall");
            k.SetValue("QuietUninstallString", $"\"{Exe}\" --uninstall --quiet");
            k.SetValue("URLInfoAbout", "https://github.com/sasiskax27/LiteBrowser");
            k.SetValue("HelpLink", "https://github.com/sasiskax27/LiteBrowser/issues");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            try { k.SetValue("EstimatedSize", (int)(new FileInfo(Exe).Length / 1024), RegistryValueKind.DWord); } catch { }
        }
        catch { }
    }

    /// <summary>--install: скопировать себя в папку программ, создать ярлыки и запись для удаления. --quiet — без запуска и окон.</summary>
    public static void Install(bool quiet)
    {
        SystemIntegration.EnsureInstalled();
        try { SystemIntegration.CreateShortcuts(); } catch { }
        RegisterUninstall();
        if (!quiet) Process.Start(new ProcessStartInfo(Exe) { UseShellExecute = false });
    }

    /// <summary>--uninstall: убрать ярлыки, записи в реестре и саму программу; данные — по желанию.</summary>
    public static void Uninstall(bool quiet)
    {
        ApplicationConfiguration.Initialize();
        // Браузер не должен быть открыт — иначе файлы заняты
        var me = Environment.ProcessId;
        var running = Process.GetProcessesByName("LiteBrowser").Where(p => p.Id != me).ToList();
        if (running.Count > 0)
        {
            if (quiet) foreach (var p in running) { try { p.CloseMainWindow(); if (!p.WaitForExit(5000)) p.Kill(); } catch { } }
            else
            {
                var r = MessageBox.Show("LiteBrowser сейчас открыт. Закрыть его и продолжить удаление?", "Удаление LiteBrowser",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                if (r != DialogResult.OK) return;
                foreach (var p in running) { try { p.CloseMainWindow(); if (!p.WaitForExit(8000)) p.Kill(); } catch { } }
            }
        }
        bool removeData = false;
        if (!quiet)
        {
            var r = MessageBox.Show("Удалить LiteBrowser с компьютера?\n\n«Да» — удалить программу и все данные (закладки, историю, пароли, модели ИИ).\n" +
                                    "«Нет» — удалить только программу, данные оставить (пригодятся при повторной установке).",
                "Удаление LiteBrowser", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (r == DialogResult.Cancel) return;
            removeData = r == DialogResult.Yes;
        }

        foreach (var dir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.Programs) })
            try { File.Delete(Path.Combine(dir, "LiteBrowser.lnk")); } catch { }
        var cu = Registry.CurrentUser;
        foreach (var key in new[] { @"Software\Clients\StartMenuInternet\LiteBrowser", @"Software\Classes\LiteBrowserURL", @"Software\Classes\LiteBrowserHTML", UninstallKey })
            try { cu.DeleteSubKeyTree(key, false); } catch { }
        try { using var ra = cu.OpenSubKey(@"Software\RegisteredApplications", true); ra?.DeleteValue("LiteBrowser", false); } catch { }

        // Сам exe сейчас запущен — удаляем папку после выхода
        var targets = new List<string> { InstallDir };
        if (removeData) targets.Add(DataRoot);
        var cmd = "timeout /t 2 /nobreak >nul & " + string.Join(" & ", targets.Select(t => $"rmdir /s /q \"{t}\""));
        Process.Start(new ProcessStartInfo("cmd.exe", "/c " + cmd) { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
        if (!quiet) MessageBox.Show("LiteBrowser удалён." + (removeData ? "" : "\nВаши данные остались в папке " + DataRoot), "Удаление LiteBrowser");
    }
}
