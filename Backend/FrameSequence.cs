namespace VidCropper.Backend;

public sealed record FrameSequence(string Directory, string Pattern, int StartNumber, long Count, int Width, int Height, int Fps)
{
    public string Name(long index) => (StartNumber + index).ToString("D8", System.Globalization.CultureInfo.InvariantCulture) + ".png";
    public string FilePath(long index) => Path.Combine(Directory, Name(index));

    public async Task ValidateAsync(CancellationToken ct)
    {
        if (Count < 1 || System.IO.Directory.EnumerateFileSystemEntries(Directory).LongCount() != Count)
            throw new MediaException("AI-модель вернула неполный набор кадров.");
        for (long i = 0; i < Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(FilePath(i))) throw new MediaException("Нарушена нумерация кадров AI.");
            await using var file = File.OpenRead(FilePath(i));
            if (await PngFrames.CopyFrameAsync(file, Stream.Null, ct) != (Width, Height) || file.Position != file.Length)
                throw new MediaException("AI-модель вернула повреждённый кадр или неверное разрешение.");
        }
    }
}

public sealed record FrameProgress(string Id, string Label, long Done, long Total, bool Estimated = false)
{
    public double Percent => Math.Clamp(Done * 100d / Math.Max(1, Total), 0, 100);
}

public class FrameWorkspace(IWebHostEnvironment environment)
{
    public const long ReserveBytes = 512L * 1024 * 1024;
    public string Root { get; } = Path.Combine(environment.ContentRootPath, "temp", "ai", Guid.NewGuid().ToString("N"));
    public virtual long AvailableBytes(string path) => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;
    public void CheckSpace(params string[] paths)
    {
        foreach (var path in paths)
            if (AvailableBytes(path) < ReserveBytes)
                throw new MediaException("Недостаточно места для обработки: на диске должно оставаться не менее 512 МиБ. Освободите место для временных PNG.", 507);
    }
}
