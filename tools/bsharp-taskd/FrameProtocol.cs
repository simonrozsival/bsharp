// Length-prefixed JSON framing used by the bsharp-taskd protocol.
//
// Wire format: [4-byte little-endian uint32 length] [length bytes of UTF-8 JSON].
// Matches the per-project sidecar's framing exactly, so the protocol upgrade is
// purely additive at the message-level (new types behind a handshake).
#nullable enable
using System;
using System.IO;

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
}
