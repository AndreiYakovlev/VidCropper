namespace VidCropper.Backend;

public sealed record UpscaleRequest(string ModelId, int Scale);
public sealed record AiModel(string Id, string Name, string Description, string Family, string FileName, int NativeScale,
    int[] AllowedScales, bool VariableScale = false);
public enum AiArchiveKind { TarGzip }
public sealed record AiPackageArtifact(string Url, long Bytes, string Sha256, AiArchiveKind Kind,
    string ArchiveRoot, string DestinationDirectory, string SourceUrl, string License);
public sealed record AiPackage(string Family, string Version, string Url, long Bytes, string Sha256,
    string ArchiveRoot, string Executable, string ModelsDirectory, string SourceUrl, string License, AiModel[] Models,
    string[]? RequiredFiles = null, bool ExtractAll = false, bool CheckOnInstall = true,
    AiPackageArtifact[]? SupplementalArtifacts = null, bool PassNativeScale = false);

public sealed class AiCatalog
{
    public static readonly AiModel[] Models =
    [
        new("nomos-weak", "Nomos8k SPAN Weak", "Относительно чистый исходник, мягкое восстановление.",
            "span", "4xNomos8k_span_otf_weak", 4, [2, 3, 4]),
        new("nomos-medium", "Nomos8k SPAN Medium", "Восстановление умеренных дефектов исходника.",
            "span", "4xNomos8k_span_otf_medium", 4, [2, 3, 4]),
        new("nomos-strong", "Nomos8k SPAN Strong", "Для сильных дефектов; возможна потеря мелких деталей.",
            "span", "4xNomos8k_span_otf_strong", 4, [2, 3, 4]),
        new("spankendata", "4x-SPANkendata", "Для чистого реалистичного видео и фотографий без сильных дефектов.",
            "span", "4xSPANkendata", 4, [2, 3, 4]),
        new("realesrgan", "Real-ESRGAN x4plus", "Универсальная модель для обычного видео.",
            "realesrgan", "realesrgan-x4plus", 4, [2, 3, 4]),
        new("realesr-general-x4v3", "Real-ESRGAN General x4v3 (RealisticVideo)",
            "Быстрая компактная модель для реалистичного видео; мягкое восстановление и умеренное шумоподавление.",
            "upscayl", "realesr-general-x4v3", 4, [2, 3, 4]),
        new("openproteus", "OpenProteus", "Для чистого HD/FHD-видео; бережно сохраняет исходные детали.",
            "upscayl", "2x_OpenProteus_Compact_i2_70K", 2, [2]),
        new("anime-video", "Real-ESRGAN AnimeVideo v3", "Для аниме и рисованного видео.",
            "realesrgan", "realesr-animevideov3", 4, [2, 3, 4], true)
    ];

    // Ordered, application-reviewed releases. Retain earlier descriptors when adding a new release.
    public IReadOnlyList<AiPackage> Packages { get; }
    public AiCatalog() : this([
        new("span", "20240831-055257", "https://github.com/TNTwise/SPAN-ncnn-vulkan/releases/download/20240831-055257/span-ncnn-vulkan-20240831-055257-windows.zip",
            16553410, "ce72105410046e78fccd5a04498427538b0a20d8d30a1bc0f9f476bb9c8bfb6f",
            "span-ncnn-vulkan-20240831-055257-windows/", "span-ncnn-vulkan.exe", "custom_models",
            "https://github.com/TNTwise/SPAN-ncnn-vulkan/tree/20240831-055257", "AGPL-3.0; Nomos8k: CC-BY-4.0 (Helaman / Philip Hofmann)",
            Models.Where(m => m.Family == "span" && m.Id != "spankendata").ToArray()),
        new("span", "20240831-055257+spankendata", "https://github.com/TNTwise/SPAN-ncnn-vulkan/releases/download/20240831-055257/span-ncnn-vulkan-20240831-055257-windows.zip",
            16553410, "ce72105410046e78fccd5a04498427538b0a20d8d30a1bc0f9f476bb9c8bfb6f",
            "span-ncnn-vulkan-20240831-055257-windows/", "span-ncnn-vulkan.exe", "custom_models",
            "https://github.com/TNTwise/SPAN-ncnn-vulkan/tree/20240831-055257",
            "AGPL-3.0; Nomos8k: CC-BY-4.0 (Helaman / Philip Hofmann); SPANkendata: CC-BY-SA-4.0 (Crustaceous D)",
            Models.Where(m => m.Family == "span").ToArray()),
        new("realesrgan", "20220424", "https://github.com/xinntao/Real-ESRGAN/releases/download/v0.2.5.0/realesrgan-ncnn-vulkan-20220424-windows.zip",
            45474481, "abc02804e17982a3be33675e4d471e91ea374e65b70167abc09e31acb412802d",
            "", "realesrgan-ncnn-vulkan.exe", "models", "https://github.com/xinntao/Real-ESRGAN/releases/tag/v0.2.5.0",
            "BSD-3-Clause (Real-ESRGAN); MIT (NCNN executable)",
            Models.Where(m => m.Family == "realesrgan").ToArray()),
        new("upscayl", "20251207-174704+models-20250802",
            "https://github.com/upscayl/upscayl-ncnn/releases/download/20251207-174704/upscayl-bin-20251207-174704-windows.zip",
            2421760, "1f0f65c5d2ade866555e2ac467d35952c35a47080a4060fe56a1ab028f67258a",
            "upscayl-bin-20251207-174704-windows/", "upscayl-bin.exe", "models",
            "https://github.com/upscayl/upscayl-ncnn/releases/tag/20251207-174704",
            "AGPL-3.0 (upscayl-ncnn); BSD-3-Clause (Real-ESRGAN); CC BY-NC 4.0 (OpenProteus)",
            Models.Where(m => m.Family == "upscayl").ToArray(), RequiredFiles:
            [
                "LICENSE", "README.md", "upscayl-bin.exe",
                "models/realesr-general-x4v3.bin", "models/realesr-general-x4v3.param",
                "models/2x_OpenProteus_Compact_i2_70K.bin", "models/2x_OpenProteus_Compact_i2_70K.param"
            ], SupplementalArtifacts:
            [
                new("https://github.com/TNTwise/real-video-enhancer-models/releases/download/models/realesr-general-x4v3.tar.gz",
                    2282303, "0376fe8aba8a2853753ba627bdf8d6ec1d2cf7a7c2efa88e831452e43c8e0243", AiArchiveKind.TarGzip,
                    "realesr-general-x4v3/", "models", "https://github.com/xinntao/Real-ESRGAN/releases/tag/v0.3.0", "BSD-3-Clause"),
                new("https://github.com/TNTwise/real-video-enhancer-models/releases/download/models/2x_OpenProteus_Compact_i2_70K.tar.gz",
                    1123490, "0d96689273650613726ebae4482cdc56943f46e819f99109054d0ca325d6a7c7", AiArchiveKind.TarGzip,
                    "2x_OpenProteus_Compact_i2_70K/", "models", "https://github.com/Sirosky/Upscale-Hub/releases/tag/OpenProteus", "CC BY-NC 4.0")
            ], PassNativeScale: true),
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
        if (!model.AllowedScales.Contains(request.Scale))
            throw new MediaException($"Модель {model.Name} поддерживает масштаб {string.Join(" / ", model.AllowedScales.Select(scale => $"×{scale}"))}.");
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
