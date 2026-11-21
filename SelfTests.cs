using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace LiteBrowser;

/// <summary>Диагностика загрузки: качает файл в отдельном профиле и пишет, что происходит.</summary>
static class DownloadSelfTest
{
    public static void Run(string url, string outFile, bool handled)
    {
        var log = new List<string>();
        var root = Path.Combine(Path.GetTempPath(), "LiteDlTest-" + Guid.NewGuid().ToString("N"));
        var form = new Form { Width = 900, Height = 700, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-2000, -2000) };
        var view = new WebView2 { Dock = DockStyle.Fill };
        form.Controls.Add(view);
        form.Shown += async (_, _) =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(root, "data"));
                await view.EnsureCoreWebView2Async(env);
                view.CoreWebView2.Settings.IsReputationCheckingRequired = true;
                if (outFile.EndsWith("-filter.txt"))
                {
                    // Как в браузере с блокировщиком: перехватываем все запросы
                    view.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
                    view.CoreWebView2.WebResourceRequested += (_, e) => { var u = e.Request.Uri; _ = u.Length; };
                    log.Add("перехват всех запросов включён");
                }
                var done = new TaskCompletionSource<bool>();
                view.CoreWebView2.DownloadStarting += (_, e) =>
                {
                    e.Handled = handled;
                    e.ResultFilePath = Path.Combine(root, "file.bin");
                    var op = e.DownloadOperation;
                    log.Add($"{sw.ElapsedMilliseconds} мс: загрузка началась, размер {op.TotalBytesToReceive}");
                    long last = -1;
                    op.BytesReceivedChanged += (_, _) => { if (op.BytesReceived - last > 10_000_000 || op.BytesReceived == (long)(op.TotalBytesToReceive ?? 0)) { last = op.BytesReceived; log.Add($"{sw.ElapsedMilliseconds} мс: получено {op.BytesReceived / 1048576} МБ"); } };
                    op.StateChanged += (_, _) =>
                    {
                        log.Add($"{sw.ElapsedMilliseconds} мс: состояние {op.State}, причина {op.InterruptReason}, получено {op.BytesReceived}");
                        if (op.State != CoreWebView2DownloadState.InProgress) done.TrySetResult(true);
                    };
                };
                view.CoreWebView2.Navigate(url);
                if (await Task.WhenAny(done.Task, Task.Delay(120_000)) != done.Task) log.Add($"{sw.ElapsedMilliseconds} мс: ЗАВИСЛА — за 2 минуты не завершилась");
            }
            catch (Exception ex) { log.Add("исключение: " + ex.Message); }
            finally { File.WriteAllLines(outFile, log); form.Close(); }
        };
        Application.Run(form);
        try { Directory.Delete(root, true); } catch { }
    }
}

/// <summary>Самотест расширений в отдельной временной папке (не трогает данные пользователя).</summary>
static class ExtensionSelfTest
{
    public static void Run(string outFile)
    {
        var log = new List<string>();
        void Check(string name, bool ok, string? extra = null) => log.Add((ok ? "OK   " : "FAIL ") + name + (extra != null ? " — " + extra : ""));
        var root = Path.Combine(Path.GetTempPath(), "LiteExtTest-" + Guid.NewGuid().ToString("N"));
        Extensions.DirOverride = Path.Combine(root, "extensions");
        Directory.CreateDirectory(Extensions.DirOverride);

        var form = new Form { Width = 900, Height = 700, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-2000, -2000) };
        var view = new WebView2 { Dock = DockStyle.Fill };
        form.Controls.Add(view);
        form.Shown += async (_, _) =>
        {
            try
            {
                var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(root, "data"),
                    new CoreWebView2EnvironmentOptions { AreBrowserExtensionsEnabled = true });
                await view.EnsureCoreWebView2Async(env);
                view.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true; // расширения любят открывать свои страницы — в тесте не нужно
                var profile = view.CoreWebView2.Profile;
                log.Add("движок: " + env.BrowserVersionString);

                async Task<ExtensionInfo?> Try(string label, string url)
                {
                    try
                    {
                        var r = Extensions.Parse(url) ?? throw new Exception("ссылка не распознана");
                        var info = await Extensions.Download(r, env.BrowserVersionString);
                        Check(label + ": скачано и распаковано", true, $"{info.Name} {info.Version}, MV{info.ManifestVersion}, права: {string.Join("; ", Extensions.DescribePermissions(info))}");
                        return info;
                    }
                    catch (Exception ex) { Check(label, false, ex.Message); return null; }
                }

                var chrome = await Try("Chrome Web Store (Dark Reader)", "https://chromewebstore.google.com/detail/dark-reader/eimadpbcbfnmbkopoojfekhnkhdbieeh");
                var edge = await Try("Edge Add-ons (Dark Reader)", "https://microsoftedge.microsoft.com/addons/detail/dark-reader/ifoakfbpdcdoeenechcleahebpibofpc");
                if (edge != null) Extensions.Discard(edge);
                try
                {
                    // Подмена: другой ID при том же файле должна отклоняться
                    var raw = await new HttpClient().GetByteArrayAsync("https://clients2.google.com/service/update2/crx?response=redirect&prodversion=157.0&acceptformat=crx3&x=id%3Deimadpbcbfnmbkopoojfekhnkhdbieeh%26uc");
                    bool rejectedId = false, rejectedTamper = false;
                    try { CrxVerifier.Verify(raw, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"); } catch { rejectedId = true; }
                    raw[^100] ^= 0xFF;
                    try { CrxVerifier.Verify(raw, "eimadpbcbfnmbkopoojfekhnkhdbieeh"); } catch { rejectedTamper = true; }
                    Check("подменённый ID отклоняется", rejectedId);
                    Check("изменённый файл отклоняется по подписи", rejectedTamper);
                }
                catch (Exception ex) { Check("проверка подмены", false, ex.Message); }
                try
                {
                    var ff = Extensions.Parse("https://addons.mozilla.org/ru/firefox/addon/darkreader/")!;
                    var fi = await Extensions.Download(ff, env.BrowserVersionString);
                    Check("Firefox Add-ons (Dark Reader): скачано", true, $"{fi.Name} {fi.Version}");
                    Extensions.Discard(fi);
                }
                catch (NotSupportedException ex) { Check("Firefox Add-ons: понятная ошибка о несовместимости", true, ex.Message); }
                catch (Exception ex) { Check("Firefox Add-ons", false, ex.Message); }

                if (chrome != null)
                {
                    var added = await Extensions.Install(profile, chrome);
                    var list = await profile.GetBrowserExtensionsAsync();
                    Check("расширение установлено в движок", list.Any(x => x.Id == added.Id), $"id {added.Id}, «{added.Name}», включено: {added.IsEnabled}");
                    if (chrome.Popup != null)
                    {
                        var done = new TaskCompletionSource<bool>();
                        view.CoreWebView2.NavigationCompleted += (_, e) => done.TrySetResult(e.IsSuccess);
                        view.CoreWebView2.Navigate($"chrome-extension://{added.Id}/{chrome.Popup.TrimStart('/')}");
                        var ok = await Task.WhenAny(done.Task, Task.Delay(15000)) == done.Task && done.Task.Result;
                        await Task.Delay(1500);
                        var html = ok ? await view.CoreWebView2.ExecuteScriptAsync("document.body ? document.body.innerText.length : -1") : "-";
                        Check("окно расширения открывается во вкладке", ok, "длина текста на странице: " + html);
                    }
                    await Extensions.Remove(profile, added.Id);
                    Check("расширение удаляется", !(await profile.GetBrowserExtensionsAsync()).Any(x => x.Id == added.Id));
                }
            }
            catch (Exception ex) { log.Add("FAIL исключение: " + ex); }
            finally
            {
                File.WriteAllLines(outFile, log);
                form.Close();
            }
        };
        Application.Run(form);
        try { Directory.Delete(root, true); } catch { }
    }
}

/// <summary>Самотест лёгких инструментов разработчика и палитры команд: настоящий WebView2 во временной папке.</summary>
static class DevToolsSelfTest
{
    public static void Run(string outFile)
    {
        var log = new List<string>();
        void Check(string name, bool ok, string? extra = null) => log.Add((ok ? "OK   " : "FAIL ") + name + (extra != null ? " — " + extra : ""));
        var root = Path.Combine(Path.GetTempPath(), "LiteDevTest-" + Guid.NewGuid().ToString("N"));
        var form = new Form { Width = 900, Height = 700, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-2000, -2000) };
        var view = new WebView2 { Dock = DockStyle.Fill };
        form.Controls.Add(view);
        form.Shown += async (_, _) =>
        {
            try
            {
                // Палитра команд: нечёткий поиск
                Check("поиск «вкл» находит «Новая вкладка»", CommandPalette.Score("Новая вкладка", "вкл") > 0);
                Check("«нвк» находит «Новая вкладка» по буквам", CommandPalette.Score("Новая вкладка", "нвк") > 0);
                Check("начало строки важнее середины", CommandPalette.Score("История", "ист") > CommandPalette.Score("Очистить историю", "ист"));
                Check("«zzz» ничего не находит", CommandPalette.Score("Новая вкладка", "zzz") == 0);

                var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(root, "data"));
                await view.EnsureCoreWebView2Async(env);
                var core = view.CoreWebView2;
                var dev = new DevMonitor(core);
                await dev.AttachAsync();
                var loaded = new TaskCompletionSource<bool>();
                core.NavigationCompleted += (_, e) => loaded.TrySetResult(e.IsSuccess);
                dev.OnNavigation("https://example.com/");
                core.Navigate("https://example.com/");
                bool ok = await Task.WhenAny(loaded.Task, Task.Delay(30_000)) == loaded.Task && loaded.Task.Result;
                Check("страница загрузилась", ok);
                await core.ExecuteScriptAsync("console.log('lite-test', {a: 1}, [1,2]); console.warn('lite-warn'); setTimeout(() => { throw new Error('lite-boom'); }, 0); fetch('/lite-404').catch(() => {});");
                await Task.Delay(2500);
                var doc = dev.Net.FirstOrDefault(n => n.Type == "Document");
                Check("сеть: запрос страницы записан", doc != null, doc == null ? null : $"{doc.Status} {doc.Url} {doc.Size} Б {doc.Ms:0} мс");
                Check("сеть: статус 200", doc?.Status == 200);
                var f = dev.Net.FirstOrDefault(n => n.Url.Contains("lite-404"));
                Check("сеть: fetch записан со статусом 404", f?.Status == 404, f == null ? "нет записи" : f.Status.ToString());
                var logEntry = dev.Console.FirstOrDefault(c => c.Text.Contains("lite-test"));
                Check("консоль: console.log пойман", logEntry != null, logEntry?.Text);
                Check("консоль: объект показан", logEntry?.Text.Contains("a: 1") == true || logEntry?.Text.Contains("\"a\"") == true, logEntry?.Text);
                Check("консоль: предупреждение", dev.Console.Any(c => c.Level == "warn" && c.Text.Contains("lite-warn")));
                var err = dev.Console.FirstOrDefault(c => c.Level == "error" && c.Text.Contains("lite-boom"));
                Check("консоль: необработанная ошибка поймана", err != null, err?.Text.Split('\n')[0]);
                var (r1, e1) = await dev.EvaluateAsync("1 + 1");
                Check("выполнение: 1 + 1 = 2", r1 == "2" && !e1, r1);
                var (r2, _) = await dev.EvaluateAsync("document.title");
                Check("выполнение: заголовок страницы", r2.Contains("Example"), r2);
                var (r3, e3) = await dev.EvaluateAsync("nope.x");
                Check("выполнение: ошибка возвращается", e3, r3.Split('\n')[0]);
                var (r4, _) = await dev.EvaluateAsync("let q = 5; q * 2");
                Check("выполнение: let в консоли", r4 == "10", r4);
                var m = await dev.MetricsAsync();
                Check("скорость: метрики получены", m.GetValueOrDefault("Nodes") > 0, $"узлов {m.GetValueOrDefault("Nodes")}, память {m.GetValueOrDefault("JSHeapUsedSize") / 1024:0} КБ");
                var timing = System.Text.Json.JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync(DevMonitor.TimingScript));
                Check("скорость: замеры страницы", timing?.Contains("\"ttfb\"") == true, timing?.Length > 160 ? timing[..160] : timing);
                dev.OnNavigation("https://example.org/");
                Check("переход очищает журнал сети", dev.Net.Count == 0);
                dev.Detach();
                Check("отключение от вкладки", !dev.Attached);
                Check("загрузки: .exe считается программой", DownloadGuard.Risk(@"C:\x\setup.EXE") == "exec");
                Check("загрузки: .pdf безопасен", DownloadGuard.Risk(@"C:\x\doc.pdf") == "");
                var tmp = Path.Combine(root, "t.bin");
                File.WriteAllText(tmp, "abc");
                Check("загрузки: SHA-256", await DownloadGuard.Sha256Async(tmp) == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
                DownloadGuard.MarkOfTheWeb(tmp, "https://example.com/file?secret=1");
                var zone = File.ReadAllText(tmp + ":Zone.Identifier");
                Check("загрузки: метка «из интернета»", zone.Contains("ZoneId=3") && !zone.Contains("secret"), zone.Replace("\r\n", " | "));
            }
            catch (Exception ex) { log.Add("FAIL исключение: " + ex); }
            finally { File.WriteAllLines(outFile, log); form.Close(); }
        };
        Application.Run(form);
        try { Directory.Delete(root, true); } catch { }
    }
}
