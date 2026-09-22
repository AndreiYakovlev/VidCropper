using System.Diagnostics;
using VidCropper.Backend;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot"))
        ? AppContext.BaseDirectory : Directory.GetCurrentDirectory()
});
var portValue = builder.Configuration["port"] ?? builder.Configuration["LocalServer:Port"] ?? "5180";
if (!int.TryParse(portValue, out var port) || port is < 0 or > 65535)
{
    Console.Error.WriteLine("Порт должен быть от 1 до 65535; 0 — автоматически выбрать свободный.");
    return 1;
}
var mediaOptions = builder.Configuration.GetSection("Media").Get<MediaOptions>() ?? new();
if (mediaOptions.MaxUploadBytes < 1) { Console.Error.WriteLine("Media:MaxUploadBytes должен быть положительным."); return 1; }
if (mediaOptions.PngThreads < 0) { Console.Error.WriteLine("Media:PngThreads должен быть 0 (автоматически) или положительным числом потоков."); return 1; }
if (mediaOptions.MaxPhotoSide < 1 || mediaOptions.MaxPhotoPixels < 1)
{ Console.Error.WriteLine("Media:MaxPhotoSide и Media:MaxPhotoPixels должны быть положительными."); return 1; }
if (builder.Configuration.GetValue<bool>("InstallTools"))
{
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
    ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    Console.CancelKeyPress += cancel;
    try { await MediaToolInstaller.EnsureAsync(AppContext.BaseDirectory, cancellation.Token); }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"Не удалось подготовить FFmpeg: {exception.Message}");
        Console.Error.WriteLine("Проверьте интернет или вручную поместите ffmpeg.exe и ffprobe.exe в папку tools. Инструкция — в README.md.");
        return 1;
    }
    finally { Console.CancelKeyPress -= cancel; }
}
builder.WebHost.UseKestrel(options =>
{
    options.Listen(System.Net.IPAddress.Loopback, port);
    options.Limits.MaxRequestBodySize = mediaOptions.MaxUploadBytes;
});
builder.Services.AddSingleton(mediaOptions);
builder.Services.AddSingleton<MediaTools>();
builder.Services.AddSingleton<MediaStore>();
builder.Services.AddSingleton<PhotoStore>();
builder.Services.AddSingleton<ProcessingGate>();
builder.Services.AddSingleton(new AiCatalog());
builder.Services.AddSingleton<AiRunner>();
builder.Services.AddSingleton<RifeRunner>();
builder.Services.AddSingleton<AiPackages>();
builder.Services.AddSingleton<AiPipeline>();
builder.Services.AddSingleton<FrameWorkspace>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<AiPackages>());
builder.Services.AddSingleton<ExportService>();
builder.Services.AddSingleton<PhotoExportService>();
builder.Services.AddSingleton<ExportArchive>();
builder.Services.AddSingleton<DownloadTools>();
builder.Services.AddSingleton<LinkDownloadService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<LinkDownloadService>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<ExportService>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<PhotoExportService>());
var app = builder.Build();
app.Use(async (context, next) =>
{
    if (context.Request.Host.Host != "127.0.0.1") { context.Response.StatusCode = 403; return; }
    if (context.Request.Path.StartsWithSegments("/api") && context.Request.Method is not ("GET" or "HEAD"))
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (context.Request.Headers["X-VidCropper"] != "1" ||
            (origin.Length > 0 && origin != $"http://{context.Request.Host}"))
        { context.Response.StatusCode = 403; return; }
    }
    try { await next(context); }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested || app.Lifetime.ApplicationStopping.IsCancellationRequested) { }
    catch (Exception exception) when (!context.Response.HasStarted)
    {
        var status = exception switch { MediaException media => media.Status, BadHttpRequestException bad => bad.StatusCode, _ => 500 };
        if (status == 500) app.Logger.LogError(exception, "Ошибка запроса");
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { error = exception is MediaException ? exception.Message :
            status == 413 ? "Файл превышает лимит загрузки." : status == 400 ? "Некорректный запрос." : "Ошибка сервера. Проверьте свободное место и журнал." });
    }
});
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    // The local URL stays the same across upgrades; revalidate HTML and all module imports.
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache"
});
app.MapMediaApi(mediaOptions);
app.Lifetime.ApplicationStarted.Register(() =>
{
    var address = app.Urls.Single();
    Console.WriteLine($"VidCropper: {address}");
    if (!builder.Configuration.GetValue<bool>("OpenBrowser")) return;
    try { Process.Start(new ProcessStartInfo(address) { UseShellExecute = true }); }
    catch (Exception exception) { app.Logger.LogWarning(exception, "Откройте {Address} в браузере вручную.", address); }
});
try { await app.RunAsync(); return 0; }
catch (IOException exception)
{
    app.Logger.LogError(exception, "Не удалось запустить сервер на порту {Port}. Возможно, он занят. Укажите другой: Start.cmd 5300 или Start.cmd 0.", port);
    return 1;
}
