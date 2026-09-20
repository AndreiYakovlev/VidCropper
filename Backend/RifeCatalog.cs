namespace VidCropper.Backend;

public sealed record InterpolationRequest(string ModelId, int Multiplier);

public static class RifeCatalog
{
    // Derived from the pinned, unmodified release archive. Legacy files remain installed.
    private static readonly string[] Versions =
    [
        "rife-v4.26", "rife-v4.26-large", "rife-v4.25", "rife-v4.25-heavy",
        "rife-v4.25-lite", "rife-v4.24", "rife-v4.23", "rife-v4.22",
        "rife-v4.22-lite", "rife-v4.21", "rife-v4.20", "rife-v4.19",
        "rife-v4.18", "rife-v4.17", "rife-v4.17-lite", "rife-v4.16-lite",
        "rife-v4.15", "rife-v4.15-lite", "rife-v4.14", "rife-v4.14-lite",
        "rife-v4.13", "rife-v4.13-lite", "rife-v4.12", "rife-v4.12-lite",
        "rife-v4.11", "rife-v4.10", "rife-v4.9", "rife-v4.8",
        "rife-v4.7", "rife-v4.6", "rife-v4.5", "rife-v4.4",
        "rife-v4.3", "rife-v4.2", "rife-v4.1", "rife-v4",
        "rife-v3.9"
    ];
    public static readonly AiModel[] Models = Versions.Select(id => new AiModel(id,
        "RIFE " + id[6..], "Интерполяция 2× / 3×. Vulkan; длительность и скорость звука сохраняются.",
        "rife", id, 1)).ToArray();

    public static AiModel Validate(InterpolationRequest request)
    {
        if (request.Multiplier is not (2 or 3)) throw new MediaException("Интерполяция поддерживает только 2× и 3×.");
        return Models.FirstOrDefault(m => m.Id == request.ModelId)
            ?? throw new MediaException("Неизвестная или несовместимая модель интерполяции.");
    }

    public static readonly AiPackage Package = new("rife", "20250112",
        "https://github.com/TNTwise/rife-ncnn-vulkan/releases/download/20250112/windows.zip",
        826923873, "42ed35e115b026f222386648920218cb8a9c7ae1e23698a7363bdd2e1455aba3",
        "rife-ncnn-vulkan-refs/heads/master-windows/", "rife-ncnn-vulkan.exe", ".",
        "https://github.com/TNTwise/rife-ncnn-vulkan/releases/tag/20250112", "MIT", Models,
        RequiredFiles: ArchiveFiles(), ExtractAll: true, CheckOnInstall: false);

    private static string[] ArchiveFiles()
    {
        string[] legacy = ["rife", "rife-HD", "rife-UHD", "rife-anime", "rife-v2", "rife-v2.3", "rife-v2.4", "rife-v3.0", "rife-v3.1"];
        return new[] { "LICENSE", "README.md", "rife-ncnn-vulkan.exe", "vcomp140.dll" }
            .Concat(legacy.SelectMany(model => new[] { "contextnet", "flownet", "fusionnet" }
                .SelectMany(net => new[] { $"{model}/{net}.bin", $"{model}/{net}.param" })))
            .Concat(Versions.Append("rife-v3.6").SelectMany(model => new[] { $"{model}/flownet.bin", $"{model}/flownet.param" })).ToArray();
    }
}
