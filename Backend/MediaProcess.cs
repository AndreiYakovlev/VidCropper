using System.Diagnostics;
using System.Text;

namespace VidCropper.Backend;

// Owns pipe draining and cancellation for long-lived decoder/encoder and AI child processes.
public sealed class MediaProcess : IAsyncDisposable
{
    private readonly Process process;
    private readonly CancellationTokenRegistration registration;
    private readonly Task errors;
    private readonly Task? output;
    private readonly StringBuilder log = new();
    public Stream Input => process.StandardInput.BaseStream;
    public Stream Output => process.StandardOutput.BaseStream;

    public MediaProcess(string executable, IEnumerable<string> args, CancellationToken ct, bool pipeInput = false,
        bool pipeOutput = false, Action<string>? onLine = null)
    {
        ct.ThrowIfCancellationRequested();
        process = new Process { StartInfo = new(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = pipeInput, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        try { process.Start(); }
        catch { process.Dispose(); throw; }
        registration = ct.Register(Kill);
        errors = DrainAsync(process.StandardError, onLine);
        if (!pipeOutput) output = DrainAsync(process.StandardOutput, onLine);
    }

    private async Task DrainAsync(StreamReader reader, Action<string>? onLine)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            lock (log) { if (log.Length < 32768) log.AppendLine(line); }
            onLine?.Invoke(line);
        }
    }

    public async Task CompleteAsync(CancellationToken ct)
    {
        await process.WaitForExitAsync(ct);
        await errors;
        if (output is not null) await output;
        ct.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
        {
            string message;
            lock (log) message = log.ToString();
            throw new MediaException($"{Path.GetFileName(process.StartInfo.FileName)}: обработка завершилась с ошибкой ({process.ExitCode}). {message}", 422);
        }
    }
    private void Kill()
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
    public async ValueTask DisposeAsync()
    {
        Kill();
        await process.WaitForExitAsync();
        await errors;
        if (output is not null) await output;
        registration.Dispose(); process.Dispose();
    }
}
