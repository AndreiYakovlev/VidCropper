using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VidCropper.Backend;

var root = Path.Combine(Path.GetTempPath(), "VidCropper-AiChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var downloads = new Dictionary<string, byte[]>();
var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
var server = builder.Build();
server.MapGet("/{name}", async (string name, HttpContext context) =>
{
    if (name == "offline") { context.Response.StatusCode = 503; return; }
    if (name == "slow") { await Task.Delay(30000, context.RequestAborted); return; }
    await context.Response.Body.WriteAsync(downloads[name], context.RequestAborted);
});
await server.StartAsync();
var url = server.Urls.Single();
var lifetime = new TestLifetime();
var tools = new MediaTools(new(), NullLogger<MediaTools>.Instance);
var runner = new TestRunner(tools);
var releases = new List<AiPackage>();
var catalog = new AiCatalog(releases);
var gate = new ProcessingGate();
var env = new TestEnvironment { ContentRootPath = root };
var packages = new AiPackages(catalog, env, gate, runner, lifetime, NullLogger<AiPackages>.Instance);
int assertions = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); assertions++; Console.WriteLine("PASS " + name); }
var clock = new TestClock();
var timer = new ProcessingTimer(clock);
timer.Report("extract",0,100);
Check(timer.RemainingSeconds is null,"ETA needs measured progress");
clock.Advance(10); timer.Report("extract",25,100);
Check(timer.RemainingSeconds is null,"ETA excludes startup and first-frame warmup");
clock.Advance(5); timer.Report("extract",50,100);
Check(timer.RemainingSeconds==10 && timer.ElapsedSeconds==15,"ETA uses measured throughput");
clock.Advance(5);
Check(timer.RemainingSeconds==20,"ETA adapts when progress stalls");
timer.Report("future-stage",0,200);
Check(timer.RemainingSeconds is null && timer.StageElapsedSeconds==0 && timer.ElapsedSeconds==20,"new stage resets ETA but keeps total");
clock.Advance(2); timer.Report("future-stage",50,200);
clock.Advance(2); timer.Report("future-stage",100,200);
Check(timer.RemainingSeconds==4,"future stages share estimator");
timer.Stop(); clock.Advance(20);
Check(timer.ElapsedSeconds==24 && timer.RemainingSeconds is null,"terminal elapsed time stays frozen");
AiPackage Release(string version, byte[] bytes, string? endpoint = null)
{
    downloads[version] = bytes;
    return new("span", version, url + "/" + (endpoint ?? version), bytes.Length,
        Convert.ToHexString(SHA256.HashData(bytes)), "", "fake.exe", "models", "https://example.invalid/source", "test only", [AiCatalog.Model("nomos-weak")]);
}
async Task<AiOperation> Finish(AiOperation op)
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    while (op.Status is "queued" or "running") { await Task.Delay(10, deadline.Token); op = packages.Get(op.Id); }
    while (gate.Busy) await Task.Delay(1, deadline.Token);
    return op;
}
byte[] Zip(bool traversal = false, bool missing = false)
{
    using var stream = new MemoryStream();
    using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        foreach (var name in new[] { "fake.exe", "vcomp140.dll", "LICENSE", "models/4xNomos8k_span_otf_weak.param", "models/4xNomos8k_span_otf_weak.bin" })
        {
            if (missing && name.EndsWith(".bin")) continue;
            using var writer = new StreamWriter(zip.CreateEntry(traversal ? "../escape" : name).Open()); writer.Write("test fixture " + name);
            if (traversal) break;
        }
    return stream.ToArray();
}
try
{
    releases.Add(Release("v1", Zip()));
    Check((await Finish(packages.Start("nomos-weak", "install"))).Status == "completed", "initial install");
    Check(packages.Resolve(new("nomos-weak", 2)).Package.Version == "v1", "active v1");
    var checks = runner.Checks;
    Check((await Finish(packages.Start("nomos-weak", "install"))).Status == "completed" && runner.Checks == checks + 1, "reuse validates installed package");
    releases.Add(Release("v2", Zip()));
    runner.Fail = true;
    Check((await Finish(packages.Start("nomos-weak", "install"))).Status == "failed", "GPU failure refuses activation");
    Check(packages.Resolve(new("nomos-weak", 2)).Package.Version == "v1", "failed update preserves active version");
    runner.Fail = false;
    Check((await Finish(packages.Start("nomos-weak", "install"))).Status == "completed", "update to v2");
    Check(packages.Resolve(new("nomos-weak", 2)).Package.Version == "v2", "v2 activated");
    Check((await Finish(packages.Start("nomos-weak", "rollback"))).Status == "completed", "rollback");
    var restarted = new AiPackages(catalog, env, gate, runner, lifetime, NullLogger<AiPackages>.Instance);
    Check(restarted.Resolve(new("nomos-weak", 2)).Package.Version == "v1", "rollback persists across restart");
    Check(File.Exists(Path.Combine(root, "tools/ai/span/v2/fake.exe")), "previous package retained");

    var active = packages.Resolve(new("nomos-weak", 2));
    await File.WriteAllTextAsync(Path.Combine(active.Directory, "fake.exe"), "corrupted");
    bool corrupt = false;
    try { await packages.VerifyAsync(active, default); } catch (MediaException) { corrupt = true; }
    Check(corrupt, "file corruption detected");
    releases.RemoveAt(releases.Count - 1);
    Check((await Finish(packages.Start("nomos-weak", "install"))).Status == "completed", "explicit repair restores package");
    await packages.VerifyAsync(packages.Resolve(new("nomos-weak", 2)), default);

    foreach (var (name, bytes, endpoint, badHash) in new[] {
        ("bad-hash", Zip(), (string?)null, true), ("broken-zip", new byte[]{1,2,3}, (string?)null, false),
        ("missing", Zip(missing:true), (string?)null, false), ("traversal", Zip(traversal:true), (string?)null, false),
        ("offline", Zip(), "offline", false) })
    {
        var release = Release(name, bytes, endpoint); if (badHash) release = release with { Sha256 = new string('0', 64) };
        releases.Add(release);
        Check((await Finish(packages.Start("nomos-weak", "install"))).Status == "failed", name + " rejected");
        Check(packages.Resolve(new("nomos-weak", 2)).Package.Version == "v1", name + " preserves active");
        Check(!Directory.EnumerateDirectories(Path.Combine(root,"tools/ai"), ".install-*").Any(), name + " cleans staging");
        releases.RemoveAt(releases.Count - 1);
    }
    releases.Add(Release("cancel", Zip(), "slow"));
    var cancel = packages.Start("nomos-weak", "install");
    bool excluded = false;
    try { using var conflict = gate.Acquire(); } catch (MediaException e) { excluded = e.Status == 409; }
    Check(excluded, "installation excludes processing");
    Check((await packages.CancelAsync(cancel.Id)).Status == "cancelled", "cancel download");
    releases.RemoveAt(releases.Count - 1);
    Check(!gate.Busy, "cancel releases shared gate");

    var workspace = new TestWorkspace(env);
    var foreign = Path.Combine(root,"temp","ai","foreign-session","keep.txt");
    Directory.CreateDirectory(Path.GetDirectoryName(foreign)!);
    await File.WriteAllTextAsync(foreign,"preserve");
    using var store = new MediaStore(NullLogger<MediaStore>.Instance);
    var sourcePath = Path.Combine(store.Root,"source.mp4");
    await tools.RunAsync(false,["-hide_banner","-loglevel","error","-y","-f","lavfi","-i","testsrc2=size=64x48:rate=24","-t","1","-c:v","libx264",sourcePath],null,default);
    var info = await tools.ProbeAsync(sourcePath,default);
    var source = store.Add(Guid.NewGuid(),sourcePath,"source.mp4",info);
    var request = new ExportRequest(source.Id,new(1,1,61,45),10,24,false,64,48,Quality:"high",Upscale:new("nomos-weak",3));
    var pipeline = new AiPipeline(tools,runner,packages,workspace,NullLogger<AiPipeline>.Instance);
    var result = Path.Combine(store.Root,"result.mp4");
    long frames=0;
    await pipeline.RunAsync(request,source,new(0,1),(182,134),20,packages.Resolve(request.Upscale!),result,p=>frames=p.Done,default);
    var output = await tools.ProbeAsync(result,default);
    Check(frames==24 && runner.Batches==1 && runner.InputCount==24 && runner.ContinuousNames && output.Width==182 && output.Height==134 && output.Fps==24, "single AI invocation receives complete numbered sequence and preserves count, scale, FPS");
    Check(!Directory.Exists(workspace.Root), "pipeline scratch removed");
    Check(await File.ReadAllTextAsync(foreign)=="preserve","cleanup preserves other sessions");
    foreach (var stage in new[] { "extract", "upscale", "encode", "compare" })
    {
        using var cancelStage = new CancellationTokenSource();
        var cancelled = false;
        try
        {
            await pipeline.RunAsync(request,source,new(0,1),(182,134),20,packages.Resolve(request.Upscale!),result,
                p => { if(p.Id==stage && (stage!="extract" || p.Done>0))cancelStage.Cancel(); },cancelStage.Token,Path.Combine(store.Root,"before.mp4"));
        }
        catch(OperationCanceledException) { cancelled=true; }
        Check(cancelled && !Directory.Exists(workspace.Root), $"cancel and clean at {stage}");
    }
    workspace.FreeBytes = 0;
    bool diskRejected=false;
    try { await pipeline.RunAsync(request,source,new(0,1),(182,134),20,packages.Resolve(request.Upscale!),result,_=>{},default); }
    catch(MediaException e) { diskRejected=e.Status==507; }
    Check(diskRejected && !Directory.Exists(workspace.Root),"insufficient disk rejected before extraction");
    workspace.FreeBytes = long.MaxValue;
    diskRejected=false;
    try { await pipeline.RunAsync(request,source,new(0,1),(182,134),20,packages.Resolve(request.Upscale!),result,p=>{if(p.Id=="extract")workspace.FreeBytes=0;},default); }
    catch(MediaException e) { diskRejected=e.Status==507; }
    Check(diskRejected && !Directory.Exists(workspace.Root),"disk monitor stops processing and cleans files");
    workspace.FreeBytes = long.MaxValue;
    using (var exports = new ExportService(store,tools,NullLogger<ExportService>.Instance,lifetime,gate,packages,pipeline,new ExportArchive(env)))
    {
        runner.Hold = true; runner.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var job = exports.Start(request);
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(gate.Busy,"AI task owns gate during inference");
        Check((await exports.CancelAsync(job.Id)).Status=="cancelled","cancel during inference");
        Check(!Directory.Exists(workspace.Root) && !File.Exists(Path.Combine(store.Root,$"{job.Id:N}.mp4")),"cancel removes intermediate and output files");
        runner.Hold=false;runner.MissingFrames=true;
        job=exports.Start(request);
        while(exports.Get(job.Id).Status is "queued" or "running" or "finalizing")await Task.Delay(10);
        Check(exports.Get(job.Id).Status=="failed","missing AI output fails export");
        await exports.DeleteAsync(job.Id);
        Check(!gate.Busy,"failure releases processing gate");
        runner.MissingFrames=false;
        runner.Corrupt=true;
        job=exports.Start(request);
        while(exports.Get(job.Id).Status is "queued" or "running" or "finalizing")await Task.Delay(10);
        Check(exports.Get(job.Id).Status=="failed" && !Directory.Exists(workspace.Root),"corrupt output fails and cleans frames");
        runner.Corrupt=false;
        await exports.DeleteAsync(job.Id);
        job=exports.Start(request);
        while(exports.Get(job.Id).Status is "queued" or "running" or "finalizing")await Task.Delay(10);
        var completed=exports.Get(job.Id);
        Check(completed.Status=="completed","AI export archived successfully");
        var saved=Path.Combine(root,"output",completed.FileName);
        await exports.DeleteAsync(job.Id);
        Check(File.Exists(saved),"AI archive survives task deletion");
        var archiveCount=Directory.GetFiles(Path.Combine(root,"output")).Length;
        job=exports.Start(request,0);
        while(exports.Get(job.Id).Status is "queued" or "running" or "finalizing")await Task.Delay(10);
        Check(exports.Get(job.Id).Status=="completed" && Directory.GetFiles(Path.Combine(root,"output")).Length==archiveCount,"preview stays temporary");
        await exports.DeleteAsync(job.Id);
        await exports.StopAsync(default);
    }
    var png = Path.Combine(root,"frame.png");
    await tools.RunAsync(false,["-hide_banner","-loglevel","error","-y","-f","lavfi","-i","color=red:size=8x8","-frames:v","1",png],null,default);
    var data=await File.ReadAllBytesAsync(png);
    using var concatenated=new MemoryStream(data.Concat(data).ToArray());
    Check(await PngFrames.CopyFrameAsync(concatenated,Stream.Null,default)==(8,8) && await PngFrames.CopyFrameAsync(concatenated,Stream.Null,default)==(8,8) && await PngFrames.CopyFrameAsync(concatenated,Stream.Null,default) is null,"PNG framing reads concatenated stream");
    bool truncated=false;
    try { await PngFrames.CopyFrameAsync(new MemoryStream(data[..^2]),Stream.Null,default); } catch(EndOfStreamException) { truncated=true; }
    Check(truncated,"truncated PNG rejected");
    await InterpolationChecks.RunAsync(tools, env, lifetime, downloads, url, Check);
    Console.WriteLine($"PASS: {assertions} assertions. AI runners are test doubles, not a GPU quality check.");
}
finally
{
    await packages.StopAsync(default); await server.StopAsync(); await server.DisposeAsync();
    Directory.Delete(root,true);
}

sealed class TestRunner : AiRunner
{
    private readonly MediaTools tools;
    public TestRunner(MediaTools tools) : base(tools) { this.tools = tools; }
    public bool Fail; public bool Hold; public bool MissingFrames; public int Checks; public int Batches; public int InputCount; public bool ContinuousNames; public bool Corrupt;
    public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override Task CheckAsync(AiInstallation installation,CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); Checks++; if(Fail)throw new MediaException("GPU fixture failure");return Task.CompletedTask; }
    public override async Task RunAsync(AiInstallation installation,string input,string output,int scale,CancellationToken ct, Action<string>? completed = null)
    {
        Batches++;
        var files = Directory.GetFiles(input).Order().ToArray(); InputCount=files.Length;
        ContinuousNames = files.Select(Path.GetFileName).SequenceEqual(Enumerable.Range(1,files.Length).Select(i=>$"{i:D8}.png"));
        Started.TrySetResult();
        if(Hold)await Task.Delay(Timeout.Infinite,ct);
        if(MissingFrames)return;
        foreach(var file in files)
        {
            await tools.RunAsync(false,["-hide_banner","-loglevel","error","-y","-i",file,"-vf",$"scale=iw*{scale}:ih*{scale}:flags=neighbor","-frames:v","1",Path.Combine(output,Path.GetFileName(file))],null,ct);
            completed?.Invoke(Path.GetFileName(file));
        }
        if(Corrupt) await File.WriteAllBytesAsync(Path.Combine(output,Path.GetFileName(files[0])), [1,2,3],ct);
    }
}
sealed class TestLifetime : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted=>default;
    public CancellationToken ApplicationStopping=>default;
    public CancellationToken ApplicationStopped=>default;
    public void StopApplication() { }
}
sealed class TestEnvironment : IWebHostEnvironment
{
    public string ApplicationName {get;set;}="AiChecks";
    public string EnvironmentName {get;set;}="Test";
    public string ContentRootPath {get;set;}="";
    public IFileProvider ContentRootFileProvider {get;set;}=new NullFileProvider();
    public string WebRootPath {get;set;}="";
    public IFileProvider WebRootFileProvider {get;set;}=new NullFileProvider();
}

sealed class TestWorkspace(IWebHostEnvironment environment) : FrameWorkspace(environment)
{
    public long FreeBytes = long.MaxValue;
    public override long AvailableBytes(string path) => FreeBytes;
}

sealed class TestClock : TimeProvider
{
    private long timestamp;
    public override long TimestampFrequency => 1000;
    public override long GetTimestamp() => timestamp;
    public void Advance(int seconds) => timestamp += seconds * 1000;
}
