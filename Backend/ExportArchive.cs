namespace VidCropper.Backend;

public sealed class ExportArchive(IWebHostEnvironment environment)
{
    public string Root { get; } = Path.Combine(environment.ContentRootPath, "output");

    public async Task<string> SaveAsync(string source, string name, CancellationToken ct)
    {
        Directory.CreateDirectory(Root);
        var staging = Path.Combine(Root, $".{Guid.NewGuid():N}.part");
        try
        {
            await using (var input = File.OpenRead(source))
            await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                await input.CopyToAsync(output, ct);
            ct.ThrowIfCancellationRequested();
            var stem = string.Concat(Path.GetFileNameWithoutExtension(name)
                .Select(c => Path.GetInvalidFileNameChars().Contains(c) || char.IsControl(c) ? '_' : c)).Trim().TrimEnd('.');
            if (string.IsNullOrWhiteSpace(stem)) stem = "video";
            if (stem.Length > 150) stem = stem[..150];
            if (System.Text.RegularExpressions.Regex.IsMatch(stem.Split('.')[0].TrimEnd(),
                @"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                stem = "_" + stem;
            for (var suffix = 0; ; suffix++)
            {
                var destination = Path.Combine(Root, stem + (suffix == 0 ? "" : $" ({suffix})") + ".mp4");
                try { File.Move(staging, destination, overwrite: false); return destination; }
                catch (IOException) when (File.Exists(destination) || Directory.Exists(destination)) { }
            }
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }
}
