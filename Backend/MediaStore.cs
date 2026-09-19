namespace VidCropper.Backend;

public sealed class MediaStore(ILogger<MediaStore> logger) : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, Source> sources = [];
    public string Root { get; } = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "VidCropper", Guid.NewGuid().ToString("N"))).FullName;

    public sealed class Source(Guid id, string path, string name, VideoInfo info)
    {
        public Guid Id { get; } = id;
        public string Path { get; } = path;
        public string Name { get; } = name;
        public VideoInfo Info { get; } = info;
        public int Readers { get; set; }
        public bool DeleteRequested { get; set; }
    }

    public sealed class Lease(Source source, Action release) : IDisposable
    {
        private Action? onRelease = release;
        public Source Source { get; } = source;
        public void Dispose() => Interlocked.Exchange(ref onRelease, null)?.Invoke();
    }

    public Source Add(Guid id, string path, string name, VideoInfo info)
    {
        var source = new Source(id, path, name, info);
        lock (gate) sources.Add(id, source);
        return source;
    }

    public Lease Acquire(Guid id)
    {
        lock (gate)
        {
            if (!sources.TryGetValue(id, out var source) || source.DeleteRequested)
                throw new MediaException("Исходник больше не доступен. Откройте видео заново.", 404);
            source.Readers++;
            return new Lease(source, () =>
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

    private void Remove(Source source) { TryDelete(source.Path); sources.Remove(source.Id); }

    public void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException exception) { logger.LogWarning(exception, "Не удалось удалить временный файл {Path}", path); }
        catch (UnauthorizedAccessException exception) { logger.LogWarning(exception, "Не удалось удалить временный файл {Path}", path); }
    }

    public void Dispose()
    {
        // This directory is a fresh, private GUID directory owned by this instance only.
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException exception) { logger.LogWarning(exception, "Не удалось очистить {Root}", Root); }
        catch (UnauthorizedAccessException exception) { logger.LogWarning(exception, "Не удалось очистить {Root}", Root); }
    }
}
