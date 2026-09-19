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
    bool Audio, int SourceWidth, int SourceHeight);
public sealed record ExportSnapshot(Guid Id, string Status, double Progress, string? Error,
    VideoInfo? Result, string FileName);

public static class ExportSettings
{
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
        return (Math.Max(2, (int)((long)c.Width * request.Scale / 200) * 2),
            Math.Max(2, (int)((long)c.Height * request.Scale / 200) * 2));
    }
}
