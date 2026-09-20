using System.Globalization;

namespace VidCropper.Backend;

public class RifeRunner(MediaTools tools)
{
    public virtual async Task RunAsync(AiInstallation installation, FrameSequence input, FrameSequence output,
        CancellationToken ct, Action<string>? completed = null)
    {
        if (output.Count > int.MaxValue) throw new MediaException("Слишком много кадров для RIFE. Выберите более короткий отрезок.");
        // A single source frame has no neighbouring frame to interpolate.
        if (input.Count == 1)
        {
            for (long i = 0; i < output.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                File.Copy(input.FilePath(0), output.FilePath(i));
                completed?.Invoke(output.Name(i));
            }
            return;
        }
        string[] args = ["-i", input.Directory, "-o", output.Directory,
            "-m", Path.Combine(installation.ModelsPath, installation.Model.FileName),
            "-n", output.Count.ToString(CultureInfo.InvariantCulture), "-f", "%08d.png", "-j", "1:1:1", "-v"];
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
            throw new MediaException($"RIFE не смог обработать кадры. Проверьте Vulkan, драйвер и свободную видеопамять. {e.Message}", 422);
        }
    }

    // Called only by the explicit GPU check action, never by installation.
    public virtual async Task CheckAsync(AiInstallation installation, CancellationToken ct)
    {
        var root = Path.Combine(Path.GetTempPath(), "VidCropper-rife-check-" + Guid.NewGuid().ToString("N"));
        var input = new FrameSequence(Path.Combine(root, "input"), "%08d.png", 1, 2, 64, 64, 1);
        var output = input with { Directory = Path.Combine(root, "output"), Count = 6, Fps = 3 };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        Directory.CreateDirectory(input.Directory); Directory.CreateDirectory(output.Directory);
        try
        {
            await tools.RunAsync(false, ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
                "testsrc2=size=64x64:rate=1", "-frames:v", "2", Path.Combine(input.Directory, input.Pattern)], null, timeout.Token);
            await RunAsync(installation, input, output, timeout.Token);
            await output.ValidateAsync(timeout.Token);
        }
        finally { Directory.Delete(root, true); }
    }
}

public sealed class SceneCuts(MediaTools tools)
{
    public async Task<HashSet<long>> DetectAsync(FrameSequence input, Action<FrameProgress> report, CancellationToken ct)
    {
        var cuts = new HashSet<long>();
        long frame = 0;
        report(new("scenes", "Поиск смены сцен", 0, input.Count));
        await tools.RunAsync(false, ["-hide_banner", "-loglevel", "error", "-nostdin", "-framerate", input.Fps.ToString(),
            "-start_number", input.StartNumber.ToString(CultureInfo.InvariantCulture), "-i", Path.Combine(input.Directory, input.Pattern),
            "-vf", "scdet=threshold=10,metadata=mode=print:key=lavfi.scd.score:file=-", "-an", "-f", "null", "-"], line =>
        {
            if (line.StartsWith("frame:", StringComparison.Ordinal))
            {
                var value = line[6..].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                if (long.TryParse(value, out var index)) frame = index;
                report(new("scenes", "Поиск смены сцен", Math.Min(frame + 1, input.Count), input.Count));
            }
            else if (line.StartsWith("lavfi.scd.score=", StringComparison.Ordinal) &&
                double.TryParse(line[16..], CultureInfo.InvariantCulture, out var score) && score >= 10 && frame > 0)
                cuts.Add(frame);
        }, ct);
        report(new("scenes", "Поиск смены сцен", input.Count, input.Count));
        return cuts;
    }

    public static void Restore(FrameSequence input, FrameSequence output, int multiplier, IEnumerable<long> cuts, CancellationToken ct)
    {
        foreach (var cut in cuts)
            for (var offset = 1; offset < multiplier; offset++)
            {
                ct.ThrowIfCancellationRequested();
                File.Copy(input.FilePath(cut - 1), output.FilePath((cut - 1) * multiplier + offset), true);
            }
    }
}
