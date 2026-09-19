using System.Buffers;
using System.Text.Json;

namespace VidCropper.Backend;

public static class Api
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void MapMediaApi(this WebApplication app, MediaOptions options)
    {
        app.MapGet("/api/config", async (MediaTools tools, CancellationToken ct) =>
            Results.Ok(new { maxUploadBytes = options.MaxUploadBytes, error = await tools.CheckAsync(ct) }));
        app.MapPut("/api/media/{id:guid}", async (Guid id, HttpRequest request, MediaStore store, MediaTools tools, IHostApplicationLifetime lifetime) =>
        {
            if (request.ContentType != "application/octet-stream") throw new MediaException("Ожидается видеофайл.", 415);
            if (request.ContentLength > options.MaxUploadBytes) throw new MediaException("Файл превышает лимит загрузки.", 413);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(request.HttpContext.RequestAborted, lifetime.ApplicationStopping);
            var ct = linked.Token;
            var path = Path.Combine(store.Root, $"{id:N}.source");
            var name = Path.GetFileName(request.Query["name"].ToString());
            if (string.IsNullOrWhiteSpace(name)) name = "video";
            if (name.Length > 200) name = name[..200];
            FileStream file;
            try { file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous); }
            catch (IOException) when (File.Exists(path)) { throw new MediaException("Этот файл уже загружается.", 409); }
            try
            {
                await using (file)
                {
                    var buffer = ArrayPool<byte>.Shared.Rent(65536);
                    try
                    {
                        long total = 0;
                        int read;
                        while ((read = await request.Body.ReadAsync(buffer, ct)) > 0)
                        {
                            total += read;
                            if (total > options.MaxUploadBytes) throw new MediaException("Файл превышает лимит загрузки.", 413);
                            await file.WriteAsync(buffer.AsMemory(0, read), ct);
                        }
                        if (total == 0) throw new MediaException("Файл пуст.");
                    }
                    finally { ArrayPool<byte>.Shared.Return(buffer); }
                }
                var info = await tools.ProbeAsync(path, ct);
                ct.ThrowIfCancellationRequested();
                store.Add(id, path, name, info);
                return Results.Ok(new { id, info });
            }
            catch { store.TryDelete(path); throw; }
        });
        app.MapDelete("/api/media/{id:guid}", (Guid id, MediaStore store) => { store.Delete(id); return Results.NoContent(); });
        app.MapPost("/api/exports", (ExportRequest request, ExportService service) => Results.Ok(service.Start(request)));
        app.MapGet("/api/exports/{id:guid}", (Guid id, ExportService service) => service.Get(id));
        app.MapPost("/api/exports/{id:guid}/cancel", async (Guid id, ExportService service) => Results.Ok(await service.CancelAsync(id)));
        app.MapDelete("/api/exports/{id:guid}", async (Guid id, ExportService service) => { await service.DeleteAsync(id); return Results.NoContent(); });
        app.MapGet("/api/exports/{id:guid}/download", (Guid id, ExportService service) =>
        {
            var result = service.OpenResult(id);
            return Results.File(result.Stream, "video/mp4", result.Name, enableRangeProcessing: true);
        });
        app.MapGet("/api/exports/{id:guid}/events", async (Guid id, ExportService service, HttpContext context) =>
        {
            var snapshot = service.Get(id);
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            while (true)
            {
                await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(snapshot, JsonOptions)}\n\n", context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                if (snapshot.Status is "completed" or "failed" or "cancelled") break;
                await Task.Delay(300, context.RequestAborted);
                snapshot = service.Get(id);
            }
        });
        app.MapPost("/api/shutdown", (HttpContext context, IHostApplicationLifetime lifetime) =>
        {
            context.Response.OnCompleted(() => { lifetime.StopApplication(); return Task.CompletedTask; });
            return Results.Ok(new { message = "Приложение завершает работу." });
        });
    }
}
