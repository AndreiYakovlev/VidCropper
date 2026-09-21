using System.IO.Compression;
using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json;

namespace VidCropper.Backend;

public sealed record AiInstallation(AiPackage Package, string Directory, AiModel Model)
{
    public string Executable => Path.Combine(Directory, Package.Executable);
    public string ModelsPath => Path.Combine(Directory, Package.ModelsDirectory);
}
public sealed record AiPackageState(string? Active = null, string? Previous = null);
public sealed record AiInstalledFile(string Path, long Bytes, string Sha256);
public sealed record AiReceipt(string ArchiveSha256, AiInstalledFile[] Files, string[]? ArtifactSha256s = null);
public sealed record AiOperation(Guid Id, string Status, string Stage, double? Progress, string? Error);
public sealed record AiPackageStatus(string Family, string Version, string? ActiveVersion, string? PreviousVersion,
    bool Installed, bool UpdateAvailable, bool CanRollback, long DownloadBytes, string License, string SourceUrl,
    string[] ActiveModels, string[] PreviousModels);

public sealed class AiPackages(AiCatalog catalog, IWebHostEnvironment environment, ProcessingGate gate,
    AiRunner runner, IHostApplicationLifetime lifetime, ILogger<AiPackages> logger, RifeRunner? rifeRunner = null) : IHostedService
{
    private readonly object sync = new();
    private readonly Dictionary<Guid, Operation> operations = [];
    public string Root { get; } = Path.Combine(environment.ContentRootPath, "tools", "ai");
    private bool stopping;
    private sealed class Operation(Guid id)
    {
        public AiOperation Snapshot = new(id, "queued", "Подготовка…", null, null);
        public CancellationTokenSource Cancellation = new();
        public Task Task = Task.CompletedTask;
    }
    private string Folder(AiPackage p) => Path.Combine(Root, p.Family, p.Version);
    private string StatePath(string family) => Path.Combine(Root, family, "active.json");
    private AiPackageState ReadState(string family)
    {
        var path = StatePath(family);
        if (!File.Exists(path)) return new();
        try { return JsonSerializer.Deserialize<AiPackageState>(File.ReadAllText(path)) ?? new(); }
        catch (JsonException) { return new(); }
    }
    private void WriteState(string family, AiPackageState state)
    {
        var path = StatePath(family);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(state)); File.Move(temp, path, true); }
        finally { File.Delete(temp); }
    }
    private AiPackage? Known(string family, string? version) => catalog.Packages.FirstOrDefault(p => p.Family == family && p.Version == version);
    private static IEnumerable<string> ArtifactHashes(AiPackage package) =>
        new[] { package.Sha256 }.Concat(package.SupplementalArtifacts?.Select(artifact => artifact.Sha256) ?? []);
    private static long DownloadBytes(AiPackage package) => checked(package.Bytes +
        (package.SupplementalArtifacts?.Sum(artifact => artifact.Bytes) ?? 0));
    private static bool ReceiptMatches(AiPackage package, AiReceipt receipt)
    {
        if (!receipt.ArchiveSha256.Equals(package.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
        var expected = ArtifactHashes(package).ToArray();
        if (expected.Length == 1) return receipt.ArtifactSha256s is null ||
            receipt.ArtifactSha256s.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase);
        return receipt.ArtifactSha256s?.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase) == true;
    }

    private bool Present(AiPackage? package)
    {
        if (package is null) return false;
        try
        {
            var receipt = JsonSerializer.Deserialize<AiReceipt>(File.ReadAllText(Path.Combine(Folder(package), "receipt.json")));
            return receipt is not null && ReceiptMatches(package, receipt) &&
                Required(package).All(name => receipt.Files.Any(f => f.Path == name)) &&
                receipt.Files.All(f => new FileInfo(SafePath(Folder(package), f.Path)) is { Exists: true } info && info.Length == f.Bytes);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or MediaException) { return false; }
    }

    public object Status() => new
    {
        models = AiCatalog.Models,
        interpolationModels = RifeCatalog.Models,
        busy = gate.Busy,
        supported = OperatingSystem.IsWindows() && System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.X64,
        packages = catalog.Packages.Select(p => p.Family).Distinct().Select(family =>
        {
            var latest = catalog.Latest(family);
            var state = ReadState(family);
            var active = Known(family, state.Active);
            var previous = Known(family, state.Previous);
            return new AiPackageStatus(family, latest.Version, state.Active, state.Previous,
                Present(active), active is not null && active.Version != latest.Version,
                Present(previous) && state.Previous != state.Active, DownloadBytes(latest), latest.License, latest.SourceUrl,
                active?.Models.Select(model => model.Id).ToArray() ?? [], previous?.Models.Select(model => model.Id).ToArray() ?? []);
        }).ToArray()
    };

    public AiInstallation Resolve(UpscaleRequest request)
    {
        return ResolveModel(AiCatalog.Validate(request));
    }

    public AiInstallation ResolveInterpolation(InterpolationRequest request) => ResolveModel(RifeCatalog.Validate(request));

    private AiInstallation ResolveModel(AiModel model)
    {
        var state = ReadState(model.Family);
        var package = Known(model.Family, state.Active);
        if (!Present(package)) throw new MediaException("Установите или восстановите компоненты выбранной AI-модели.", 409);
        var versionModel = package!.Models.SingleOrDefault(m => m.Id == model.Id)
            ?? throw new MediaException("Активная версия пакета не поддерживает эту модель. Обновите AI-модуль.", 409);
        return new(package, Folder(package), versionModel);
    }

    public async Task VerifyAsync(AiInstallation installation, CancellationToken ct)
    {
        var receipt = JsonSerializer.Deserialize<AiReceipt>(await File.ReadAllTextAsync(Path.Combine(installation.Directory, "receipt.json"), ct))
            ?? throw new MediaException("Повреждена установка AI. Восстановите пакет.");
        foreach (var file in receipt.Files)
        {
            await using var stream = File.OpenRead(SafePath(installation.Directory, file.Path));
            if (!Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new MediaException("Файлы AI-модуля повреждены. Восстановите пакет.", 409);
        }
    }

    public AiOperation Start(string modelId, string action)
    {
        var model = AiCatalog.AnyModel(modelId);
        if (action is not ("install" or "rollback" or "check")) throw new MediaException("Неизвестная операция AI.");
        if (!OperatingSystem.IsWindows() || System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.X64)
            throw new MediaException("AI-модуль поддерживает Windows x64.", 503);
        lock (sync)
        {
            if (stopping) throw new MediaException("Приложение завершает работу.", 503);
            var owner = gate.Acquire();
            var op = new Operation(Guid.NewGuid());
            operations.Add(op.Snapshot.Id, op);
            op.Task = Task.Run(async () =>
            {
                using (owner)
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(op.Cancellation.Token, lifetime.ApplicationStopping))
                {
                    linked.CancelAfter(TimeSpan.FromMinutes(30));
                    void Report(string stage, double? progress) { lock (sync) op.Snapshot = op.Snapshot with { Status = "running", Stage = stage, Progress = progress }; }
                    try
                    {
                        await ChangeAsync(model, action, Report, linked.Token);
                        lock (sync) op.Snapshot = op.Snapshot with { Status = "completed", Stage = "AI-модуль готов", Progress = 100 };
                    }
                    catch (OperationCanceledException)
                    {
                        lock (sync) op.Snapshot = op.Snapshot with { Status = "cancelled", Stage = "Операция отменена или превышено время ожидания" };
                    }
                    catch (Exception e)
                    {
                        logger.LogError(e, "Ошибка установки/проверки AI");
                        lock (sync) op.Snapshot = op.Snapshot with { Status = "failed", Error = e is MediaException ? e.Message : "Не удалось подготовить AI-модуль. Проверьте интернет, свободное место и журнал сервера." };
                    }
                }
            });
            return op.Snapshot;
        }
    }

    private async Task ChangeAsync(AiModel model, string action, Action<string, double?> report, CancellationToken ct)
    {
        var state = ReadState(model.Family);
        if (action == "check")
        {
            var installed = ResolveModel(model);
            await VerifyAsync(installed, ct);
            report("Проверка модели на GPU…", null);
            await CheckModelAsync(installed, ct);
            return;
        }
        var target = action == "rollback"
            ? Known(model.Family, state.Previous) ?? throw new MediaException("Предыдущая версия отсутствует.", 409)
            : catalog.Latest(model.Family);
        var targetModel = target.Models.SingleOrDefault(m => m.Id == model.Id)
            ?? throw new MediaException(action == "rollback"
                ? "Предыдущая версия пакета не поддерживает выбранную модель. Выберите модель из предыдущего пакета."
                : "Последняя версия AI-пакета не поддерживает выбранную модель.", 409);
        if (Present(target))
        {
            var installed = new AiInstallation(target, Folder(target), targetModel);
            try
            {
                await VerifyAsync(installed, ct);
                if (target.CheckOnInstall)
                {
                    report("Проверка модели на GPU…", null);
                    await CheckModelAsync(installed, ct);
                }
                ct.ThrowIfCancellationRequested();
                if (state.Active != target.Version) WriteState(model.Family, new(target.Version, state.Active));
                return;
            }
            catch (MediaException) when (action == "install") { /* explicit install also repairs damaged files */ }
        }
        if (action == "rollback") throw new MediaException("Предыдущий пакет повреждён; откат невозможен.", 409);

        Directory.CreateDirectory(Root);
        var scratch = Path.Combine(Root, ".install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var archive = Path.Combine(scratch, "package.zip");
            var supplemental = target.SupplementalArtifacts ?? [];
            var supplementalArchives = new string[supplemental.Length];
            var totalBytes = DownloadBytes(target);
            long downloadedBytes = 0;
            using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("VidCropper/1.0");
            await DownloadAsync(client, target.Url, archive, target.Bytes, target.Sha256, downloadedBytes, totalBytes, report, ct);
            downloadedBytes += target.Bytes;
            for (var index = 0; index < supplemental.Length; index++)
            {
                var artifact = supplemental[index];
                var path = Path.Combine(scratch, $"artifact-{index}.tar.gz");
                supplementalArchives[index] = path;
                await DownloadAsync(client, artifact.Url, path, artifact.Bytes, artifact.Sha256,
                    downloadedBytes, totalBytes, report, ct);
                downloadedBytes += artifact.Bytes;
            }
            report("Распаковка и проверка…", null);
            var staged = Path.Combine(scratch, "ready"); Directory.CreateDirectory(staged);
            await ExtractAsync(target, archive, supplementalArchives, staged, ct);
            if (target.Family == "realesrgan")
                foreach (var name in new[] { "Real-ESRGAN.txt", "Real-ESRGAN-ncnn-vulkan.txt" })
                    File.Copy(Path.Combine(environment.ContentRootPath, "licenses", "ai", name), Path.Combine(staged, name));
            if (target.CheckOnInstall)
            {
                report("Проверка модели на GPU…", null);
                await CheckModelAsync(new(target, staged, targetModel), ct);
            }
            ct.ThrowIfCancellationRequested();
            var destination = Folder(target);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var backup = destination + ".repair-" + Guid.NewGuid().ToString("N");
            if (Directory.Exists(destination)) Directory.Move(destination, backup);
            try
            {
                Directory.Move(staged, destination);
                WriteState(model.Family, new(target.Version, state.Active == target.Version ? state.Previous : state.Active));
            }
            catch
            {
                if (Directory.Exists(destination)) Directory.Delete(destination, true);
                if (Directory.Exists(backup)) Directory.Move(backup, destination);
                throw;
            }
            // Keep repair backup as well; never delete a previously installed package automatically.
        }
        finally { TryClean(scratch); }
    }

    private static async Task DownloadAsync(HttpClient client, string url, string path, long expectedBytes, string expectedSha256,
        long completedBytes, long totalBytes, Action<string, double?> report, CancellationToken ct)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[65536]; long downloaded = 0; int count;
        await using (var output = File.Create(path))
        {
            while ((count = await input.ReadAsync(buffer, ct)) > 0)
            {
                downloaded += count;
                if (downloaded > expectedBytes) throw new MediaException("Размер AI-пакета не совпадает с каталогом.");
                await output.WriteAsync(buffer.AsMemory(0, count), ct);
                report("Скачивание AI-пакета…", (completedBytes + downloaded) * 100d / totalBytes);
            }
            await output.FlushAsync(ct);
        }
        if (downloaded != expectedBytes) throw new MediaException("AI-пакет скачан не полностью.");
        await using var stream = File.OpenRead(path);
        if (!Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new MediaException("Контрольная сумма AI-пакета не совпала. Установка отменена.");
    }

    private static HashSet<string> Required(AiPackage p)
    {
        if (p.RequiredFiles is not null) return new(p.RequiredFiles, StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal) { p.Executable, "vcomp140.dll" };
        if (p.Family == "span") names.Add("LICENSE");
        foreach (var model in p.Models)
            foreach (var scale in model.VariableScale ? new[] { 2, 3, 4 } : new[] { model.NativeScale })
                foreach (var extension in new[] { "bin", "param" })
                    names.Add($"{p.ModelsDirectory}/{model.FileName}{(model.VariableScale ? $"-x{scale}" : "")}.{extension}");
        return names;
    }
    public static string SafePath(string root, string name)
    {
        var full = Path.GetFullPath(Path.Combine(root, name));
        if (Path.IsPathRooted(name) || name.Contains(':') || !full.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new MediaException("Недопустимый путь внутри AI-пакета.");
        return full;
    }
    private static async Task ExtractAsync(AiPackage p, string archive, string[] supplementalArchives,
        string destination, CancellationToken ct)
    {
        var required = Required(p);
        var extracted = new List<AiInstalledFile>();
        await ExtractZipAsync(p, archive, destination, required, extracted, ct);
        var artifacts = p.SupplementalArtifacts ?? [];
        if (artifacts.Length != supplementalArchives.Length) throw new InvalidOperationException("AI artifact list mismatch.");
        for (var index = 0; index < artifacts.Length; index++)
        {
            if (artifacts[index].Kind != AiArchiveKind.TarGzip) throw new MediaException("Неизвестный формат AI-пакета.");
            await ExtractTarGzipAsync(artifacts[index], supplementalArchives[index], destination, required, extracted, ct);
        }
        if (!required.IsSubsetOf(extracted.Select(f => f.Path))) throw new MediaException("В AI-пакете отсутствуют необходимые файлы.");
        var sources = new List<string> { p.SourceUrl, p.License };
        foreach (var artifact in artifacts)
        {
            sources.Add(artifact.SourceUrl);
            sources.Add(artifact.License);
        }
        if (p.Family == "span")
            sources.AddRange(["Nomos8k author: Helaman / Philip Hofmann", "https://openmodeldb.info/models/4x-Nomos8k-span-otf-medium",
                "SPANkendata author: Crustaceous D / terrainer", "https://openmodeldb.info/models/4x-SPANkendata"]);
        await File.WriteAllTextAsync(Path.Combine(destination, "SOURCES.txt"), string.Join('\n', sources) + "\n", ct);
        await File.WriteAllTextAsync(Path.Combine(destination, "receipt.json"),
            JsonSerializer.Serialize(new AiReceipt(p.Sha256, extracted.ToArray(), ArtifactHashes(p).ToArray())), ct);
    }

    private static async Task ExtractZipAsync(AiPackage p, string archive, string destination, HashSet<string> required,
        List<AiInstalledFile> extracted, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(archive);
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            _ = SafePath(destination, entry.FullName);
            if (!entry.FullName.StartsWith(p.ArchiveRoot, StringComparison.Ordinal)) continue;
            var relative = entry.FullName[p.ArchiveRoot.Length..];
            if (relative.Length == 0 || entry.FullName.EndsWith('/')) continue;
            if (!p.ExtractAll && !required.Contains(relative)) continue;
            await using var input = entry.Open();
            await ExtractFileAsync(input, entry.Length, relative, destination, extracted, ct);
        }
    }

    private static async Task ExtractTarGzipAsync(AiPackageArtifact artifact, string archive, string destination,
        HashSet<string> required, List<AiInstalledFile> extracted, CancellationToken ct)
    {
        await using var stream = File.OpenRead(archive);
        await using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        while (tar.GetNextEntry() is { } entry)
        {
            ct.ThrowIfCancellationRequested();
            var entryName = NormalizePath(entry.Name);
            _ = SafePath(destination, entryName);
            if (!entryName.StartsWith(artifact.ArchiveRoot, StringComparison.Ordinal)) continue;
            var relative = entryName[artifact.ArchiveRoot.Length..];
            if (relative.Length == 0 || entry.EntryType == TarEntryType.Directory) continue;
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream is null)
                throw new MediaException("Некорректный архив AI.");
            var installedPath = NormalizePath(Path.Combine(artifact.DestinationDirectory, relative));
            if (!required.Contains(installedPath)) continue;
            await ExtractFileAsync(entry.DataStream, entry.Length, installedPath, destination, extracted, ct);
        }
    }

    private static async Task ExtractFileAsync(Stream input, long length, string relative, string destination,
        List<AiInstalledFile> extracted, CancellationToken ct)
    {
        if (length > 128L * 1024 * 1024 || extracted.Any(f => f.Path.Equals(relative, StringComparison.OrdinalIgnoreCase)))
            throw new MediaException("Некорректный архив AI.");
        var path = SafePath(destination, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using (var output = File.Create(path)) await input.CopyToAsync(output, ct);
        var extractedPath = SafePath(destination, relative);
        await using var file = File.OpenRead(extractedPath);
        if (file.Length != length) throw new MediaException("Некорректный архив AI.");
        extracted.Add(new(relative, file.Length, Convert.ToHexString(await SHA256.HashDataAsync(file, ct))));
    }
    private static string NormalizePath(string path) => path.Replace('\\', '/');
    private Task CheckModelAsync(AiInstallation installation, CancellationToken ct) => installation.Package.Family == "rife"
        ? (rifeRunner ?? throw new InvalidOperationException("RIFE runner is not registered.")).CheckAsync(installation, ct)
        : runner.CheckAsync(installation, ct);
    private void TryClean(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { logger.LogWarning(e, "Не удалось очистить AI temp {Path}", path); }
    }
    public AiOperation Get(Guid id) { lock (sync) return Find(id).Snapshot; }
    private Operation Find(Guid id) => operations.TryGetValue(id, out var op) ? op : throw new MediaException("Операция AI не найдена.", 404);
    public async Task<AiOperation> CancelAsync(Guid id)
    {
        Operation op; lock (sync) { op = Find(id); op.Cancellation.Cancel(); }
        await op.Task; return Get(id);
    }
    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public async Task StopAsync(CancellationToken ct)
    {
        Task[] tasks;
        lock (sync) { stopping = true; foreach (var op in operations.Values) op.Cancellation.Cancel(); tasks = operations.Values.Select(op => op.Task).ToArray(); }
        await Task.WhenAll(tasks);
        foreach (var op in operations.Values) op.Cancellation.Dispose();
    }
}
