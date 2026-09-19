using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace VidCropper.Backend;

public sealed class DownloadTools(IWebHostEnvironment environment)
{
    // ContentRoot is the project directory for dotnet run and the app directory for portable builds.
    public string DirectoryPath { get; } = Path.Combine(environment.ContentRootPath, "tools");
    public string YtDlpPath => Path.Combine(DirectoryPath, "yt-dlp.exe");
    public string DenoPath => Path.Combine(DirectoryPath, "deno.exe");
    public object Status() => new { ytDlpInstalled = File.Exists(YtDlpPath), denoInstalled = File.Exists(DenoPath), toolsDirectory = DirectoryPath, qualitySelectionSupported = true, sourceInspectionSupported = true };
    public bool Ready => File.Exists(YtDlpPath) && File.Exists(DenoPath);

    public async Task InstallAsync(Action<string, double?> report, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows() || System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.X64)
            throw new MediaException("Автоматическая установка поддерживает Windows x64.", 503);
        Directory.CreateDirectory(DirectoryPath);
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("VidCropper/1.0");
        if (!File.Exists(YtDlpPath))
            await InstallAssetAsync(client, "yt-dlp/yt-dlp", "yt-dlp.exe", YtDlpPath, false, report, ct);
        if (!File.Exists(DenoPath))
            await InstallAssetAsync(client, "denoland/deno", "deno-x86_64-pc-windows-msvc.zip", DenoPath, true, report, ct);
    }

    private async Task InstallAssetAsync(HttpClient client, string repository, string assetName, string destination,
        bool zipped, Action<string, double?> report, CancellationToken ct)
    {
        var tool = Path.GetFileNameWithoutExtension(destination);
        report($"Поиск актуальной версии {tool}…", null);
        using var release = await client.GetAsync($"https://api.github.com/repos/{repository}/releases/latest", ct);
        release.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await release.Content.ReadAsStringAsync(ct));
        var asset = json.RootElement.GetProperty("assets").EnumerateArray().FirstOrDefault(a => a.GetProperty("name").GetString() == assetName);
        if (asset.ValueKind == JsonValueKind.Undefined || !asset.TryGetProperty("digest", out var digestProperty))
            throw new MediaException($"GitHub не предоставил файл или контрольную сумму {tool}. Попробуйте ручную установку.", 502);
        var digest = digestProperty.GetString();
        if (digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71)
            throw new MediaException($"Не удалось проверить контрольную сумму {tool}.", 502);
        var url = asset.GetProperty("browser_download_url").GetString()!;
        if (!url.StartsWith($"https://github.com/{repository}/releases/download/", StringComparison.Ordinal))
            throw new MediaException("GitHub вернул неожиданный адрес загрузки.", 502);
        var scratch = Path.Combine(DirectoryPath, $".download-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        try
        {
            var archive = Path.Combine(scratch, assetName);
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var length = response.Content.Headers.ContentLength;
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = File.Create(archive))
            {
                var buffer = new byte[65536];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > 512L * 1024 * 1024) throw new MediaException("Слишком большой архив инструмента.", 502);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    report($"Скачивание {tool}…", length > 0 ? Math.Clamp(total * 100d / length.Value, 0, 100) : null);
                }
            }
            report($"Проверка {tool}…", null);
            await using (var stream = File.OpenRead(archive))
            {
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
                if (!actual.Equals(digest[7..], StringComparison.OrdinalIgnoreCase))
                    throw new MediaException($"Контрольная сумма {tool} не совпала. Файл не установлен.", 502);
            }
            var executable = archive;
            if (zipped)
            {
                using var zip = ZipFile.OpenRead(archive);
                var entry = zip.GetEntry("deno.exe") ?? throw new MediaException("В архиве нет deno.exe.", 502);
                if (entry.Length > 512L * 1024 * 1024) throw new MediaException("Слишком большой файл deno.exe.", 502);
                executable = Path.Combine(scratch, "deno.exe");
                await using var input = entry.Open();
                await using var output = File.Create(executable);
                await input.CopyToAsync(output, ct);
            }
            ct.ThrowIfCancellationRequested();
            // Never replace a manually installed tool; only publish a complete verified executable.
            if (!File.Exists(destination)) File.Move(executable, destination);
        }
        finally
        {
            // scratch was created by this operation inside tools; never remove the tools directory.
            try { Directory.Delete(scratch, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
