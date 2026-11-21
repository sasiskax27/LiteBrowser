using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;

namespace LiteBrowser;

sealed record LocalModel(string Id, string Name, string Description, string Url, long SizeMb);

/// <summary>
/// Lite AI — встроенный ИИ: модель работает прямо в браузере через llama.cpp (LLamaSharp), без интернета и аккаунтов.
/// Движок (~5–50 МБ) и модель (1–4,5 ГБ) скачиваются один раз при первом включении и лежат в папке данных браузера.
/// </summary>
static class LocalAi
{
    const string LlamaVersion = "0.30.0";

    public static readonly LocalModel[] Models =
    {
        new("qwen2.5-1.5b", "Быстрая — Qwen 2.5 1.5B", "Подходит для любого компьютера. Пересказы, перевод, простые вопросы",
            "https://huggingface.co/Qwen/Qwen2.5-1.5B-Instruct-GGUF/resolve/main/qwen2.5-1.5b-instruct-q4_k_m.gguf", 1066),
        new("qwen2.5-3b", "Сбалансированная — Qwen 2.5 3B", "Заметно умнее, работает на большинстве компьютеров",
            "https://huggingface.co/Qwen/Qwen2.5-3B-Instruct-GGUF/resolve/main/qwen2.5-3b-instruct-q4_k_m.gguf", 2007),
        new("gemma3-4b", "Gemma 3 4B (Google)", "Хорошо пишет по-русски, нужен компьютер помощнее",
            "https://huggingface.co/bartowski/google_gemma-3-4b-it-GGUF/resolve/main/google_gemma-3-4b-it-Q4_K_M.gguf", 2374),
        new("qwen2.5-7b", "Умная — Qwen 2.5 7B", "Лучшие ответы, нужна видеокарта (от 6 ГБ памяти)",
            "https://huggingface.co/bartowski/Qwen2.5-7B-Instruct-GGUF/resolve/main/Qwen2.5-7B-Instruct-Q4_K_M.gguf", 4466),
    };

    static string Dir => Path.Combine(AppEnv.DataDir, "ai");
    static string ModelPath(LocalModel m) => Path.Combine(Dir, "models", m.Id + ".gguf");
    static string RuntimeDir(string backend) => Path.Combine(Dir, "runtime", backend);

    public static LocalModel? Find(string id) => Models.FirstOrDefault(m => m.Id == id);
    public static bool IsDownloaded(LocalModel m) => File.Exists(ModelPath(m));
    public static List<LocalModel> Downloaded() => Models.Where(IsDownloaded).ToList();

    // Состояние загрузки — для страницы помощника
    public static string? DownloadingId { get; private set; }
    public static double DownloadProgress { get; private set; }
    public static string Status { get; private set; } = "";
    static CancellationTokenSource? downloadCts;

    // Загруженная модель
    static LLamaWeights? weights;
    static ModelParams? modelParams;
    static string? loadedId;
    static string? configuredBackend;
    static readonly SemaphoreSlim gate = new(1, 1);
    public static string? LoadedBackend => configuredBackend;

    static string CpuVariant() => Avx512F.IsSupported ? "avx512" : Avx2.IsSupported ? "avx2" : Avx.IsSupported ? "avx" : "noavx";

    /// <summary>Скачать движок и модель. Повторный вызов во время загрузки — ничего не делает.</summary>
    public static async Task DownloadAsync(LocalModel m, bool gpu)
    {
        if (DownloadingId != null) return;
        DownloadingId = m.Id;
        DownloadProgress = 0;
        downloadCts = new CancellationTokenSource();
        var ct = downloadCts.Token;
        try
        {
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("LiteBrowser/" + AppEnv.Version);
            await EnsureRuntime(http, "cpu", ct);
            if (gpu) { try { await EnsureRuntime(http, "vulkan", ct); } catch (Exception ex) { Status = "Движок для видеокарты не скачался, будет процессор: " + ex.Message; } }

            var path = ModelPath(m);
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var part = path + ".part";
                long have = File.Exists(part) ? new FileInfo(part).Length : 0;
                using var req = new HttpRequestMessage(HttpMethod.Get, m.Url);
                if (have > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null); // докачка
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                if (resp.StatusCode != System.Net.HttpStatusCode.PartialContent) have = 0;
                long total = have + (resp.Content.Headers.ContentLength ?? m.SizeMb * 1048576);
                await using (var src = await resp.Content.ReadAsStreamAsync(ct))
                await using (var dst = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create))
                {
                    var buf = new byte[1 << 20];
                    long got = have;
                    int n;
                    while ((n = await src.ReadAsync(buf, ct)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, n), ct);
                        got += n;
                        DownloadProgress = (double)got / total;
                        Status = $"Скачиваю модель: {got / 1048576} из {total / 1048576} МБ";
                    }
                }
                if (new FileInfo(part).Length < 50_000_000) throw new InvalidDataException("Модель скачалась не полностью");
                File.Move(part, path, true);
            }
            Status = "Модель готова";
        }
        catch (OperationCanceledException) { Status = "Загрузка остановлена (можно продолжить позже)"; }
        catch (Exception ex) { Status = "Ошибка загрузки: " + ex.Message; }
        finally { DownloadingId = null; }
    }

    public static void CancelDownload() => downloadCts?.Cancel();

    public static void Delete(LocalModel m)
    {
        if (loadedId == m.Id) Unload();
        try { File.Delete(ModelPath(m)); File.Delete(ModelPath(m) + ".part"); } catch { }
    }

    /// <summary>Нативный движок llama.cpp: берём из официального пакета LLamaSharp на nuget.org только нужные файлы.</summary>
    static async Task EnsureRuntime(HttpClient http, string backend, CancellationToken ct)
    {
        var dir = RuntimeDir(backend);
        if (File.Exists(Path.Combine(dir, "llama.dll"))) return;
        Status = backend == "vulkan" ? "Скачиваю движок для видеокарты…" : "Скачиваю движок ИИ…";
        var pkg = backend == "vulkan" ? "LLamaSharp.Backend.Vulkan.Windows" : "LLamaSharp.Backend.Cpu";
        var folder = backend == "vulkan" ? "vulkan" : CpuVariant();
        var bytes = await http.GetByteArrayAsync($"https://www.nuget.org/api/v2/package/{pkg}/{LlamaVersion}", ct);
        var prefix = $"LLamaSharpRuntimes/win-x64/native/{folder}/";
        var tmp = dir + ".tmp";
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        Directory.CreateDirectory(tmp);
        using (var zip = new ZipArchive(new MemoryStream(bytes)))
            foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith(prefix) && e.Name.EndsWith(".dll")))
                e.ExtractToFile(Path.Combine(tmp, e.Name), true);
        if (!File.Exists(Path.Combine(tmp, "llama.dll"))) throw new InvalidDataException("В пакете нет llama.dll");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.Move(tmp, dir);
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetDllDirectory(string? path);

    /// <summary>Выбрать нативную библиотеку. Делается один раз за запуск: сменить CPU↔GPU можно только перезапуском.</summary>
    static void ConfigureNative(bool gpu)
    {
        if (configuredBackend != null) return;
        var backend = gpu && File.Exists(Path.Combine(RuntimeDir("vulkan"), "llama.dll")) ? "vulkan" : "cpu";
        var dir = RuntimeDir(backend);
        // В пакете Vulkan нет процессорной части ggml — берём её из процессорного движка той же версии
        if (backend == "vulkan" && !File.Exists(Path.Combine(dir, "ggml-cpu.dll")) && File.Exists(Path.Combine(RuntimeDir("cpu"), "ggml-cpu.dll")))
            File.Copy(Path.Combine(RuntimeDir("cpu"), "ggml-cpu.dll"), Path.Combine(dir, "ggml-cpu.dll"));
        if (!File.Exists(Path.Combine(dir, "llama.dll"))) throw new InvalidOperationException("Движок ИИ ещё не скачан");
        SetDllDirectory(dir); // чтобы нашлись ggml*.dll рядом с llama.dll
        // Подменяем загрузку нативных библиотек для LLamaSharp: «llama» и «mtmd» — из папки скачанного движка
        NativeLibrary.SetDllImportResolver(typeof(LLamaWeights).Assembly, (name, _, _) =>
        {
            var file = Path.Combine(dir, (name.EndsWith(".dll") ? name : name + ".dll"));
            return File.Exists(file) ? NativeLibrary.Load(file) : IntPtr.Zero;
        });
        configuredBackend = backend;
    }

    public static async Task EnsureLoadedAsync(LocalModel m, bool gpu, CancellationToken ct)
    {
        if (loadedId == m.Id && weights != null) return;
        await gate.WaitAsync(ct);
        try
        {
            if (loadedId == m.Id && weights != null) return;
            if (!IsDownloaded(m)) throw new InvalidOperationException("Модель ещё не скачана");
            Unload();
            Status = "Загружаю модель в память…";
            await Task.Run(() =>
            {
                ConfigureNative(gpu);
                var prev = Environment.CurrentDirectory;
                try
                {
                    Environment.CurrentDirectory = RuntimeDir(configuredBackend!); // ggml ищет свои модули и тут
                    modelParams = new ModelParams(ModelPath(m))
                    {
                        ContextSize = 8192,
                        GpuLayerCount = configuredBackend == "vulkan" ? 99 : 0,
                    };
                    weights = LLamaWeights.LoadFromFile(modelParams);
                }
                finally { Environment.CurrentDirectory = prev; }
            }, ct);
            loadedId = m.Id;
            Status = configuredBackend == "vulkan" ? "Модель загружена (видеокарта)" : "Модель загружена (процессор)";
        }
        finally { gate.Release(); }
    }

    public static void Unload()
    {
        weights?.Dispose();
        weights = null;
        loadedId = null;
    }

    static string BuildPrompt(IReadOnlyList<AiMessage> messages)
    {
        var t = new LLamaTemplate(weights!, false) { AddAssistant = true };
        foreach (var m in messages) t.Add(m.Role, m.Content);
        var applied = t.Apply();
        return LLamaTemplate.Encoding.GetString(applied);
    }

    public static async Task GenerateAsync(IReadOnlyList<AiMessage> messages, Action<string> onToken, CancellationToken ct)
    {
        if (weights == null || modelParams == null) throw new InvalidOperationException("Модель не загружена");
        string prompt = BuildPrompt(messages);
        var executor = new StatelessExecutor(weights, modelParams);
        var inference = new InferenceParams
        {
            MaxTokens = 1500,
            AntiPrompts = new List<string> { "<|im_end|>", "<end_of_turn>", "<|eot_id|>", "<|endoftext|>" },
            SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0.6f, TopP = 0.9f, RepeatPenalty = 1.1f },
        };
        await foreach (var token in executor.InferAsync(prompt, inference, ct))
        {
            var clean = token.Replace("<|im_end|>", "").Replace("<end_of_turn>", "").Replace("<|eot_id|>", "");
            if (clean.Length > 0) onToken(clean);
        }
    }
}

/// <summary>Провайдер «Lite AI (встроенный)» для общего интерфейса ИИ.</summary>
sealed class LocalProvider : IAiProvider
{
    readonly LocalModel model;
    readonly bool gpu;
    public LocalProvider(LocalModel model, bool gpu) { this.model = model; this.gpu = gpu; }
    public string Name => "Lite AI (встроенный)";
    public Task<List<string>> ModelsAsync(CancellationToken ct) => Task.FromResult(LocalAi.Downloaded().Select(m => m.Id).ToList());

    public async Task StreamAsync(IReadOnlyList<AiMessage> messages, Action<string> onToken, CancellationToken ct)
    {
        await LocalAi.EnsureLoadedAsync(model, gpu, ct);
        await Task.Run(() => LocalAi.GenerateAsync(messages, onToken, ct), ct);
    }
}
