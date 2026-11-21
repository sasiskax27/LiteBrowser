using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace LiteBrowser;

/// <summary>Чтение без интернета: сохранение страниц и список сохранённого.</summary>
sealed partial class MainForm
{
    async Task SaveOffline()
    {
        var t = active;
        if (t?.Core == null) return;
        try
        {
            var p = await Offline.SaveAsync(t.Core, t.Title);
            MessageBox.Show(this, $"Страница сохранена ({Theme.FormatSize(p.Size)}).\nОна откроется даже без интернета: меню → «Сохранённые страницы».",
                "Чтение без интернета", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Чтение без интернета"); }
    }

    static object OfflineList() => new
    {
        type = "offList",
        total = Offline.TotalSize,
        items = Offline.All.Select(p => new { id = p.Id, url = p.Url, title = p.Title, time = p.Time, size = p.Size }),
    };

    void HandleOfflineMessage(CoreWebView2 core, string type, JsonElement root)
    {
        string Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        switch (type)
        {
            case "off:list":
                Post(core, OfflineList());
                break;
            case "off:open":
                if (Offline.Get(Str("id")) is { } p) Navigate(Offline.FileUrl(p));
                break;
            case "off:remove":
                Offline.Remove(Str("id"));
                Post(core, OfflineList());
                break;
        }
    }

    /// <summary>Нет интернета, а страница сохранена — открываем сохранённую копию.</summary>
    bool TryOfflineFallback(CoreWebView2 core, string failedUrl, CoreWebView2WebErrorStatus status)
    {
        if (status is not (CoreWebView2WebErrorStatus.HostNameNotResolved or CoreWebView2WebErrorStatus.CannotConnect or
            CoreWebView2WebErrorStatus.Disconnected or CoreWebView2WebErrorStatus.ConnectionAborted or CoreWebView2WebErrorStatus.Timeout or
            CoreWebView2WebErrorStatus.ServerUnreachable)) return false;
        if (Offline.ByUrl(failedUrl) is not { } p) return false;
        BeginInvoke(() => core.Navigate(Offline.FileUrl(p)));
        return true;
    }
}
