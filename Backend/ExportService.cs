using System.Globalization;

namespace VidCropper.Backend;

public sealed class ExportService(MediaStore store, MediaTools tools, ILogger<ExportService> logger,
    IHostApplicationLifetime lifetime, ProcessingGate processingGate, AiPackages packages, AiPipeline ai, ExportArchive archive) : IHostedService, IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, Job> jobs = [];
    private bool active;
    private bool stopping;

    private sealed class Job(Guid id, string path, string fileName)
    {
        public Guid Id { get; } = id;
        public string Path { get; } = path;
        public string FileName { get; set; } = fileName;
        public string? ArchivedPath { get; set; }
        public ProcessingTimer Timer { get; } = new();
        public CancellationTokenSource Cancellation { get; } = new();
        public Task Task { get; set; } = Task.CompletedTask;
        public string? BeforePath { get; set; }
        public string? Stage { get; set; }
        public string? StageId { get; set; }
        public double? StageProgress { get; set; }
        public bool FramesTotalEstimated { get; set; }
        public long FramesDone { get; set; }
        public long FramesTotal { get; set; }
        public string Status { get; set; } = "queued";
        public double Progress { get; set; }
        public string? Error { get; set; }
        public VideoInfo? Result { get; set; }
    }

    public ExportSnapshot Start(ExportRequest request, double? previewPosition = null)
    {
        lock (gate)
        {
            if (stopping) throw new MediaException("Приложение завершает работу.", 503);
            if (active) throw new MediaException("Другой экспорт уже выполняется. Дождитесь завершения или отмените его.", 409);
            var owner = processingGate.Acquire();
            MediaStore.Lease? lease = null;
            try
            {
                lease = store.Acquire(request.MediaId);
                if (previewPosition is not null)
                {
                    if ((request.Upscale is null && request.Interpolation is null) || !double.IsFinite(previewPosition.Value)) throw new MediaException("Для пробы выберите AI-модель и позицию видео.");
                    var range = ExportSettings.ValidateTrim(request, lease.Source.Info);
                    var start = Math.Clamp(previewPosition.Value, range.Start, range.End);
                    if (range.End - start < Math.Min(0.01, range.Duration)) start = Math.Max(range.Start, range.End - 3);
                    request = request with { StartSeconds = start, EndSeconds = Math.Min(range.End, start + 3), Audio = false };
                }
                var installation = request.Upscale is null ? null : packages.Resolve(request.Upscale);
                var interpolationInstallation = request.Interpolation is null ? null : packages.ResolveInterpolation(request.Interpolation);
                var size = ExportSettings.Validate(request, lease.Source.Info);
                var trim = ExportSettings.ValidateTrim(request, lease.Source.Info);
                var crf = ExportSettings.ResolveCrf(request.Quality);
                var id = Guid.NewGuid();
                var job = new Job(id, Path.Combine(store.Root, $"{id:N}.mp4"),
                    Path.GetFileNameWithoutExtension(lease.Source.Name) + "_cropped.mp4");
                if (previewPosition is not null) job.BeforePath = Path.Combine(store.Root, $"{id:N}.before.mp4");
                jobs.Add(id, job);
                active = true;
                job.Task = Task.Run(() => RunAsync(job, request, size, trim, crf, lease, installation, interpolationInstallation, owner));
                return Snapshot(job);
            }
            catch { lease?.Dispose(); owner.Dispose(); throw; }
        }
    }

    private async Task RunAsync(Job job, ExportRequest request, (int Width, int Height) size, TrimRange trim, int crf, MediaStore.Lease lease, AiInstallation? installation, AiInstallation? interpolationInstallation, IDisposable owner)
    {
        using (owner)
        using (lease)
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(job.Cancellation.Token, lifetime.ApplicationStopping))
        {
            try
            {
                lock (gate) job.Status = "running";
                if (installation is not null || interpolationInstallation is not null)
                {
                    var stages = new List<string> { "extract" };
                    if (interpolationInstallation is not null) stages.Add("scenes");
                    if (installation is not null) stages.Add("upscale");
                    if (interpolationInstallation is not null) stages.AddRange(["resize", "interpolate"]);
                    stages.Add("encode");
                    if (job.BeforePath is not null) stages.Add("compare");
                    await ai.RunAsync(request, lease.Source, trim, size, crf, installation, job.Path, progress =>
                    {
                        lock (gate)
                        {
                            job.Stage = progress.Label; job.StageId = progress.Id;
                            job.Timer.Report(progress.Id, progress.Done, progress.Total);
                            job.StageProgress = progress.Percent; job.FramesTotalEstimated = progress.Estimated;
                            job.FramesDone = progress.Done; job.FramesTotal = progress.Total;
                            job.Progress = Math.Max(job.Progress, (stages.IndexOf(progress.Id) + progress.Percent / 100) * 99 / stages.Count);
                        }
                    }, linked.Token, job.BeforePath, interpolationInstallation);
                }
                else
                {
                    lock (gate) job.Timer.Report("encode", 0, 100);
                    await RunNormalAsync(request, lease.Source, size, trim, crf, job.Path,
                        progress => { lock (gate) { job.Progress = Math.Max(job.Progress, progress); job.Timer.Report("encode", progress, 100); } }, linked.Token);
                }
                lock (gate) { job.Status = "finalizing"; job.Timer.Report("finalize", 0, 0); }
                var info = await tools.ProbeAsync(job.Path, linked.Token);
                if (job.BeforePath is null)
                {
                    var saved = await archive.SaveAsync(job.Path, job.FileName, linked.Token);
                    lock (gate) { job.ArchivedPath = saved; job.FileName = Path.GetFileName(saved); }
                    store.TryDelete(job.Path);
                }
                lock (gate)
                {
                    if (job.ArchivedPath is null) linked.Token.ThrowIfCancellationRequested();
                    job.Result = info;
                    job.Progress = 100;
                    job.Status = "completed";
                    job.Timer.Stop();
                }
            }
            catch (OperationCanceledException)
            {
                store.TryDelete(job.Path);
                if (job.BeforePath is not null) store.TryDelete(job.BeforePath);
                lock (gate) { job.Status = "cancelled"; job.Timer.Stop(); }
            }
            catch (Exception exception)
            {
                store.TryDelete(job.Path);
                if (job.BeforePath is not null) store.TryDelete(job.BeforePath);
                logger.LogError(exception, "Ошибка экспорта {JobId}", job.Id);
                lock (gate)
                {
                    job.Status = "failed";
                    job.Timer.Stop();
                    job.Error = exception is MediaException ? exception.Message : "Не удалось завершить экспорт. Проверьте свободное место и журнал сервера.";
                }
            }
            finally { lock (gate) active = false; }
        }
    }

    private async Task RunNormalAsync(ExportRequest request, MediaStore.Source source, (int Width, int Height) size, TrimRange trim, int crf, string path, Action<double> report, CancellationToken ct)
    {
        var filter = ExportFilters.Crop(request, source.Info) + "," + ExportFilters.Resize(size.Width, size.Height);
        List<string> arguments = ["-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-protocol_whitelist", "file,pipe", "-noaccurate_seek", "-ss", trim.Start.ToString("R", CultureInfo.InvariantCulture),
            "-i", source.Path, "-t", trim.Duration.ToString("R", CultureInfo.InvariantCulture),
            "-map", $"0:{source.Info.StreamIndex}", "-vf", filter];
        if (request.Audio && source.Info.HasAudio)
            arguments.AddRange(["-map", "0:a:0?", "-af", "atrim=start=0", "-c:a", "aac", "-b:a", "192k"]);
        else arguments.Add("-an");
        arguments.AddRange(["-c:v", "libx264", "-crf", crf.ToString(CultureInfo.InvariantCulture), "-preset", "medium", "-pix_fmt", "yuv420p",
            "-map_metadata", "-1", "-metadata:s:v:0", "rotate=0", "-movflags", "+faststart", "-progress", "pipe:1", "-nostats", path]);
        await tools.RunAsync(false, arguments, line =>
        {
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                double.TryParse(line.AsSpan(12), CultureInfo.InvariantCulture, out var microseconds) && double.IsFinite(microseconds))
            {
                report(Math.Clamp(microseconds / 1_000_000 / trim.Duration * 100, 0, 99.5));
            }
        }, ct);
    }

    public ExportSnapshot Get(Guid id) { lock (gate) return Snapshot(Find(id)); }
    private Job Find(Guid id) => jobs.TryGetValue(id, out var job) ? job : throw new MediaException("Экспорт не найден.", 404);
    private static ExportSnapshot Snapshot(Job job) => new(job.Id, job.Status, job.Progress, job.Error, job.Result, job.FileName, job.Stage, job.FramesDone, job.FramesTotal, job.BeforePath is not null, job.StageId, job.StageProgress, job.FramesTotalEstimated, job.Timer.ElapsedSeconds, job.Timer.StageElapsedSeconds, job.Timer.RemainingSeconds);

    public async Task<ExportSnapshot> CancelAsync(Guid id)
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
            if (jobs.Remove(id)) { store.TryDelete(job.Path); if (job.BeforePath is not null) store.TryDelete(job.BeforePath); job.Cancellation.Dispose(); }
        }
    }

    public (FileStream Stream, string Name) OpenResult(Guid id, bool before = false)
    {
        lock (gate)
        {
            var job = Find(id);
            if (job.Status != "completed") throw new MediaException("Файл ещё не готов.", 409);
            if (before && job.BeforePath is null) throw new MediaException("Сравнение отсутствует.", 404);
            return (new FileStream(before ? job.BeforePath! : job.ArchivedPath ?? job.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous), job.FileName);
        }
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
