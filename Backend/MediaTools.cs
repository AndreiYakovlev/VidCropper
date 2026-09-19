using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace VidCropper.Backend;

public sealed class MediaTools(MediaOptions options, ILogger<MediaTools> logger)
{
    private static string Resolve(string configured, string name)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        var bundled = Path.Combine(AppContext.BaseDirectory, "tools", name + (OperatingSystem.IsWindows() ? ".exe" : ""));
        return File.Exists(bundled) ? bundled : name;
    }

    public async Task<string?> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            foreach (var probe in new[] { false, true })
                await RunAsync(probe, ["-version"], null, cancellationToken);
            return null;
        }
        catch (MediaException exception) { return exception.Message; }
    }

    public async Task<string> RunAsync(bool probe, IEnumerable<string> arguments,
        Action<string>? onLine, CancellationToken cancellationToken)
    {
        var name = probe ? "ffprobe" : "ffmpeg";
        var executable = Resolve(probe ? options.FfprobePath : options.FfmpegPath, name);
        using var process = new Process { StartInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        cancellationToken.ThrowIfCancellationRequested();
        try { process.Start(); }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            throw new MediaException($"Не найден {name}. Укажите путь в Media:{(probe ? "FfprobePath" : "FfmpegPath")} или положите программу в папку tools рядом с приложением.", 503);
        }
        using var registration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        var output = new StringBuilder();
        var errors = new StringBuilder();
        async Task DrainAsync(StreamReader reader, StringBuilder destination, bool report)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (destination.Length < 128 * 1024) destination.AppendLine(line);
                if (report) onLine?.Invoke(line);
            }
        }
        await Task.WhenAll(DrainAsync(process.StandardOutput, output, true),
            DrainAsync(process.StandardError, errors, false), process.WaitForExitAsync());
        cancellationToken.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
        {
            logger.LogWarning("{Tool} завершился с кодом {Code}: {Error}", name, process.ExitCode, errors.ToString());
            throw new MediaException(probe ? "Не удалось прочитать видео. Файл повреждён или формат не поддерживается." :
                "FFmpeg не смог обработать видео. Проверьте свободное место и поддержку кодека; подробности — в журнале сервера.", 422);
        }
        return output.ToString();
    }

    public async Task<VideoInfo> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        string json;
        try
        {
            json = await RunAsync(true, ["-v", "error", "-protocol_whitelist", "file,pipe", "-show_streams", "-show_format", "-of", "json", path], null, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new MediaException("Чтение метаданных заняло слишком много времени.", 422); }
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var streams = root.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.FirstOrDefault(s => GetString(s, "codec_type") == "video" &&
            (!s.TryGetProperty("disposition", out var disposition) || GetNumber(disposition, "attached_pic") == 0));
        if (video.ValueKind == JsonValueKind.Undefined) throw new MediaException("В файле нет видеодорожки.");
        var format = root.GetProperty("format");
        var width = (int)GetNumber(video, "width");
        var height = (int)GetNumber(video, "height");
        var sar = Fraction(GetString(video, "sample_aspect_ratio"), ':');
        if (sar > 0) width = (int)Math.Round(width * sar);
        double rotation = 0;
        if (video.TryGetProperty("side_data_list", out var sides))
            foreach (var side in sides.EnumerateArray())
                if (side.TryGetProperty("rotation", out _)) rotation = GetNumber(side, "rotation");
        if (video.TryGetProperty("tags", out var tags) && tags.TryGetProperty("rotate", out _)) rotation = GetNumber(tags, "rotate");
        var angle = ((rotation % 360) + 360) % 360;
        if (Math.Abs(angle - 90) < 1 || Math.Abs(angle - 270) < 1) (width, height) = (height, width);
        var duration = GetNumber(video, "duration");
        if (duration <= 0) duration = GetNumber(format, "duration");
        var fps = Fraction(GetString(video, "avg_frame_rate"), '/');
        if (fps <= 0) fps = Fraction(GetString(video, "r_frame_rate"), '/');
        if (width < 1 || height < 1 || width > 32768 || height > 32768 || !double.IsFinite(duration) || duration <= 0)
            throw new MediaException("Недопустимые размеры или длительность видео.");
        return new VideoInfo(width, height, duration, fps, GetString(video, "codec_name"),
            (long)GetNumber(format, "bit_rate"), streams.Any(s => GetString(s, "codec_type") == "audio"),
            (int)GetNumber(video, "index"), new FileInfo(path).Length);
    }

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.ToString() : "";
    private static double GetNumber(JsonElement element, string name) =>
        double.TryParse(GetString(element, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : 0;
    private static double Fraction(string text, char separator)
    {
        var parts = text.Split(separator);
        return parts.Length == 2 && double.TryParse(parts[0], CultureInfo.InvariantCulture, out var a) &&
            double.TryParse(parts[1], CultureInfo.InvariantCulture, out var b) && b != 0 && double.IsFinite(a / b) ? a / b : 0;
    }
}
