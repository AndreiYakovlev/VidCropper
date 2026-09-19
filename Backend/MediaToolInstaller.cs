using System.IO.Compression;
using System.Security.Cryptography;

namespace VidCropper.Backend;

public static class MediaToolInstaller
{
    private const string DownloadUrl = "https://github.com/GyanD/codexffmpeg/releases/download/7.0.2/ffmpeg-7.0.2-essentials_build.zip";
    private const string ExpectedHash = "D5308D30872B2739CF53169DF61FABA8639D39A19B20B91E611C177EF676F64C";

    public static async Task EnsureAsync(string applicationDirectory, CancellationToken cancellationToken)
    {
        var target = Path.Combine(applicationDirectory, "tools");
        if (File.Exists(Path.Combine(target, "ffmpeg.exe")) && File.Exists(Path.Combine(target, "ffprobe.exe"))) return;
        var scratch = Path.Combine(Path.GetTempPath(), "VidCropper-setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            Console.WriteLine("Первый запуск: загружаем FFmpeg 7.0.2 с GitHub сборщика Gyan.dev. Видео никуда не отправляются.");
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            using var response = await client.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var archive = Path.Combine(scratch, "ffmpeg.zip");
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = File.Create(archive))
                await input.CopyToAsync(output, cancellationToken);
            await using (var stream = File.OpenRead(archive))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
                if (hash != ExpectedHash) throw new InvalidDataException("Контрольная сумма FFmpeg не совпала. Установка остановлена.");
            }
            Console.WriteLine("Контрольная сумма проверена. Распаковываем инструменты…");
            var extracted = Path.Combine(scratch, "extracted");
            ZipFile.ExtractToDirectory(archive, extracted);
            cancellationToken.ThrowIfCancellationRequested();
            var source = Path.Combine(extracted, "ffmpeg-7.0.2-essentials_build");
            Directory.CreateDirectory(target);
            foreach (var name in new[] { "LICENSE", "README.txt", "bin/ffmpeg.exe", "bin/ffprobe.exe" })
            {
                // Move each complete file atomically; an interrupted copy cannot look installed.
                var temporary = Path.Combine(target, Path.GetFileName(name) + "." + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    File.Copy(Path.Combine(source, name), temporary);
                    File.Move(temporary, Path.Combine(target, Path.GetFileName(name)), overwrite: true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            Console.WriteLine("FFmpeg готов. Инструменты сохранены только в папке tools рядом с приложением.");
        }
        finally
        {
            // Only the freshly created private directory is removed.
            try { Directory.Delete(scratch, recursive: true); }
            catch (IOException exception) { Console.Error.WriteLine($"Не удалось очистить {scratch}: {exception.Message}"); }
            catch (UnauthorizedAccessException exception) { Console.Error.WriteLine($"Не удалось очистить {scratch}: {exception.Message}"); }
        }
    }
}
