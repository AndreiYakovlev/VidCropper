using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace VidCropper.Backend;

public sealed record LinkRequest(string Url, int? Height = null, Guid? InspectionId = null);
public sealed record LinkSourceInfo(Guid Id, string Kind, int[] Heights, string Title);

public static class DownloadQuality
{
    public static string Format(int? height)
    {
        if (height is < 1 or > 32768)
            throw new MediaException("Недопустимое качество видео. Выберите качество из списка.");
        // Every alternative retains the resolution constraint; never silently fall back to a smaller video.
        // Direct files use the original; fixed choices are only offered for formats with known dimensions.
        var filter = height is null ? "" : $"[height={height.Value.ToString(CultureInfo.InvariantCulture)}]";
        return $"bv{filter}+ba/b{filter}/bv{filter}";
    }
}
public sealed record LinkProgress(Guid Id, string Message, double? Progress);
public sealed record DownloadedMedia(Guid Id, string Name, VideoInfo Info);

public sealed class LinkDownloadService(DownloadTools downloader, MediaTools tools, MediaStore store,
    MediaOptions options, IHostApplicationLifetime lifetime, ILogger<LinkDownloadService> logger,
    IWebHostEnvironment environment) : IHostedService
{
    private readonly object gate = new();
    private Operation? active;
    private bool stopping;
    private readonly Dictionary<Guid, Inspection> inspections = [];
    private sealed record Inspection(string Url, LinkSourceInfo Info, DateTimeOffset Expires);
    private sealed class Operation(Guid id)
    {
        public Guid Id { get; } = id;
        public LinkProgress Progress { get; set; } = new(id, "Подготовка…", null);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public LinkProgress Get(Guid id)
    {
        lock (gate) return active?.Id == id ? active.Progress : throw new MediaException("Операция уже завершена или не найдена.", 404);
    }

    private async Task<T> RunAsync<T>(Guid id, Func<Action<string, double?>, CancellationToken, Task<T>> work, CancellationToken aborted)
    {
        Operation operation;
        lock (gate)
        {
            if (stopping) throw new MediaException("Приложение завершается.", 503);
            if (active is not null) throw new MediaException("Другая загрузка ещё выполняется. Повторите через несколько секунд.", 409);
            active = operation = new(id);
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(aborted, lifetime.ApplicationStopping);
        linked.CancelAfter(TimeSpan.FromHours(2));
        void Report(string message, double? progress) { lock (gate) operation.Progress = new(id, message, progress); }
        try { return await work(Report, linked.Token); }
        catch (OperationCanceledException) when (!aborted.IsCancellationRequested && !lifetime.ApplicationStopping.IsCancellationRequested)
        { throw new MediaException("Превышено время ожидания загрузки. Попробуйте ещё раз.", 408); }
        catch (HttpRequestException)
        { throw new MediaException("Не удалось скачать инструменты с GitHub. Проверьте интернет или установите их вручную.", 502); }
        catch (UnauthorizedAccessException)
        { throw new MediaException("Нет доступа для записи. Проверьте права на папку приложения и временную папку.", 503); }
        catch (IOException)
        { throw new MediaException("Ошибка записи файла. Проверьте свободное место и доступ к папке приложения.", 503); }
        finally { lock (gate) { active = null; operation.Finished.TrySetResult(); } }
    }

    public Task<bool> InstallAsync(Guid id, CancellationToken ct) => RunAsync(id, async (report, token) =>
    {
        await downloader.InstallAsync(report, token);
        return true;
    }, ct);

    private static string ValidateUrl(string value)
    {
        if (value is null || value.Length > 8192 || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var url) ||
            url.Scheme is not ("https" or "http") || string.IsNullOrEmpty(url.Host) || url.UserInfo.Length > 0)
            throw new MediaException("Введите HTTP/HTTPS-ссылку на видео без логина и пароля в адресе.");
        return url.AbsoluteUri;
    }

    public Task<LinkSourceInfo> InspectAsync(Guid id, LinkRequest request, CancellationToken ct)
    {
        var url = ValidateUrl(request.Url);
        if (!downloader.Ready) throw new MediaException("Установите yt-dlp и Deno в папку tools.", 409);
        return RunAsync(id, async (report, token) =>
        {
            report("Определение источника и доступного качества…", null);
            var info = await InspectCoreAsync(id, url, token);
            lock (gate)
            {
                foreach (var key in inspections.Where(pair => pair.Value.Expires <= DateTimeOffset.UtcNow).Select(pair => pair.Key).ToArray()) inspections.Remove(key);
                if (inspections.Count >= 20) inspections.Remove(inspections.MinBy(pair => pair.Value.Expires).Key);
                inspections[id] = new(url, info, DateTimeOffset.UtcNow.AddMinutes(10));
            }
            return info;
        }, ct);
    }

    private async Task<LinkSourceInfo> InspectCoreAsync(Guid id, string url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        string? metadata = null;
        await RunYtDlpAsync(["--ignore-config", "--no-plugin-dirs", "--no-cache-dir", "--no-playlist", "--playlist-end", "1", "--flat-playlist",
            "--no-js-runtimes", "--js-runtimes", $"deno:{downloader.DenoPath}", "--no-remote-components",
            "--socket-timeout", "30", "--retries", "2", "--skip-download", "--dump-single-json", "--", url], line =>
        {
            if (line.StartsWith('{') && line.Length <= 16 * 1024 * 1024) metadata = line;
        }, timeout.Token, null);
        if (metadata is null) throw new MediaException("Не удалось определить источник видео.", 422);
        using var json = JsonDocument.Parse(metadata);
        return DescribeSource(id, json.RootElement);
    }

    public static LinkSourceInfo DescribeSource(Guid id, JsonElement root)
    {
        static string Text(JsonElement item, string name) => item.TryGetProperty(name, out var value) ? value.ToString() : "";
        if (Text(root, "_type") is "playlist" or "multi_video")
            throw new MediaException("Укажите ссылку на отдельное видео, а не плейлист.", 422);
        if (Text(root, "is_live").Equals("True", StringComparison.OrdinalIgnoreCase) || Text(root, "live_status") is "is_live" or "is_upcoming")
            throw new MediaException("Прямые трансляции не поддерживаются.", 422);
        var formats = root.TryGetProperty("formats", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().ToArray() : [root];
        var videos = formats.Where(f => Text(f, "vcodec") != "none" && Text(f, "url").Length > 0).ToArray();
        if (videos.Length == 0) throw new MediaException("Источник не содержит доступных видеоформатов.", 422);
        var stream = videos.Any(f => Text(f, "protocol").Contains("m3u8", StringComparison.OrdinalIgnoreCase) ||
            Text(f, "protocol").Contains("dash", StringComparison.OrdinalIgnoreCase) || f.TryGetProperty("fragments", out _));
        var singleFile = Text(root, "extractor_key").Equals("Generic", StringComparison.OrdinalIgnoreCase) && videos.Length == 1 && !stream;
        var heights = singleFile ? [] : videos.Select(f => f.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number && h.TryGetInt32(out var n) ? n : 0)
            .Where(h => h > 0 && h <= 32768).Distinct().OrderDescending().ToArray();
        return new(id, singleFile ? "file" : stream ? "stream" : "resource", heights, Text(root, "title"));
    }

    public Task<DownloadedMedia> DownloadAsync(Guid id, LinkRequest request, CancellationToken ct)
    {
        var url = ValidateUrl(request.Url);
        _ = DownloadQuality.Format(request.Height);
        if (!downloader.Ready) throw new MediaException("Установите yt-dlp и Deno в папку tools.", 409);
        return RunAsync(id, async (report, token) =>
        {
            LinkSourceInfo info;
            if (request.InspectionId is { } inspectionId)
            {
                lock (gate)
                {
                    if (!inspections.TryGetValue(inspectionId, out var cached) || cached.Url != url || cached.Expires <= DateTimeOffset.UtcNow)
                        throw new MediaException("Сведения об источнике устарели. Проверьте ссылку ещё раз.", 409);
                    info = cached.Info;
                }
            }
            else
            {
                report("Определение источника…", null);
                info = await InspectCoreAsync(Guid.NewGuid(), url, token);
            }
            var height = info.Kind == "file" ? null : request.Height;
            if (height is not null && !info.Heights.Contains(height.Value))
                throw new MediaException($"Источник не предлагает качество {height}p. Проверьте ссылку и выберите доступное качество.", 422);
            return await DownloadCoreAsync(url, height, DownloadQuality.Format(height), report, token);
        }, ct);
    }

    private async Task<DownloadedMedia> DownloadCoreAsync(string url, int? height, string format, Action<string, double?> report, CancellationToken ct)
    {
        var toolError = await tools.CheckAsync(ct);
        if (toolError is not null) throw new MediaException(toolError, 503);
        var mediaId = Guid.NewGuid();
        var scratch = Path.Combine(store.Root, $"link-{mediaId:N}");
        Directory.CreateDirectory(scratch);
        var destination = Path.Combine(store.Root, $"{mediaId:N}.mp4");
        try
        {
            report("Получение информации о видео…", null);
            var title = "video";
            var downloadTooLarge = false;
            var qualityLabel = height is null ? "лучшее доступное качество" : $"{height}p";
            List<string> args = ["--ignore-config", "--no-plugin-dirs", "--no-cache-dir", "--no-playlist", "--max-downloads", "1",
                "--no-colors", "--newline", "--progress", "--progress-delta", "0.3",
                "--no-js-runtimes", "--js-runtimes", $"deno:{downloader.DenoPath}", "--no-remote-components",
                "--socket-timeout", "30", "--retries", "3", "--fragment-retries", "3",
                "--max-filesize", options.MaxUploadBytes.ToString(CultureInfo.InvariantCulture),
                "--match-filters", "!is_live", "--ffmpeg-location", tools.FfmpegExecutable,
                "--format", format, "--format-sort-force", "--format-sort", "res,fps,vcodec:h264,acodec:aac",
                "--merge-output-format", "mkv", "--output", Path.Combine(scratch, "source.%(ext)s"),
                "--print", "before_dl:TITLE:%(title)j", "--no-simulate", "--no-quiet",
                "--progress-template", "download:PROGRESS:%(progress._percent_str)s",
                "--", url];
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var limitExceeded = false;
            async Task MonitorSizeAsync()
            {
                while (!budget.IsCancellationRequested)
                {
                    // Allow space for separate tracks plus the merged source, but bound unknown-size streams too.
                    long total = 0;
                    foreach (var path in Directory.EnumerateFiles(scratch))
                    {
                        try { total += new FileInfo(path).Length; }
                        catch (FileNotFoundException) { /* Merging removes input tracks. */ }
                    }
                    if (total > options.MaxUploadBytes * 2d)
                    { limitExceeded = true; await budget.CancelAsync(); break; }
                    await Task.Delay(300, budget.Token);
                }
            }
            var monitor = MonitorSizeAsync();
            try
            {
                await RunYtDlpAsync(args, line =>
                {
                    if (line.Contains("larger than max-filesize", StringComparison.OrdinalIgnoreCase)) downloadTooLarge = true;
                    if (line.StartsWith("TITLE:", StringComparison.Ordinal))
                    {
                        try { title = JsonSerializer.Deserialize<string>(line[6..]) ?? "video"; }
                        catch (JsonException) { }
                    }
                    if (line.StartsWith("PROGRESS:", StringComparison.Ordinal))
                    {
                        var text = line[9..].Trim().TrimEnd('%');
                        report($"Скачивание видео · {qualityLabel}…", double.TryParse(text, CultureInfo.InvariantCulture, out var p) && double.IsFinite(p) ? Math.Clamp(p, 0, 100) : null);
                    }
                }, budget.Token, height);
                if (downloadTooLarge) throw new MediaException("Видео превышает лимит размера.", 413);
                var files = Directory.GetFiles(scratch, "source.*").Where(p => !p.EndsWith(".part", StringComparison.Ordinal) && !p.EndsWith(".ytdl", StringComparison.Ordinal)).ToArray();
                if (files.Length != 1) throw new MediaException("Не удалось получить один видеофайл. Плейлисты и прямые трансляции не поддерживаются.", 422);
                if (new FileInfo(files[0]).Length > options.MaxUploadBytes) throw new MediaException("Видео превышает лимит размера.", 413);
                report("Подготовка MP4 для редактора…", null);
                await NormalizeAsync(files[0], Path.Combine(scratch, "ready.mp4"), report, budget.Token);
                if (new FileInfo(Path.Combine(scratch, "ready.mp4")).Length > options.MaxUploadBytes)
                    throw new MediaException("Подготовленное видео превышает лимит размера.", 413);
            }
            catch (OperationCanceledException) when (limitExceeded && !ct.IsCancellationRequested)
            { throw new MediaException("Видео превышает лимит размера. Загрузка остановлена.", 413); }
            finally
            {
                await budget.CancelAsync();
                try { await monitor; } catch (OperationCanceledException) { }
            }
            ct.ThrowIfCancellationRequested();
            File.Move(Path.Combine(scratch, "ready.mp4"), destination);
            var info = await tools.ProbeAsync(destination, ct);
            if (height is not null && info.Height != height)
                throw new MediaException($"Выбрано {height}p, но получен кадр {info.Width} × {info.Height}. Видео не открыто: выберите другое качество или «Лучшее доступное».", 422);
            ct.ThrowIfCancellationRequested();
            var safeName = string.Concat(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) || char.IsControl(c) ? '_' : c)).Trim().TrimEnd('.');
            if (string.IsNullOrWhiteSpace(safeName)) safeName = "video";
            if (safeName.Length > 150) safeName = safeName[..150];
            // Windows reserves these names even with an extension.
            if (System.Text.RegularExpressions.Regex.IsMatch(safeName.Split('.')[0].TrimEnd(),
                @"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                safeName = "_" + safeName;
            report("Сохранение в downloads…", null);
            var savedPath = await SaveDownloadAsync(destination, safeName, ct);
            var name = Path.GetFileName(savedPath);
            store.Add(mediaId, savedPath, name, info, deleteOnRelease: false);
            store.TryDelete(destination);
            return new(mediaId, name, info);
        }
        catch { store.TryDelete(destination); throw; }
        finally
        {
            try { Directory.Delete(scratch, true); }
            catch (IOException exception) { logger.LogWarning(exception, "Не удалось очистить загрузку {Id}", mediaId); }
            catch (UnauthorizedAccessException exception) { logger.LogWarning(exception, "Не удалось очистить загрузку {Id}", mediaId); }
        }
    }

    private async Task<string> SaveDownloadAsync(string source, string name, CancellationToken ct)
    {
        var folder = Directory.CreateDirectory(Path.Combine(environment.ContentRootPath, "downloads")).FullName;
        var staging = Path.Combine(folder, $".{Guid.NewGuid():N}.part");
        try
        {
            // Stage on the destination volume, then publish only a complete, validated MP4.
            await using (var input = File.OpenRead(source))
            await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                await input.CopyToAsync(output, ct);
            ct.ThrowIfCancellationRequested();
            for (var suffix = 0; ; suffix++)
            {
                var path = Path.Combine(folder, name + (suffix == 0 ? "" : $" ({suffix})") + ".mp4");
                try { File.Move(staging, path, overwrite: false); return path; }
                catch (IOException) when (File.Exists(path) || Directory.Exists(path)) { }
            }
        }
        finally { store.TryDelete(staging); }
    }

    private async Task NormalizeAsync(string source, string result, Action<string, double?> report, CancellationToken ct)
    {
        var info = await tools.ProbeAsync(source, ct);
        var details = await tools.RunAsync(true, ["-v", "error", "-protocol_whitelist", "file,pipe", "-show_entries", "stream=index,codec_type,codec_name,pix_fmt", "-of", "json", source], null, ct);
        using var json = JsonDocument.Parse(details);
        var streams = json.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.First(s => s.GetProperty("index").GetInt32() == info.StreamIndex);
        var copyVideo = info.Codec == "h264" && video.TryGetProperty("pix_fmt", out var pixelFormat) && pixelFormat.GetString() == "yuv420p";
        var audio = streams.FirstOrDefault(s => s.GetProperty("codec_type").GetString() == "audio");
        var copyAudio = audio.ValueKind != JsonValueKind.Undefined && audio.GetProperty("codec_name").GetString() == "aac";
        List<string> args = ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-protocol_whitelist", "file,pipe", "-i", source, "-map", $"0:{info.StreamIndex}", "-map", "0:a:0?"];
        args.AddRange(copyVideo ? ["-c:v", "copy"] : ["-c:v", "libx264", "-crf", "18", "-preset", "fast", "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2", "-pix_fmt", "yuv420p"]);
        args.AddRange(copyAudio ? ["-c:a", "copy"] : ["-c:a", "aac", "-b:a", "192k"]);
        args.AddRange(["-movflags", "+faststart", "-progress", "pipe:1", "-nostats", result]);
        await tools.RunAsync(false, args, line =>
        {
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && double.TryParse(line.AsSpan(12), CultureInfo.InvariantCulture, out var us) && double.IsFinite(us))
                report(copyVideo ? "Подготовка MP4…" : "Преобразование в MP4/H.264…", Math.Clamp(us / 1e6 / info.Duration * 100, 0, 99));
        }, ct);
    }

    private async Task RunYtDlpAsync(IEnumerable<string> args, Action<string> report, CancellationToken ct, int? height)
    {
        using var process = new Process { StartInfo = new(downloader.YtDlpPath)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8 } };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        ct.ThrowIfCancellationRequested();
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception) { throw new MediaException("Не удалось запустить yt-dlp.exe. Проверьте файл в tools или скачайте актуальную версию вручную.", 503); }
        using var registration = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        var errors = new Queue<string>();
        async Task DrainAsync(StreamReader reader, bool error)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                report(line);
                if (error) { errors.Enqueue(line.Length > 1000 ? line[..1000] : line); if (errors.Count > 8) errors.Dequeue(); }
            }
        }
        await Task.WhenAll(DrainAsync(process.StandardOutput, false), DrainAsync(process.StandardError, true), process.WaitForExitAsync());
        ct.ThrowIfCancellationRequested();
        // --max-downloads 1 exits with 101 after successfully downloading the one permitted video.
        if (process.ExitCode is not (0 or 101))
        {
            var detail = string.Join('\n', errors);
            var reason = detail.Contains("HTTP Error 429", StringComparison.OrdinalIgnoreCase) || detail.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase)
                ? "Сайт временно ограничил запросы (HTTP 429). Попробуйте позже."
                : detail.Contains("Sign in", StringComparison.OrdinalIgnoreCase) || detail.Contains("age-restricted", StringComparison.OrdinalIgnoreCase) || detail.Contains("confirm your age", StringComparison.OrdinalIgnoreCase)
                ? "Сайт требует входа или подтверждения возраста. Такие видео могут быть недоступны."
                : detail.Contains("Requested format is not available", StringComparison.OrdinalIgnoreCase)
                ? (height is null ? "yt-dlp не нашёл доступный видеоформат." : $"yt-dlp не нашёл доступный формат {height}p. Выберите другое качество или «Лучшее доступное». Доступные загрузчику форматы могут отличаться от списка на сайте.")
                : detail.Contains("no such option", StringComparison.OrdinalIgnoreCase) ? "Версия yt-dlp несовместима. Обновите yt-dlp.exe в папке tools."
                : detail.Contains("Unsupported URL", StringComparison.OrdinalIgnoreCase) ? "Ссылка не поддерживается yt-dlp."
                : "Проверьте ссылку и интернет. Видео может быть закрыто, удалено или ограничено сайтом; также может потребоваться обновление yt-dlp в tools.";
            logger.LogWarning("yt-dlp завершился с кодом {Code}", process.ExitCode);
            throw new MediaException($"Не удалось скачать видео. {reason}", 422);
        }
    }

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct)
    {
        lock (gate) { stopping = true; return active?.Finished.Task ?? Task.CompletedTask; }
    }
}
