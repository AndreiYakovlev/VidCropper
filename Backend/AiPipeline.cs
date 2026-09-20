using System.Globalization;

namespace VidCropper.Backend;

public static class ExportFilters
{
    public static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    public static string Crop(ExportRequest request, VideoInfo source)
    {
        var c = request.Crop!;
        return FormattableString.Invariant($"fps={request.Fps}:start_time=0:eof_action=pass,scale={source.Width}:{source.Height}:flags=lanczos,setsar=1,crop={c.Width}:{c.Height}:{c.X}:{c.Y}:exact=1");
    }
    public static string Resize(int width, int height) => $"scale={width}:{height}:flags=lanczos,setsar=1";
}

public class AiRunner(MediaTools tools)
{
    public virtual async Task RunAsync(AiInstallation installation, string input, string output, int scale, CancellationToken ct, Action<string>? completed = null)
    {
        var args = new[] { "-i", input, "-o", output, "-m", installation.ModelsPath, "-n", installation.Model.FileName,
            "-s", scale.ToString(CultureInfo.InvariantCulture), "-f", "png", "-j", "1:1:1", "-v" };
        try
        {
            await using var process = new MediaProcess(installation.Executable, args, ct, onLine: line =>
            {
                var marker = line.LastIndexOf(" -> ", StringComparison.Ordinal);
                if (marker >= 0 && line.EndsWith(" done", StringComparison.Ordinal))
                    completed?.Invoke(Path.GetFileName(line[(marker + 4)..^5]));
            });
            await process.CompleteAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new MediaException($"AI-модель не смогла обработать кадры. Проверьте поддержку Vulkan, драйвер видеокарты и свободную видеопамять. {e.Message}", 422);
        }
    }

    public virtual async Task CheckAsync(AiInstallation installation, CancellationToken ct)
    {
        // Sibling scratch never enters the immutable installed model directory.
        var path = Path.Combine(Path.GetTempPath(), "VidCropper-ai-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            var input = Path.Combine(path, "in.png"); var output = Path.Combine(path, "out.png");
            await tools.RunAsync(false, ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=64x64:rate=1", "-frames:v", "1", input], null, timeout.Token);
            var scale = installation.Model.VariableScale ? 2 : installation.Model.NativeScale;
            await RunAsync(installation, input, output, scale, timeout.Token);
            await using var stream = File.OpenRead(output);
            var size = await PngFrames.CopyFrameAsync(stream, Stream.Null, timeout.Token);
            if (size != (64 * scale, 64 * scale)) throw new MediaException("AI-модуль вернул неверный размер тестового кадра.");
        }
        finally { Directory.Delete(path, true); }
    }
}

public sealed class AiPipeline(MediaTools tools, AiRunner runner, AiPackages packages, FrameWorkspace workspace, ILogger<AiPipeline> logger)
{
    public async Task RunAsync(ExportRequest request, MediaStore.Source source, TrimRange trim, (int Width, int Height) size,
        int crf, AiInstallation installation, string result, Action<FrameProgress> report, CancellationToken ct, string? beforePath = null)
    {
        await packages.VerifyAsync(installation, ct);
        var scratch = Path.Combine(workspace.Root, Path.GetFileNameWithoutExtension(result));
        var crop = request.Crop!;
        var input = new FrameSequence(Path.Combine(scratch, "input"), "%08d.png", 1, 0, crop.Width, crop.Height, request.Fps);
        using var stopped = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Exception? spaceError = null;
        workspace.CheckSpace(scratch, result);
        Directory.CreateDirectory(input.Directory);
        async Task MonitorAsync()
        {
            try
            {
                while (true)
                {
                    workspace.CheckSpace(scratch, result);
                    await Task.Delay(500, stopped.Token);
                }
            }
            catch (OperationCanceledException) when (stopped.IsCancellationRequested) { }
            catch (Exception e) { spaceError = e; await stopped.CancelAsync(); }
        }
        var monitor = MonitorAsync();
        try
        {
            var token = stopped.Token;
            input = await ExtractAsync(request, source, trim, input, report, token);
            var scale = installation.Model.VariableScale ? request.Upscale!.Scale : installation.Model.NativeScale;
            var output = input with { Directory = Path.Combine(scratch, "upscaled"), Width = input.Width * scale, Height = input.Height * scale };
            Directory.CreateDirectory(output.Directory);
            report(new("upscale", "AI-увеличение", 0, input.Count));
            var completed = new HashSet<string>(StringComparer.Ordinal);
            await runner.RunAsync(installation, input.Directory, output.Directory, scale, token, name =>
            {
                lock (completed)
                {
                    if (name.EndsWith(".png", StringComparison.Ordinal) && long.TryParse(Path.GetFileNameWithoutExtension(name), out var n)
                        && n >= input.StartNumber && n < input.StartNumber + input.Count && name == input.Name(n - input.StartNumber) && completed.Add(name))
                        report(new("upscale", "AI-увеличение", completed.Count, input.Count));
                }
            });
            await output.ValidateAsync(token);
            report(new("upscale", "AI-увеличение", input.Count, input.Count));
            if (beforePath is null) Directory.Delete(input.Directory, true);
            await EncodeAsync(output, request, source, trim, size, crf, result, "encode", "Сборка видео", report, token);
            Directory.Delete(output.Directory, true);
            if (beforePath is not null)
                await EncodeAsync(input, request with { Audio = false }, source, trim,
                    (Math.Max(2, crop.Width / 2 * 2), Math.Max(2, crop.Height / 2 * 2)), crf, beforePath, "compare", "Подготовка сравнения", report, token);
        }
        catch (OperationCanceledException) when (spaceError is not null && !ct.IsCancellationRequested)
        { throw spaceError; }
        catch (MediaException)
        {
            // A writer can fail before the next monitor tick; prefer an actionable disk error.
            workspace.CheckSpace(scratch, result);
            throw;
        }
        catch (IOException)
        { throw new MediaException("Ошибка записи или чтения временных кадров. Проверьте свободное место и доступ к папке temp/ai.", 507); }
        finally
        {
            await stopped.CancelAsync();
            await monitor;
            try
            {
                if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
                if (Directory.Exists(workspace.Root) && !Directory.EnumerateFileSystemEntries(workspace.Root).Any()) Directory.Delete(workspace.Root);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { logger.LogWarning(e, "Очистка AI-кадров {Path}", scratch); }
        }
    }

    private async Task<FrameSequence> ExtractAsync(ExportRequest request, MediaStore.Source source, TrimRange trim,
        FrameSequence sequence, Action<FrameProgress> report, CancellationToken ct)
    {
        var expected = Math.Max(1L, (long)Math.Ceiling(trim.Duration * request.Fps));
        report(new("extract", "Извлечение кадров", 0, expected, true));
        await using var decoder = new MediaProcess(tools.FfmpegExecutable,
            ["-hide_banner", "-loglevel", "error", "-nostdin", "-protocol_whitelist", "file,pipe", "-noaccurate_seek",
            "-ss", ExportFilters.Number(trim.Start), "-i", source.Path, "-t", ExportFilters.Number(trim.Duration),
            "-map", $"0:{source.Info.StreamIndex}", "-vf", ExportFilters.Crop(request, source.Info), "-an",
            "-c:v", "png", "-pix_fmt", "rgb24", "-threads", "1", "-f", "image2pipe", "pipe:1"], ct, pipeOutput: true);
        long count = 0;
        while (true)
        {
            var path = sequence.FilePath(count);
            (int Width, int Height)? dimensions;
            await using (var file = File.Create(path)) dimensions = await PngFrames.CopyFrameAsync(decoder.Output, file, ct);
            if (dimensions is null) { File.Delete(path); break; }
            if (dimensions != (sequence.Width, sequence.Height)) throw new MediaException("Размер исходного кадра не совпал с crop.");
            count++;
            report(new("extract", "Извлечение кадров", count, Math.Max(expected, count), true));
        }
        await decoder.CompleteAsync(ct);
        if (count == 0) throw new MediaException("Выбранный отрезок не содержит кадров.");
        report(new("extract", "Извлечение кадров", count, count));
        return sequence with { Count = count };
    }

    private async Task EncodeAsync(FrameSequence sequence, ExportRequest request, MediaStore.Source source, TrimRange trim,
        (int Width, int Height) size, int crf, string result, string stage, string label, Action<FrameProgress> report, CancellationToken ct)
    {
        report(new(stage, label, 0, sequence.Count));
        List<string> args = ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-framerate", ExportFilters.Number(sequence.Fps),
            "-start_number", sequence.StartNumber.ToString(CultureInfo.InvariantCulture), "-i", Path.Combine(sequence.Directory, sequence.Pattern)];
        if (request.Audio && source.Info.HasAudio)
            args.AddRange(["-protocol_whitelist", "file,pipe", "-noaccurate_seek", "-ss", ExportFilters.Number(trim.Start), "-i", source.Path,
                "-map", "1:a:0?", "-af", "atrim=start=0", "-c:a", "aac", "-b:a", "192k"]);
        else args.Add("-an");
        args.AddRange(["-map", "0:v:0", "-t", ExportFilters.Number(trim.Duration), "-vf", ExportFilters.Resize(size.Width, size.Height),
            "-c:v", "libx264", "-crf", crf.ToString(CultureInfo.InvariantCulture), "-preset", "medium", "-pix_fmt", "yuv420p",
            "-map_metadata", "-1", "-metadata:s:v:0", "rotate=0", "-movflags", "+faststart", "-progress", "pipe:1", "-nostats", result]);
        await tools.RunAsync(false, args, line =>
        {
            if (line.StartsWith("frame=", StringComparison.Ordinal) && long.TryParse(line.AsSpan(6).Trim(), out var frame))
                report(new(stage, label, Math.Min(frame, sequence.Count), sequence.Count));
        }, ct);
        report(new(stage, label, sequence.Count, sequence.Count));
    }
}
