namespace VidCropper.Backend;

public sealed record UpscaleRequest(string ModelId, int Scale);
public sealed record AiModel(string Id, string Name, string Description, string Family, string FileName, int NativeScale,
    bool VariableScale = false);
public sealed record AiPackage(string Family, string Version, string Url, long Bytes, string Sha256,
    string ArchiveRoot, string Executable, string ModelsDirectory, string SourceUrl, string License, AiModel[] Models,
    string[]? RequiredFiles = null, bool ExtractAll = false, bool CheckOnInstall = true);

public sealed class AiCatalog
{
    public static readonly AiModel[] Models =
    [
        new("nomos-weak", "Nomos8k SPAN Weak", "Относительно чистый исходник, мягкое восстановление.", "span", "4xNomos8k_span_otf_weak", 4),
        new("nomos-medium", "Nomos8k SPAN Medium", "Восстановление умеренных дефектов исходника.", "span", "4xNomos8k_span_otf_medium", 4),
        new("nomos-strong", "Nomos8k SPAN Strong", "Для сильных дефектов; возможна потеря мелких деталей.", "span", "4xNomos8k_span_otf_strong", 4),
        new("realesrgan", "Real-ESRGAN x4plus", "Универсальная модель для обычного видео.", "realesrgan", "realesrgan-x4plus", 4),
        new("anime-video", "Real-ESRGAN AnimeVideo v3", "Для аниме и рисованного видео.", "realesrgan", "realesr-animevideov3", 4, true)
    ];

    // Ordered, application-reviewed releases. Retain earlier descriptors when adding a new release.
    public IReadOnlyList<AiPackage> Packages { get; }
    public AiCatalog() : this([
        new("span", "20240831-055257", "https://github.com/TNTwise/SPAN-ncnn-vulkan/releases/download/20240831-055257/span-ncnn-vulkan-20240831-055257-windows.zip",
            16553410, "ce72105410046e78fccd5a04498427538b0a20d8d30a1bc0f9f476bb9c8bfb6f",
            "span-ncnn-vulkan-20240831-055257-windows/", "span-ncnn-vulkan.exe", "custom_models",
            "https://github.com/TNTwise/SPAN-ncnn-vulkan/tree/20240831-055257", "AGPL-3.0; Nomos8k: CC-BY-4.0 (Helaman / Philip Hofmann)", Models.Where(m => m.Family == "span").ToArray()),
        new("realesrgan", "20220424", "https://github.com/xinntao/Real-ESRGAN/releases/download/v0.2.5.0/realesrgan-ncnn-vulkan-20220424-windows.zip",
            45474481, "abc02804e17982a3be33675e4d471e91ea374e65b70167abc09e31acb412802d",
            "", "realesrgan-ncnn-vulkan.exe", "models", "https://github.com/xinntao/Real-ESRGAN/releases/tag/v0.2.5.0",
            "BSD-3-Clause (Real-ESRGAN); MIT (NCNN executable)", Models.Where(m => m.Family == "realesrgan").ToArray()),
        RifeCatalog.Package]) { }

    public AiCatalog(IReadOnlyList<AiPackage> packages) => Packages = packages;
    public AiPackage Latest(string family) => Packages.LastOrDefault(p => p.Family == family)
        ?? throw new MediaException("Неизвестное семейство AI.");
    public AiPackage Version(string family, string version) => Packages.FirstOrDefault(p => p.Family == family && p.Version == version)
        ?? throw new MediaException("Эта версия AI не поддерживается каталогом приложения.");
    public static AiModel Model(string id) => Models.FirstOrDefault(m => m.Id == id)
        ?? throw new MediaException("Неизвестная модель Upscaler.");
    public static AiModel AnyModel(string id) => Models.Concat(RifeCatalog.Models).FirstOrDefault(m => m.Id == id)
        ?? throw new MediaException("Неизвестная модель AI.");
    public static AiModel Validate(UpscaleRequest request)
    {
        var model = Model(request.ModelId);
        if (request.Scale is not (2 or 3 or 4)) throw new MediaException("Масштаб Upscaler должен быть ×2, ×3 или ×4.");
        return model;
    }
}

// One owner for exports, previews and package changes; acquired before a task is published.
public sealed class ProcessingGate
{
    private readonly SemaphoreSlim semaphore = new(1, 1);
    public bool Busy => semaphore.CurrentCount == 0;
    public IDisposable Acquire()
    {
        if (!semaphore.Wait(0)) throw new MediaException("Дождитесь завершения текущей обработки или установки AI.", 409);
        return new Releaser(semaphore);
    }
    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? owner = semaphore;
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release();
    }
}
