using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace VidCropper.Backend;

public sealed class MediaTools(MediaOptions options, ILogger<MediaTools> logger)
{
    public int PngThreads { get; } = options.PngThreads switch
    {
        < 0 => throw new ArgumentOutOfRangeException(nameof(options.PngThreads), "Media:PngThreads должен быть неотрицательным."),
        0 => Math.Max(1, Environment.ProcessorCount / 2),
        _ => options.PngThreads
    };

    public string FfmpegExecutable
    {
        get
        {
            var executable = Resolve(options.FfmpegPath, "ffmpeg");
            if (Path.IsPathRooted(executable)) return executable;
            if (File.Exists(executable)) return Path.GetFullPath(executable);
            foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                var candidate = Path.Combine(directory.Trim('"'), executable + (OperatingSystem.IsWindows() && !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? ".exe" : ""));
                if (File.Exists(candidate)) return candidate;
            }
            return executable;
        }
    }
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
        var rate = FrameRate.Parse(GetString(video, "avg_frame_rate")) ?? FrameRate.Parse(GetString(video, "r_frame_rate"));
        var fps = rate?.Value ?? 0;
        if (width < 1 || height < 1 || width > 32768 || height > 32768 || !double.IsFinite(duration) || duration <= 0)
            throw new MediaException("Недопустимые размеры или длительность видео.");
        return new VideoInfo(width, height, duration, fps, GetString(video, "codec_name"),
            (long)GetNumber(format, "bit_rate"), streams.Any(s => GetString(s, "codec_type") == "audio"),
            (int)GetNumber(video, "index"), new FileInfo(path).Length, rate?.ToString());
    }

    public async Task<ImageProbe> ProbeImageAsync(string path, int maxSide, long maxPixels,
        bool requireSingleFrame, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        string json;
        try
        {
            json = await RunAsync(true,
                ["-v", "error", "-select_streams", "v:0", "-show_entries",
                 "stream=index,codec_name,width,height,pix_fmt", "-of", "json", path], null, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new MediaException("Чтение фото заняло слишком много времени.", 422); }
        using var document = JsonDocument.Parse(json);
        var streams = document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        if (streams.Length != 1) throw new MediaException("В файле нет поддерживаемого изображения.");
        var stream = streams[0];
        var codec = GetString(stream, "codec_name").ToLowerInvariant();
        var format = codec switch
        {
            "mjpeg" or "jpeg" => "jpeg",
            "png" => "png",
            "webp" => "webp",
            _ => throw new MediaException("Поддерживаются только статические JPG, PNG и WebP.")
        };
        var width = (int)GetNumber(stream, "width");
        var height = (int)GetNumber(stream, "height");
        if (width < 1 || height < 1 || width > maxSide || height > maxSide || (long)width * height > maxPixels)
            throw new MediaException($"Фото превышает безопасный лимит: до {maxSide} пикселей по стороне и {maxPixels:N0} пикселей всего.");
        if (requireSingleFrame)
        {
            var countJson = await RunAsync(true,
                ["-v", "error", "-select_streams", "v:0", "-count_frames", "-show_entries",
                 "stream=nb_read_frames", "-of", "json", path], null, timeout.Token);
            using var countDocument = JsonDocument.Parse(countJson);
            var counted = countDocument.RootElement.GetProperty("streams").EnumerateArray().FirstOrDefault();
            if (counted.ValueKind == JsonValueKind.Undefined || GetNumber(counted, "nb_read_frames") != 1)
                throw new MediaException("Анимированные изображения не поддерживаются. Выберите статический JPG, PNG или WebP.");
        }
        return new(width, height, format, GetString(stream, "pix_fmt"), (int)GetNumber(stream, "index"));
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
