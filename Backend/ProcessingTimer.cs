namespace VidCropper.Backend;

// Uses monotonic time; callers synchronize access with their task state.
public sealed class ProcessingTimer(TimeProvider? clock = null)
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly long started = (clock ?? TimeProvider.System).GetTimestamp();
    private long? stageStarted;
    private long? stopped;
    private long? sampleStarted;
    private double sampleDone;
    private string? stage;
    private double done, total;

    public void Report(string id, double completed, double count)
    {
        if (stopped is not null) return;
        if (stage != id) { stage = id; stageStarted = clock.GetTimestamp(); done = 0; sampleStarted = null; }
        done = Math.Max(done, completed);
        total = count;
        // Loading the model and warming up the first frame are not steady processing speed.
        if (done > 0 && sampleStarted is null) { sampleStarted = clock.GetTimestamp(); sampleDone = done; }
    }

    public void Stop() => stopped ??= clock.GetTimestamp();
    public double ElapsedSeconds => clock.GetElapsedTime(started, stopped ?? clock.GetTimestamp()).TotalSeconds;
    public double StageElapsedSeconds => stageStarted is { } start
        ? clock.GetElapsedTime(start, stopped ?? clock.GetTimestamp()).TotalSeconds : 0;
    public double? RemainingSeconds
    {
        get
        {
            if (stopped is not null || total <= 0 || done <= 0) return null;
            if (done >= total) return 0;
            if (sampleStarted is not { } sample || done <= sampleDone) return null;
            var elapsed = clock.GetElapsedTime(sample, clock.GetTimestamp()).TotalSeconds;
            if (elapsed < 1) return null;
            var estimate = elapsed * (total - done) / (done - sampleDone);
            return double.IsFinite(estimate) ? Math.Max(0, estimate) : null;
        }
    }
}
