using System.Buffers.Binary;

namespace VidCropper.Backend;

// PNG is self-delimiting: IEND ends a frame, unlike scanning compressed data for signatures.
public static class PngFrames
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    public static async Task<(int Width, int Height)?> CopyFrameAsync(Stream source, Stream destination, CancellationToken ct)
    {
        var signature = new byte[8];
        if (await source.ReadAsync(signature.AsMemory(0, 1), ct) == 0) return null;
        await source.ReadExactlyAsync(signature.AsMemory(1), ct);
        if (!signature.AsSpan().SequenceEqual(Signature)) throw new MediaException("Повреждён поток кадров PNG.");
        await destination.WriteAsync(signature, ct);
        var buffer = new byte[65536];
        var header = new byte[8];
        int width = 0, height = 0;
        long total = 8;
        while (true)
        {
            await source.ReadExactlyAsync(header, ct);
            var length = BinaryPrimitives.ReadUInt32BigEndian(header);
            total += length + 12L;
            if (total > 1024L * 1024 * 1024) throw new MediaException("Слишком большой кадр PNG.");
            await destination.WriteAsync(header, ct);
            var ihdr = header.AsSpan(4).SequenceEqual("IHDR"u8);
            var end = header.AsSpan(4).SequenceEqual("IEND"u8);
            if (width == 0 && !ihdr) throw new MediaException("У кадра PNG отсутствует IHDR.");
            if (ihdr)
            {
                if (length != 13 || width != 0) throw new MediaException("Некорректный заголовок PNG.");
                await source.ReadExactlyAsync(buffer.AsMemory(0, 13), ct);
                width = BinaryPrimitives.ReadInt32BigEndian(buffer);
                height = BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(4));
                if (width <= 0 || height <= 0) throw new MediaException("Некорректный размер кадра PNG.");
                await destination.WriteAsync(buffer.AsMemory(0, 13), ct);
                length = 0;
            }
            long remaining = length + 4L; // chunk data and CRC
            while (remaining > 0)
            {
                var count = (int)Math.Min(buffer.Length, remaining);
                await source.ReadExactlyAsync(buffer.AsMemory(0, count), ct);
                await destination.WriteAsync(buffer.AsMemory(0, count), ct);
                remaining -= count;
            }
            if (end)
            {
                if (length != 0) throw new MediaException("Некорректный конец кадра PNG.");
                return (width, height);
            }
        }
    }
}
