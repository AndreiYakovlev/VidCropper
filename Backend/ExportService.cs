using System.Globalization;

namespace VidCropper.Backend;

public sealed class ExportService(MediaStore store, MediaTools tools, ILogger<ExportService> logger,
    IHostApplicationLifetime lifetime) : IHostedService, IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, Job> jobs = [];
    private bool active;
    private bool stopping;

    private sealed class Job(Guid id, string path, string fileName)
    {
        public Guid Id { get; } = id;
        public string Path { get; } = path;
        public string FileName { get; } = fileName;
        public CancellationTokenSource Cancellation { get; } = new();
        public Task Task { get; set; } = Task.CompletedTask;
        public string Status { get; set; } = "queued";
        public double Progress { get; set; }
        public string? Error { get; set; }
        public VideoInfo? Result { get; set; }
    }

    public ExportSnapshot Start(ExportRequest request)
    {
        lock (gate)
        {
            if (stopping) throw new MediaException("Приложение завершает работу.", 503);
            if (active) throw new MediaException("Другой экспорт уже выполняется. Дождитесь завершения или отмените его.", 409);
            var lease = store.Acquire(request.MediaId);
            try
            {
                var size = ExportSettings.Validate(request, lease.Source.Info);
                var trim = ExportSettings.ValidateTrim(request, lease.Source.Info);
                var crf = ExportSettings.ResolveCrf(request.Quality);
                var id = Guid.NewGuid();
                var job = new Job(id, Path.Combine(store.Root, $"{id:N}.mp4"),
                    Path.GetFileNameWithoutExtension(lease.Source.Name) + "_cropped.mp4");
                jobs.Add(id, job);
                active = true;
                job.Task = Task.Run(() => RunAsync(job, request, size, trim, crf, lease));
                return Snapshot(job);
            }
            catch { lease.Dispose(); throw; }
        }
    }

    private async Task RunAsync(Job job, ExportRequest request, (int Width, int Height) size, TrimRange trim, int crf, MediaStore.Lease lease)
    {
        using (lease)
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(job.Cancellation.Token, lifetime.ApplicationStopping))
        {
            try
            {
                var source = lease.Source;
                var crop = request.Crop!;
                // Retain the frame covering the seek point, including a sub-frame selection at EOF.
                // fps trims negative preroll timestamps; eof_action keeps the final partial frame.
                // Normalize display geometry after autorotation, before applying browser coordinates.
                var filter = FormattableString.Invariant($"fps={request.Fps}:start_time=0:eof_action=pass,scale={source.Info.Width}:{source.Info.Height}:flags=lanczos,setsar=1,crop={crop.Width}:{crop.Height}:{crop.X}:{crop.Y}:exact=1,scale={size.Width}:{size.Height}:flags=lanczos,setsar=1");
                List<string> arguments = ["-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                    "-protocol_whitelist", "file,pipe", "-noaccurate_seek", "-ss", trim.Start.ToString("R", CultureInfo.InvariantCulture),
                    "-i", source.Path, "-t", trim.Duration.ToString("R", CultureInfo.InvariantCulture),
                    "-map", $"0:{source.Info.StreamIndex}", "-vf", filter];
                if (request.Audio && source.Info.HasAudio)
                    arguments.AddRange(["-map", "0:a:0?", "-af", "atrim=start=0", "-c:a", "aac", "-b:a", "192k"]);
                else arguments.Add("-an");
                arguments.AddRange(["-c:v", "libx264", "-crf", crf.ToString(CultureInfo.InvariantCulture), "-preset", "medium", "-pix_fmt", "yuv420p",
                    "-map_metadata", "-1", "-metadata:s:v:0", "rotate=0", "-movflags", "+faststart", "-progress", "pipe:1", "-nostats", job.Path]);
                lock (gate) job.Status = "running";
                await tools.RunAsync(false, arguments, line =>
                {
                    if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                        double.TryParse(line.AsSpan(12), CultureInfo.InvariantCulture, out var microseconds) && double.IsFinite(microseconds))
                    {
                        lock (gate) job.Progress = Math.Max(job.Progress, Math.Clamp(microseconds / 1_000_000 / trim.Duration * 100, 0, 99.5));
                    }
                }, linked.Token);
                lock (gate) job.Status = "finalizing";
                var info = await tools.ProbeAsync(job.Path, linked.Token);
                lock (gate)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    job.Result = info;
                    job.Progress = 100;
                    job.Status = "completed";
                }
            }
            catch (OperationCanceledException)
            {
                store.TryDelete(job.Path);
                lock (gate) job.Status = "cancelled";
            }
            catch (Exception exception)
            {
                store.TryDelete(job.Path);
                logger.LogError(exception, "Ошибка экспорта {JobId}", job.Id);
                lock (gate)
                {
                    job.Status = "failed";
                    job.Error = exception is MediaException ? exception.Message : "Не удалось завершить экспорт. Проверьте свободное место и журнал сервера.";
                }
            }
            finally { lock (gate) active = false; }
        }
    }

    public ExportSnapshot Get(Guid id) { lock (gate) return Snapshot(Find(id)); }
    private Job Find(Guid id) => jobs.TryGetValue(id, out var job) ? job : throw new MediaException("Экспорт не найден.", 404);
    private static ExportSnapshot Snapshot(Job job) => new(job.Id, job.Status, job.Progress, job.Error, job.Result, job.FileName);

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
            if (jobs.Remove(id)) { store.TryDelete(job.Path); job.Cancellation.Dispose(); }
        }
    }

    public (FileStream Stream, string Name) OpenResult(Guid id)
    {
        lock (gate)
        {
            var job = Find(id);
            if (job.Status != "completed") throw new MediaException("Файл ещё не готов.", 409);
            return (new FileStream(job.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous), job.FileName);
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
