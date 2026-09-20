using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using VidCropper.Backend;

static class InterpolationChecks
{
    public static async Task RunAsync(MediaTools tools, TestEnvironment env, TestLifetime lifetime,
        Dictionary<string, byte[]> downloads, string url, Action<bool, string> check)
    {
        void Reject(Action action, string label)
        {
            var rejected = false;
            try { action(); } catch (MediaException) { rejected = true; }
            check(rejected, label);
        }
        check(RifeCatalog.Models.Length == 37 && RifeCatalog.Package.RequiredFiles!.Length == 134, "full pinned RIFE manifest and compatible catalog");
        foreach (var model in RifeCatalog.Models)
            foreach (var multiplier in new[] { 2, 3 })
                check(RifeCatalog.Validate(new(model.Id, multiplier)) == model, $"catalog {model.Id} x{multiplier}");
        Reject(() => RifeCatalog.Validate(new("rife-v4.26", 4)), "reject interpolation x4");
        Reject(() => RifeCatalog.Validate(new("rife-v2.3", 2)), "legacy models not selectable");
        Reject(() => AiCatalog.Validate(new("rife-v4.26", 2)), "RIFE cannot be used as an upscaler");
        check(FrameRate.Parse("24000/1001")!.Value.Multiply(3).ToString() == "72000/1001", "rational FPS is exact");
        check(FrameRate.Parse("0/0") is null && FrameRate.Parse("-1/1") is null, "invalid frame rates rejected");
        var old = JsonSerializer.Deserialize<ExportRequest>("{\"MediaId\":\"00000000-0000-0000-0000-000000000000\",\"Fps\":30}");
        check(old is { Interpolation: null }, "old request remains valid without interpolation settings");

        byte[] Zip(bool missing = false, bool traversal = false)
        {
            using var bytes = new MemoryStream();
            using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
            {
                foreach (var name in RifeCatalog.Package.RequiredFiles!.Append("extra-model/readme.txt"))
                {
                    if (missing && name == "rife-v4.26/flownet.bin") continue;
                    using var writer = new StreamWriter(zip.CreateEntry(RifeCatalog.Package.ArchiveRoot + name).Open());
                    writer.Write("fixture " + name);
                }
                if (traversal) { using var writer = new StreamWriter(zip.CreateEntry("../outside.txt").Open()); writer.Write("invalid"); }
            }
            return bytes.ToArray();
        }
        AiPackage Release(string version, byte[] bytes, string? endpoint = null)
        {
            downloads[version] = bytes;
            return RifeCatalog.Package with { Version = version, Url = url + "/" + (endpoint ?? version), Bytes = bytes.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) };
        }
        var releases = new List<AiPackage> { Release("rife-fixture", Zip()) };
        var runner = new FakeRifeRunner(tools);
        var upscaler = new TestRunner(tools);
        var gate = new ProcessingGate();
        var packages = new AiPackages(new(releases), env, gate, upscaler, lifetime, NullLogger<AiPackages>.Instance, runner);
        async Task<AiOperation> Finish(AiOperation op)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (op.Status is "queued" or "running") { await Task.Delay(10, deadline.Token); op = packages.Get(op.Id); }
            while (gate.Busy) await Task.Delay(1, deadline.Token);
            return op;
        }
        var workspace = new TestWorkspace(env);
        using var store = new MediaStore(NullLogger<MediaStore>.Instance);
        try
        {
            check((await Finish(packages.Start("rife-v4.26", "install"))).Status == "completed" && runner.Checks == 0, "RIFE install never runs GPU");
            var installed = packages.ResolveInterpolation(new("rife-v4.26", 2));
            check(File.Exists(Path.Combine(installed.Directory, "rife/contextnet.bin")) &&
                File.Exists(Path.Combine(installed.Directory, "extra-model/readme.txt")), "full archive including legacy and extra files preserved");
            check((await Finish(packages.Start("rife-v3.9", "check"))).Status == "completed" && runner.Checks == 1, "explicit check dispatches to RIFE test double");
            await File.WriteAllTextAsync(Path.Combine(installed.Directory, "rife-v4.26/flownet.bin"), "corruption");
            var corrupt = false;
            try { await packages.VerifyAsync(installed, default); } catch (MediaException) { corrupt = true; }
            check(corrupt, "RIFE file corruption detected");
            check((await Finish(packages.Start("rife-v4.26", "install"))).Status == "completed" && runner.Checks == 1, "repair does not invoke GPU");
            foreach (var (version, bytes) in new[] { ("rife-missing", Zip(missing: true)), ("rife-traversal", Zip(traversal: true)) })
            {
                releases.Add(Release(version, bytes));
                check((await Finish(packages.Start("rife-v4.26", "install"))).Status == "failed", version + " refused");
                check(packages.ResolveInterpolation(new("rife-v4.26", 2)).Package.Version == "rife-fixture", "failed RIFE update preserves active");
                releases.RemoveAt(releases.Count - 1);
            }
            releases.Add(Release("rife-second", Zip()));
            check((await Finish(packages.Start("rife-v4.26", "install"))).Status == "completed", "RIFE update");
            check((await Finish(packages.Start("rife-v4.26", "rollback"))).Status == "completed" && runner.Checks == 1, "RIFE rollback without GPU");
            releases.RemoveAt(releases.Count - 1);
            releases.Add(Release("rife-cancel", Zip(), "slow"));
            var cancel = packages.Start("rife-v4.26", "install");
            Reject(() => { using var owner = gate.Acquire(); }, "RIFE install owns shared gate");
            check((await packages.CancelAsync(cancel.Id)).Status == "cancelled" && !gate.Busy, "RIFE download cancellation");
            releases.RemoveAt(releases.Count - 1);

            // Reuse the already installed fake SPAN package from the preceding tests.
            var spanDir = Path.Combine(env.ContentRootPath, "tools/ai/span/v1");
            var receipt = JsonSerializer.Deserialize<AiReceipt>(await File.ReadAllTextAsync(Path.Combine(spanDir, "receipt.json")))!;
            releases.Add(new("span", "v1", "https://example.invalid", 0, receipt.ArchiveSha256, "", "fake.exe", "models", "", "test", [AiCatalog.Model("nomos-weak")]));
            var pipeline = new AiPipeline(tools, upscaler, packages, workspace, NullLogger<AiPipeline>.Instance, runner);
            var path = Path.Combine(store.Root, "fractional.mp4");
            await tools.RunAsync(false, ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=64x48:rate=24000/1001",
                "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000", "-t", "1", "-c:v", "libx264", "-c:a", "aac", path], null, default);
            var source = store.Add(Guid.NewGuid(), path, "fractional.mp4", await tools.ProbeAsync(path, default));
            check(source.Info.FrameRate == "24000/1001", "probe preserves exact rational FPS");
            var request = new ExportRequest(source.Id, new(1, 1, 61, 45), 50, 25, true, 64, 48, .125, .65,
                Interpolation: new("rife-v4.26", 2));
            var result = Path.Combine(store.Root, "rife.mp4");
            foreach (var combined in new[] { false, true })
            foreach (var multiplier in new[] { 2, 3 })
            {
                var req = request with { Interpolation = new("rife-v4.26", multiplier), Upscale = combined ? new("nomos-weak", 3) : null };
                var size = ExportSettings.Validate(req, source.Info);
                var before = Path.Combine(store.Root, "before.mp4");
                var batches = runner.Batches;
                var stages = new List<string>();
                await pipeline.RunAsync(req, source, new(.125, .65), size, 16, combined ? packages.Resolve(req.Upscale!) : null,
                    result, p => stages.Add(p.Id), default, before, installed);
                var info = await tools.ProbeAsync(result, default);
                var baseline = await tools.ProbeAsync(before, default);
                check(runner.Batches == batches + 1 && runner.OutputCount == runner.InputCount * multiplier && runner.ContinuousNames,
                    $"single RIFE sequence, ordered count x{multiplier}, combined={combined}");
                check(info.FrameRate == FrameRate.Source(source.Info).Multiply(multiplier).ToString() &&
                    Math.Abs(info.Duration - .525) <= 1 / info.Fps + .001 && info.HasAudio, "fractional FPS, trimmed duration and audio preserved");
                var frameJson = await tools.RunAsync(true, ["-v", "error", "-count_frames", "-select_streams", "v:0", "-show_entries", "stream=nb_read_frames", "-of", "json", result], null, default);
                using var frameDocument = JsonDocument.Parse(frameJson);
                var encodedFrames = long.Parse(frameDocument.RootElement.GetProperty("streams")[0].GetProperty("nb_read_frames").GetString()!);
                check(Math.Abs(encodedFrames - .525 * info.Fps) <= 1, "encoded frame count matches trimmed timeline");
                check(baseline.FrameRate == source.Info.FrameRate && Math.Abs(baseline.Duration - info.Duration) < 1 / baseline.Fps + .001,
                    "preview before/after share timeline at different frame rates");
                check(runner.InputSize == size && info.Width == size.Width && info.Height == size.Height, "RIFE receives final resolution after crop/resize/upscale");
                check(stages.Contains("scenes") && stages.Contains("interpolate") && stages.Contains("upscale") == combined && !Directory.Exists(workspace.Root),
                    "combined stages and scratch cleanup");
            }
            // Timestamp-spaced VFR frames must use the source average rate, not request.Fps.
            var vfr = Path.Combine(store.Root, "vfr.mp4");
            await tools.RunAsync(false, ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=64x48:rate=24:duration=1",
                "-vf", "select='if(lt(n,12),1,not(mod(n,3)))'", "-fps_mode", "vfr", "-c:v", "libx264", vfr], null, default);
            var variable = store.Add(Guid.NewGuid(), vfr, "vfr.mp4", await tools.ProbeAsync(vfr, default));
            var vfrRequest = request with { MediaId = variable.Id, StartSeconds = 0, EndSeconds = variable.Info.Duration };
            await pipeline.RunAsync(vfrRequest, variable, new(0, variable.Info.Duration), (30, 22), 16, null, result, _ => { }, default,
                interpolationInstallation: installed);
            var vfrResult = await tools.ProbeAsync(result, default);
            check(vfrResult.FrameRate == FrameRate.Source(variable.Info).Multiply(2).ToString() &&
                Math.Abs(vfrResult.Duration - variable.Info.Duration) <= 1 / vfrResult.Fps + .001, "VFR normalized from timestamps with duration preserved");

            var shortRequest = request with { StartSeconds = .2, EndSeconds = .21, Audio = false };
            await pipeline.RunAsync(shortRequest, source, new(.2, .21), (30, 22), 16, null, result, _ => { }, default, interpolationInstallation: installed);
            var shortInfo = await tools.ProbeAsync(result, default);
            check(runner.InputCount == 1 && shortInfo.Duration > 0 && !shortInfo.HasAudio, "sub-frame trimmed interval produces a valid silent result");

            foreach (var stage in new[] { "extract", "scenes", "resize", "interpolate", "encode", "compare" })
            {
                using var stop = new CancellationTokenSource();
                var stopped = false;
                try
                {
                    await pipeline.RunAsync(request, source, new(.125, .65), (30, 22), 16, null, result,
                        p => { if (p.Id == stage) stop.Cancel(); }, stop.Token, Path.Combine(store.Root, "before.mp4"), installed);
                }
                catch (OperationCanceledException) { stopped = true; }
                check(stopped && !Directory.Exists(workspace.Root), "RIFE cancellation at " + stage);
            }
            using (var exports = new ExportService(store, tools, NullLogger<ExportService>.Instance, lifetime, gate, packages, pipeline, new(env)))
            {
                runner.Hold = true;
                var job = exports.Start(request);
                await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                check((await exports.CancelAsync(job.Id)).Status == "cancelled" && !gate.Busy, "cancel active RIFE releases gate");
                runner.Hold = false;
                foreach (var bad in new[] { "missing", "corrupt" })
                {
                    runner.BadOutput = bad;
                    job = exports.Start(request);
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    while (exports.Get(job.Id).Status is "queued" or "running" or "finalizing") await Task.Delay(10, deadline.Token);
                    check(exports.Get(job.Id).Status == "failed" && !Directory.Exists(workspace.Root) &&
                        !File.Exists(Path.Combine(store.Root, $"{job.Id:N}.mp4")), "RIFE " + bad + " output rejected and cleaned");
                    await exports.DeleteAsync(job.Id);
                }
                runner.BadOutput = null;
                var archived = Directory.GetFiles(Path.Combine(env.ContentRootPath, "output")).Length;
                job = exports.Start(request, .2);
                using var previewDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                while (exports.Get(job.Id).Status is "queued" or "running" or "finalizing") await Task.Delay(10, previewDeadline.Token);
                var preview = exports.Get(job.Id);
                check(preview.Status == "completed" && preview.Preview && preview.Result is { HasAudio: false } &&
                    Directory.GetFiles(Path.Combine(env.ContentRootPath, "output")).Length == archived, "RIFE-only preview succeeds without publishing an archive");
                await exports.DeleteAsync(job.Id);
            }
            workspace.FreeBytes = 0;
            var full = false;
            try { await pipeline.RunAsync(request, source, new(.125, .65), (30, 22), 16, null, result, _ => { }, default, interpolationInstallation: installed); }
            catch (MediaException e) { full = e.Status == 507; }
            check(full && !Directory.Exists(workspace.Root), "RIFE insufficient disk fails before extraction");
            workspace.FreeBytes = long.MaxValue;
            await CheckSceneCutsAsync(tools, store.Root, check);
            // This exercises only the explicit one-frame copy path; it cannot start an executable.
            var single = new FrameSequence(Path.Combine(store.Root, "single"), "%08d.png", 1, 1, 8, 8, 24);
            var multiple = single with { Directory = Path.Combine(store.Root, "single-out"), Count = 3, Fps = 72 };
            Directory.CreateDirectory(single.Directory); Directory.CreateDirectory(multiple.Directory);
            await tools.RunAsync(false, ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=red:size=8x8", "-frames:v", "1", single.FilePath(0)], null, default);
            await new RifeRunner(tools).RunAsync(installed with { Directory = "NONEXISTENT-NO-EXECUTABLE" }, single, multiple, default);
            await multiple.ValidateAsync(default);
            check(File.ReadAllBytes(single.FilePath(0)).SequenceEqual(File.ReadAllBytes(multiple.FilePath(2))), "single frame duplicates without launching RIFE");
        }
        finally { await packages.StopAsync(default); }
    }

    private static async Task CheckSceneCutsAsync(MediaTools tools, string root, Action<bool, string> check)
    {
        var input = new FrameSequence(Path.Combine(root, "scenes"), "%08d.png", 1, 6, 32, 32, 6);
        var output = input with { Directory = Path.Combine(root, "scenes-out"), Count = 18, Fps = 18 };
        Directory.CreateDirectory(input.Directory); Directory.CreateDirectory(output.Directory);
        foreach (var (color, start) in new[] { ("black", 1), ("white", 4) })
            await tools.RunAsync(false, ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", $"color={color}:size=32x32:rate=6",
                "-frames:v", "3", "-start_number", start.ToString(), Path.Combine(input.Directory, input.Pattern)], null, default);
        var cuts = await new SceneCuts(tools).DetectAsync(input, _ => { }, default);
        check(cuts.SetEquals([3L]), "scdet finds exact zero-based cut boundary");
        for (long i = 0; i < output.Count; i++) File.Copy(input.FilePath(3), output.FilePath(i));
        SceneCuts.Restore(input, output, 3, cuts, default);
        check(File.ReadAllBytes(output.FilePath(7)).SequenceEqual(File.ReadAllBytes(input.FilePath(2))) &&
            File.ReadAllBytes(output.FilePath(8)).SequenceEqual(File.ReadAllBytes(input.FilePath(2))) &&
            File.ReadAllBytes(output.FilePath(9)).SequenceEqual(File.ReadAllBytes(input.FilePath(3))), "cut repair replaces only synthetic frames, preserves new scene");
    }
}

sealed class FakeRifeRunner(MediaTools tools) : RifeRunner(tools)
{
    public int Checks, Batches;
    public long InputCount, OutputCount;
    public (int, int) InputSize;
    public bool ContinuousNames, Hold;
    public string? BadOutput;
    public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override Task CheckAsync(AiInstallation installation, CancellationToken ct) { ct.ThrowIfCancellationRequested(); Checks++; return Task.CompletedTask; }
    public override async Task RunAsync(AiInstallation installation, FrameSequence input, FrameSequence output, CancellationToken ct, Action<string>? completed = null)
    {
        ct.ThrowIfCancellationRequested();
        Batches++; InputCount = input.Count; OutputCount = output.Count; InputSize = (input.Width, input.Height);
        ContinuousNames = Directory.GetFiles(input.Directory).Order().Select(Path.GetFileName).SequenceEqual(Enumerable.Range(1, (int)input.Count).Select(i => $"{i:D8}.png"));
        if (Hold) { Started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }
        if (BadOutput == "missing") return;
        for (long i = 0; i < output.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            File.Copy(input.FilePath(i / (output.Count / input.Count)), output.FilePath(i));
            completed?.Invoke(output.Name(i));
        }
        if (BadOutput == "corrupt") await File.WriteAllBytesAsync(output.FilePath(0), [1, 2, 3], ct);
    }
}
