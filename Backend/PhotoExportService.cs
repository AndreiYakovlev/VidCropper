using System.Globalization;

namespace VidCropper.Backend;

public sealed class PhotoExportService(PhotoStore store, MediaTools tools, MediaOptions options,
    ILogger<PhotoExportService> logger, IHostApplicationLifetime lifetime, ProcessingGate processingGate,
    AiPackages packages, AiRunner runner, ExportArchive archive) : IHostedService, IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, Job> jobs = [];
    private bool active;
    private bool stopping;

    private sealed class Job(Guid id, string root, string resultPath, string fileName, string contentType, bool preview)
    {
        public Guid Id { get; } = id;
        public string Root { get; } = root;
        public string ResultPath { get; } = resultPath;
        public string? BeforePath { get; set; }
        public string FileName { get; set; } = fileName;
        public string ContentType { get; } = contentType;
        public bool Preview { get; } = preview;
        public string? ArchivedPath { get; set; }
        public ProcessingTimer Timer { get; } = new();
        public CancellationTokenSource Cancellation { get; } = new();
        public Task Task { get; set; } = Task.CompletedTask;
        public string Status { get; set; } = "queued";
        public double Progress { get; set; }
        public string? Error { get; set; }
        public string? Stage { get; set; }
        public string? StageId { get; set; }
        public double? StageProgress { get; set; }
        public ImageInfo? Result { get; set; }
    }

    public PhotoExportSnapshot Start(PhotoExportRequest request, bool preview = false)
    {
        lock (gate)
        {
            if (stopping) throw new MediaException("Приложение завершает работу.", 503);
            if (active) throw new MediaException("Другая обработка уже выполняется. Дождитесь завершения или отмените её.", 409);
            var owner = processingGate.Acquire();
            PhotoStore.Lease? lease = null;
            try
            {
                lease = store.Acquire(request.MediaId);
                if (preview && request.Upscale is null)
                    throw new MediaException("Для сравнения включите Upscaler.");
                var size = PhotoExportSettings.Validate(request, lease.Source.Info,
                    options.MaxPhotoSide, options.MaxPhotoPixels);
                var installation = request.Upscale is null ? null : packages.Resolve(request.Upscale);
                var id = Guid.NewGuid();
                var root = Path.Combine(store.Root, $"job-{id:N}");
                Directory.CreateDirectory(root);
                var fileName = Path.GetFileNameWithoutExtension(lease.Source.Name) + "_edited" + size.Extension;
                var resultPath = Path.Combine(root, preview ? "after.png" : "result" + size.Extension);
                var job = new Job(id, root, resultPath, fileName, preview ? "image/png" : size.ContentType, preview);
                if (preview) job.BeforePath = Path.Combine(root, "before.png");
                jobs.Add(id, job);
                active = true;
                job.Task = Task.Run(() => RunAsync(job, request, size, lease, installation, owner));
                return Snapshot(job);
            }
            catch
            {
                lease?.Dispose();
                owner.Dispose();
                throw;
            }
        }
    }

    private async Task RunAsync(Job job, PhotoExportRequest request,
        (int Width, int Height, string Extension, string ContentType) size,
        PhotoStore.Lease lease, AiInstallation? installation, IDisposable owner)
    {
        using (owner)
        using (lease)
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(job.Cancellation.Token, lifetime.ApplicationStopping))
        {
            try
            {
                lock (gate) job.Status = "running";
                var ct = linked.Token;
                var crop = request.Crop!;
                var cropped = Path.Combine(job.Root, "cropped.png");
                Report(job, "prepare", "Подготовка фото", 0, 5);
                await tools.RunAsync(false,
                    ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", lease.Source.Path,
                     "-vf", FormattableString.Invariant($"crop={crop.Width}:{crop.Height}:{crop.X}:{crop.Y}:exact=1"),
                     "-frames:v", "1", "-pix_fmt", "rgba", "-map_metadata", "-1", cropped], null, ct);
                Report(job, "prepare", "Подготовка фото", 100, 20);

                if (job.BeforePath is not null)
                    await ResizePngAsync(cropped, job.BeforePath, size.Width, size.Height, ct);

                var processed = cropped;
                if (installation is not null)
                {
                    await packages.VerifyAsync(installation, ct);
                    var rgb = Path.Combine(job.Root, "rgb.png");
                    var alpha = Path.Combine(job.Root, "alpha.png");
                    var upscaled = Path.Combine(job.Root, "upscaled-rgb.png");
                    await tools.RunAsync(false,
                        ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", cropped,
                         "-frames:v", "1", "-pix_fmt", "rgb24", rgb], null, ct);
                    await tools.RunAsync(false,
                        ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", cropped,
                         "-vf", "alphaextract", "-frames:v", "1", "-pix_fmt", "gray", alpha], null, ct);
                    Report(job, "upscale", "AI-увеличение", 0, 25);
                    var runnerScale = installation.Model.VariableScale
                        ? request.Upscale!.Scale : installation.Model.NativeScale;
                    await runner.RunAsync(installation, rgb, upscaled, runnerScale, ct,
                        _ => Report(job, "upscale", "AI-увеличение", 100, 75));
                    processed = Path.Combine(job.Root, "composed.png");
                    var filter = $"[0:v]scale={size.Width}:{size.Height}:flags=lanczos,format=rgb24[rgb];" +
                        $"[1:v]scale={size.Width}:{size.Height}:flags=lanczos,format=gray[a];[rgb][a]alphamerge[out]";
                    await tools.RunAsync(false,
                        ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", upscaled, "-i", alpha,
                         "-filter_complex", filter, "-map", "[out]", "-frames:v", "1", "-pix_fmt", "rgba", processed], null, ct);
                    Report(job, "upscale", "AI-увеличение", 100, 80);
                }
                else if (crop.Width != size.Width || crop.Height != size.Height)
                {
                    processed = Path.Combine(job.Root, "resized.png");
                    await ResizePngAsync(cropped, processed, size.Width, size.Height, ct);
                }

                if (job.Preview)
                {
                    if (processed != job.ResultPath) File.Copy(processed, job.ResultPath, true);
                }
                else
                {
                    Report(job, "encode", "Сохранение фото", 0, 85);
                    await EncodeAsync(processed, job.ResultPath, request, size, ct);
                }
                Report(job, "encode", job.Preview ? "Подготовка сравнения" : "Сохранение фото", 100, 95);
                var resultProbe = await tools.ProbeImageAsync(job.ResultPath,
                    options.MaxPhotoSide, options.MaxPhotoPixels, true, ct);
                var resultInfo = new ImageInfo(resultProbe.Width, resultProbe.Height,
                    job.Preview ? "png" : request.Format.ToLowerInvariant() is "jpg" ? "jpeg" : request.Format.ToLowerInvariant(),
                    new FileInfo(job.ResultPath).Length,
                    (job.Preview || size.Extension != ".jpg") && lease.Source.Info.HasAlpha);
                if (!job.Preview)
                {
                    var saved = await archive.SaveAsync(job.ResultPath, job.FileName, ct);
                    lock (gate) { job.ArchivedPath = saved; job.FileName = Path.GetFileName(saved); }
                }
                lock (gate)
                {
                    job.Result = resultInfo;
                    job.Progress = 100;
                    job.StageProgress = 100;
                    job.Status = "completed";
                    job.Timer.Stop();
                }
                if (!job.Preview) TryDeleteDirectory(job.Root);
            }
            catch (OperationCanceledException)
            {
                TryDeleteDirectory(job.Root);
                lock (gate) { job.Status = "cancelled"; job.Timer.Stop(); }
            }
            catch (Exception exception)
            {
                TryDeleteDirectory(job.Root);
                logger.LogError(exception, "Ошибка обработки фото {JobId}", job.Id);
                lock (gate)
                {
                    job.Status = "failed";
                    job.Timer.Stop();
                    job.Error = exception is MediaException ? exception.Message :
                        "Не удалось обработать фото. Проверьте свободное место и журнал сервера.";
                }
            }
            finally { lock (gate) active = false; }
        }
    }

    private void Report(Job job, string id, string label, double stageProgress, double totalProgress)
    {
        lock (gate)
        {
            job.StageId = id;
            job.Stage = label;
            job.StageProgress = stageProgress;
            job.Progress = Math.Max(job.Progress, totalProgress);
            job.Timer.Report(id, stageProgress, 100);
        }
    }

    private async Task ResizePngAsync(string input, string output, int width, int height, CancellationToken ct) =>
        await tools.RunAsync(false,
            ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", input,
             "-vf", $"scale={width}:{height}:flags=lanczos", "-frames:v", "1", "-pix_fmt", "rgba", output], null, ct);

    private async Task EncodeAsync(string input, string output, PhotoExportRequest request,
        (int Width, int Height, string Extension, string ContentType) size, CancellationToken ct)
    {
        var common = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", input };
        switch (size.Extension)
        {
            case ".png":
                common.AddRange(["-frames:v", "1", "-pix_fmt", "rgba", "-c:v", "png",
                    "-compression_level", "6", "-pred", "mixed", "-map_metadata", "-1", output]);
                break;
            case ".jpg":
                var q = 2 + (int)Math.Round((100 - request.Quality!.Value) * 29 / 99d,
                    MidpointRounding.AwayFromZero);
                var backgroundWidth = size.Width + size.Width % 2;
                var backgroundHeight = size.Height + size.Height % 2;
                var filter = $"color=c=white:s={backgroundWidth}x{backgroundHeight},format=rgba[bg];" +
                    $"[0:v]format=rgba[fg];[bg][fg]overlay=shortest=1,format=rgba," +
                    $"crop={size.Width}:{size.Height}:0:0:exact=1,format=yuvj444p[out]";
                common.AddRange(["-filter_complex", filter, "-map", "[out]", "-frames:v", "1",
                    "-q:v", q.ToString(CultureInfo.InvariantCulture), "-map_metadata", "-1", output]);
                break;
            default:
                common.AddRange(["-frames:v", "1", "-c:v", "libwebp", "-quality",
                    request.Quality!.Value.ToString(CultureInfo.InvariantCulture), "-pix_fmt", "yuva420p",
                    "-map_metadata", "-1", output]);
                break;
        }
        await tools.RunAsync(false, common, null, ct);
    }

    public PhotoExportSnapshot Get(Guid id) { lock (gate) return Snapshot(Find(id)); }
    private Job Find(Guid id) => jobs.TryGetValue(id, out var job)
        ? job : throw new MediaException("Обработка фото не найдена.", 404);
    private static PhotoExportSnapshot Snapshot(Job job) => new(job.Id, job.Status, job.Progress,
        job.Error, job.Result, job.FileName, job.ContentType, job.Preview, job.Stage, job.StageId,
        job.StageProgress, job.Timer.ElapsedSeconds, job.Timer.StageElapsedSeconds, job.Timer.RemainingSeconds);

    public async Task<PhotoExportSnapshot> CancelAsync(Guid id)
    {
        Job job;
        lock (gate)
        {
            job = Find(id);
            if (job.Status is "queued" or "running" or "finalizing") job.Cancellation.Cancel();
        }
        await job.Task;
        lock (gate) return Snapshot(job);
    }

    public async Task DeleteAsync(Guid id)
    {
        Job? job;
        lock (gate) jobs.TryGetValue(id, out job);
        if (job is null) return;
        await CancelAsync(id);
        lock (gate)
        {
            if (!jobs.Remove(id)) return;
            TryDeleteDirectory(job.Root);
            job.Cancellation.Dispose();
        }
    }

    public (FileStream Stream, string Name, string ContentType) OpenResult(Guid id, string? variant = null)
    {
        lock (gate)
        {
            var job = Find(id);
            if (job.Status != "completed") throw new MediaException("Фото ещё не готово.", 409);
            string path;
            if (variant is not null)
            {
                if (!job.Preview || variant is not ("before" or "after"))
                    throw new MediaException("Сравнение не найдено.", 404);
                path = variant == "before" ? job.BeforePath! : job.ResultPath;
            }
            else path = job.ArchivedPath ?? job.ResultPath;
            return (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                65536, FileOptions.Asynchronous), job.FileName, variant is null ? job.ContentType : "image/png");
        }
    }

    private void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (IOException exception) { logger.LogWarning(exception, "Не удалось очистить {Path}", path); }
        catch (UnauthorizedAccessException exception) { logger.LogWarning(exception, "Не удалось очистить {Path}", path); }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task[] pending;
        lock (gate)
        {
            stopping = true;
            foreach (var job in jobs.Values) job.Cancellation.Cancel();
            pending = jobs.Values.Select(job => job.Task).ToArray();
        }
        await Task.WhenAll(pending);
    }
    public void Dispose() { foreach (var job in jobs.Values) job.Cancellation.Dispose(); }
}
