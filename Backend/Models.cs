namespace VidCropper.Backend;

public sealed class MediaOptions
{
    public string FfmpegPath { get; set; } = "";
    public string FfprobePath { get; set; } = "";
    public long MaxUploadBytes { get; set; } = 10L * 1024 * 1024 * 1024;
}

public sealed class MediaException(string message, int status = 400) : Exception(message)
{
    public int Status { get; } = status;
}

public sealed record VideoInfo(int Width, int Height, double Duration, double Fps,
    string Codec, long BitRate, bool HasAudio, int StreamIndex, long Size);
public sealed record CropRegion(int X, int Y, int Width, int Height);
public sealed record ExportRequest(Guid MediaId, CropRegion? Crop, int Scale, int Fps,
    bool Audio, int SourceWidth, int SourceHeight, double? StartSeconds = null, double? EndSeconds = null,
    string? Quality = null, UpscaleRequest? Upscale = null);
public sealed record AiPreviewRequest(ExportRequest Export, double Position);
public sealed record TrimRange(double Start, double End)
{
    public double Duration => End - Start;
}
public sealed record ExportSnapshot(Guid Id, string Status, double Progress, string? Error,
    VideoInfo? Result, string FileName, string? Stage = null, long FramesDone = 0, long FramesTotal = 0, bool Preview = false,
    string? StageId = null, double? StageProgress = null, bool FramesTotalEstimated = false,
    double ElapsedSeconds = 0, double StageElapsedSeconds = 0, double? RemainingSeconds = null);

public static class ExportSettings
{
    public static int ResolveCrf(string? quality) => quality switch
    {
        null or "maximum" => 16,
        "high" => 20,
        "balanced" => 23,
        "compact" => 28,
        _ => throw new MediaException("Недопустимое качество видео.")
    };

    public static TrimRange ValidateTrim(ExportRequest request, VideoInfo source)
    {
        var start = request.StartSeconds ?? 0;
        var end = request.EndSeconds ?? source.Duration;
        var minimum = Math.Min(0.01, source.Duration);
        if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end > source.Duration ||
            start >= end || end - start < minimum - 1e-9)
            throw new MediaException("Недопустимый отрезок видео. Начало должно быть раньше конца, минимальная длина — 0,01 секунды (либо весь файл, если он короче).");
        return new TrimRange(start, end);
    }

    public static (int Width, int Height) Validate(ExportRequest request, VideoInfo source)
    {
        if (request.SourceWidth != source.Width || request.SourceHeight != source.Height)
            throw new MediaException("Размеры предпросмотра отличаются от исходника. Экспорт остановлен, чтобы не сместить рамку.");
        var c = request.Crop;
        if (c is null || c.X < 0 || c.Y < 0 || c.Width < 1 || c.Height < 1 ||
            (long)c.X + c.Width > source.Width || (long)c.Y + c.Height > source.Height)
            throw new MediaException("Область кадрирования выходит за границы видео.");
        if (request.Scale is < 1 or > 100 || request.Fps is not (24 or 25 or 30 or 50 or 60))
            throw new MediaException("Недопустимый масштаб или FPS.");
        if (request.Upscale is not null)
        {
            AiCatalog.Validate(request.Upscale);
            if ((long)c.Width * 4 > 32768 || (long)c.Height * 4 > 32768)
                throw new MediaException("Область слишком велика для AI: родной результат не должен превышать 32768 пикселей по стороне.");
            return (c.Width * request.Upscale.Scale / 2 * 2, c.Height * request.Upscale.Scale / 2 * 2);
        }
        return (Math.Max(2, (int)((long)c.Width * request.Scale / 200) * 2),
            Math.Max(2, (int)((long)c.Height * request.Scale / 200) * 2));
    }
}
