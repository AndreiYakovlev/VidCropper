using System.Buffers;

namespace VidCropper.Backend;

public sealed class PhotoStore(ILogger<PhotoStore> logger) : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, Source> sources = [];
    public string Root { get; } = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "VidCropperPhotos", Guid.NewGuid().ToString("N"))).FullName;

    public sealed class Source(Guid id, string path, string name, ImageInfo info)
    {
        public Guid Id { get; } = id;
        public string Path { get; } = path;
        public string Name { get; } = name;
        public ImageInfo Info { get; } = info;
        public int Readers { get; set; }
        public bool DeleteRequested { get; set; }
    }

    public sealed class Lease(Source source, Action release) : IDisposable
    {
        private Action? onRelease = release;
        public Source Source { get; } = source;
        public void Dispose() => Interlocked.Exchange(ref onRelease, null)?.Invoke();
    }

    public async Task<Source> ImportAsync(Guid id, Stream body, string name, long? contentLength,
        MediaOptions options, MediaTools tools, CancellationToken ct)
    {
        if (contentLength > options.MaxUploadBytes) throw new MediaException("Файл превышает лимит загрузки.", 413);
        name = Path.GetFileName(name);
        if (string.IsNullOrWhiteSpace(name)) name = "photo.png";
        if (name.Length > 200) name = name[..200];
        var raw = Path.Combine(Root, $"{id:N}.upload");
        var normalized = Path.Combine(Root, $"{id:N}.png");
        try
        {
            await using (var file = new FileStream(raw, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(65536);
                try
                {
                    long total = 0;
                    int read;
                    while ((read = await body.ReadAsync(buffer, ct)) > 0)
                    {
                        total += read;
                        if (total > options.MaxUploadBytes) throw new MediaException("Файл превышает лимит загрузки.", 413);
                        await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    }
                    if (total == 0) throw new MediaException("Файл пуст.");
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
            }
            var originalSize = new FileInfo(raw).Length;
            var probe = await tools.ProbeImageAsync(raw, options.MaxPhotoSide, options.MaxPhotoPixels, true, ct);
            await tools.RunAsync(false,
                ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", raw,
                 "-map", $"0:{probe.StreamIndex}", "-frames:v", "1", "-vf", "setsar=1",
                 "-pix_fmt", "rgba", "-map_metadata", "-1", "-c:v", "png", normalized], null, ct);
            var normalizedProbe = await tools.ProbeImageAsync(
                normalized, options.MaxPhotoSide, options.MaxPhotoPixels, true, ct);
            var hasAlpha = probe.PixelFormat.Contains('a', StringComparison.OrdinalIgnoreCase) ||
                probe.PixelFormat.Equals("pal8", StringComparison.OrdinalIgnoreCase);
            var source = new Source(id, normalized, name,
                new(normalizedProbe.Width, normalizedProbe.Height, probe.Format, originalSize, hasAlpha));
            lock (gate) sources.Add(id, source);
            return source;
        }
        catch
        {
            TryDelete(normalized);
            throw;
        }
        finally { TryDelete(raw); }
    }

    public Lease Acquire(Guid id)
    {
        lock (gate)
        {
            if (!sources.TryGetValue(id, out var source) || source.DeleteRequested)
                throw new MediaException("Фото больше не доступно. Откройте его заново.", 404);
            source.Readers++;
            return new(source, () =>
            {
                lock (gate)
                {
                    source.Readers--;
                    if (source.Readers == 0 && source.DeleteRequested) Remove(source);
                }
            });
        }
    }

    public void Delete(Guid id)
    {
        lock (gate)
        {
            if (!sources.TryGetValue(id, out var source)) return;
            source.DeleteRequested = true;
            if (source.Readers == 0) Remove(source);
        }
    }

    private void Remove(Source source)
    {
        TryDelete(source.Path);
        sources.Remove(source.Id);
    }

    public void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException exception) { logger.LogWarning(exception, "Не удалось удалить временный файл {Path}", path); }
        catch (UnauthorizedAccessException exception) { logger.LogWarning(exception, "Не удалось удалить временный файл {Path}", path); }
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException exception) { logger.LogWarning(exception, "Не удалось очистить {Root}", Root); }
        catch (UnauthorizedAccessException exception) { logger.LogWarning(exception, "Не удалось очистить {Root}", Root); }
    }
}
