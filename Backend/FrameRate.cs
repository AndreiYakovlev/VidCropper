using System.Globalization;

namespace VidCropper.Backend;

// Keep the source time base exact through extraction, interpolation and encoding.
public readonly record struct FrameRate
{
    public long Numerator { get; }
    public long Denominator { get; }
    public double Value => (double)Numerator / Denominator;

    public FrameRate(long numerator, long denominator = 1)
    {
        if (numerator <= 0 || denominator <= 0)
            throw new MediaException("Не удалось определить корректную частоту кадров исходника.");
        var a = numerator; var b = denominator;
        while (b != 0) (a, b) = (b, a % b);
        Numerator = numerator / a; Denominator = denominator / a;
    }

    public static FrameRate? Parse(string? text)
    {
        var parts = text?.Split('/');
        return parts is { Length: 2 } && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var n) &&
            long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var d) && n > 0 && d > 0
            ? new(n, d) : null;
    }

    public static FrameRate Source(VideoInfo source) => Parse(source.FrameRate) ??
        (double.IsFinite(source.Fps) && source.Fps > 0 && source.Fps < 1_000_000
            ? new FrameRate((long)Math.Round(source.Fps * 1_000_000), 1_000_000)
            : throw new MediaException("Не удалось определить частоту кадров для интерполяции."));

    public FrameRate Multiply(int factor) => new(checked(Numerator * factor), Denominator);
    public override string ToString() => FormattableString.Invariant($"{Numerator}/{Denominator}");
    public static implicit operator FrameRate(int fps) => new(fps);
}
