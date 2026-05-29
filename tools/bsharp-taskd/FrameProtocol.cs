// Length-prefixed JSON framing used by the bsharp-taskd protocol.
//
// Wire format: [4-byte little-endian uint32 length] [length bytes of UTF-8 JSON].
// Matches the per-project sidecar's framing exactly, so the protocol upgrade is
// purely additive at the message-level (new types behind a handshake).
#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Bsharp.Taskd;

internal static class FrameProtocol {
    public const int MaxFrameLength = 64 * 1024 * 1024;

    public static byte[]? ReadFrame(Stream input) {
        Span<byte> lenBytes = stackalloc byte[4];
        var read = input.Read(lenBytes);
        if (read == 0) return null;
        while (read < 4) {
            var n = input.Read(lenBytes[read..]);
            if (n == 0) throw new EndOfStreamException();
            read += n;
        }
        var len = BitConverter.ToInt32(lenBytes);
        if (len < 0 || len > MaxFrameLength)
            throw new InvalidDataException($"invalid frame length {len}");
        var payload = new byte[len];
        var offset = 0;
        while (offset < len) {
            var n = input.Read(payload, offset, len - offset);
            if (n == 0) throw new EndOfStreamException();
            offset += n;
        }
        return payload;
    }

    public static void WriteFrame(Stream output, ReadOnlySpan<byte> payload) {
        Span<byte> lenBytes = stackalloc byte[4];
        BitConverter.TryWriteBytes(lenBytes, payload.Length);
        output.Write(lenBytes);
        output.Write(payload);
        output.Flush();
    }

    public static async Task<byte[]?> ReadFrameAsync(Stream input, CancellationToken cancellationToken = default) {
        var lenBuf = new byte[4];
        var read = await input.ReadAsync(lenBuf.AsMemory(0, 4), cancellationToken).ConfigureAwait(false);
        if (read == 0) return null;
        while (read < 4) {
            var n = await input.ReadAsync(lenBuf.AsMemory(read, 4 - read), cancellationToken).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException();
            read += n;
        }
        var len = BitConverter.ToInt32(lenBuf);
        if (len < 0 || len > MaxFrameLength)
            throw new InvalidDataException($"invalid frame length {len}");
        var payload = new byte[len];
        var offset = 0;
        while (offset < len) {
            var n = await input.ReadAsync(payload.AsMemory(offset, len - offset), cancellationToken).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException();
            offset += n;
        }
        return payload;
    }

    public static async Task WriteFrameAsync(Stream output, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) {
        var lenBuf = new byte[4];
        BitConverter.TryWriteBytes(lenBuf, payload.Length);
        await output.WriteAsync(lenBuf, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
