using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using AgentForge.Domain.Devices;

namespace AgentForge.Devices;

internal static class SerialCaptureCodec
{
    private static readonly byte[] Magic = "AFSCAP01"u8.ToArray();
    private const int MaximumFrames = 65_536;

    public static MemoryStream Encode(PhysicalDeviceId physicalId, IReadOnlyList<SerialCaptureFrame> frames)
    {
        if (!SerialDeviceRecordValidator.IsPhysicalId(physicalId) || frames.Count > MaximumFrames || frames.Any(frame => !frame.IsValid()))
            throw new InvalidDataException("Serial capture cannot be encoded.");
        var stream = new MemoryStream();
        stream.Write(Magic);
        WriteInt32(stream, 1);
        WriteBytes(stream, Encoding.UTF8.GetBytes(physicalId.Value));
        WriteInt32(stream, frames.Count);
        foreach (var frame in frames)
        {
            WriteInt64(stream, frame.OffsetTicks);
            WriteInt64(stream, frame.DroppedBefore);
            stream.WriteByte(frame.DisconnectedAfter ? (byte)1 : (byte)0);
            WriteBytes(stream, frame.Bytes.AsSpan());
        }
        stream.Position = 0;
        return stream;
    }

    public static async Task<IReadOnlyList<SerialCaptureFrame>> DecodeAsync(
        Stream stream,
        PhysicalDeviceId expectedDevice,
        long maximumArtifactLength,
        CancellationToken cancellationToken)
    {
        if (maximumArtifactLength is < 1 or > 33_554_432)
            throw new InvalidDataException("Serial capture artifact length is invalid.");
        var buffer = new byte[maximumArtifactLength];
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0) break;
            offset += read;
        }
        var extra = new byte[1];
        if (offset != buffer.Length || await stream.ReadAsync(extra, cancellationToken) != 0)
            throw new InvalidDataException("Serial capture artifact length does not match its reference.");
        return Decode(buffer, expectedDevice);
    }

    public static string HashFrames(IEnumerable<SerialCaptureFrame> frames)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> numeric = stackalloc byte[8];
        foreach (var frame in frames)
        {
            BinaryPrimitives.WriteInt64LittleEndian(numeric, frame.OffsetTicks);
            hash.AppendData(numeric);
            BinaryPrimitives.WriteInt64LittleEndian(numeric, frame.DroppedBefore);
            hash.AppendData(numeric);
            hash.AppendData([frame.DisconnectedAfter ? (byte)1 : (byte)0]);
            BinaryPrimitives.WriteInt64LittleEndian(numeric, frame.Bytes.Length);
            hash.AppendData(numeric);
            hash.AppendData(frame.Bytes.AsSpan());
        }
        return $"sha256:{Convert.ToHexStringLower(hash.GetHashAndReset())}";
    }

    private static List<SerialCaptureFrame> Decode(ReadOnlySpan<byte> content, PhysicalDeviceId expectedDevice)
    {
        var offset = 0;
        if (content.Length < Magic.Length || !content[..Magic.Length].SequenceEqual(Magic))
            throw new InvalidDataException("Serial capture magic is invalid.");
        offset += Magic.Length;
        if (ReadInt32(content, ref offset) != 1) throw new InvalidDataException("Serial capture version is unsupported.");
        var device = Encoding.UTF8.GetString(ReadBytes(content, ref offset, 256));
        if (!string.Equals(device, expectedDevice.Value, StringComparison.Ordinal))
            throw new InvalidDataException("Serial capture device binding is invalid.");
        var count = ReadInt32(content, ref offset);
        if (count is < 0 or > MaximumFrames) throw new InvalidDataException("Serial frame count is invalid.");
        var frames = new List<SerialCaptureFrame>(count);
        long priorTicks = -1;
        for (var index = 0; index < count; index++)
        {
            var ticks = ReadInt64(content, ref offset);
            var dropped = ReadInt64(content, ref offset);
            if (offset >= content.Length) throw new InvalidDataException("Serial disconnect marker is missing.");
            var disconnected = content[offset++] switch { 0 => false, 1 => true, _ => throw new InvalidDataException("Serial disconnect marker is invalid.") };
            var bytes = ReadBytes(content, ref offset, 1_048_576).ToArray().ToImmutableArray();
            var frame = new SerialCaptureFrame(ticks, bytes, dropped, disconnected);
            if (!frame.IsValid() || ticks < priorTicks) throw new InvalidDataException("Serial frame is invalid or unordered.");
            frames.Add(frame);
            priorTicks = ticks;
        }
        if (offset != content.Length) throw new InvalidDataException("Serial capture has trailing content.");
        return frames;
    }

    private static void WriteBytes(Stream stream, ReadOnlySpan<byte> bytes)
    {
        WriteInt32(stream, bytes.Length);
        stream.Write(bytes);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteInt64(Stream stream, long value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static int ReadInt32(ReadOnlySpan<byte> content, ref int offset)
    {
        if (content.Length - offset < 4) throw new InvalidDataException("Serial capture integer is truncated.");
        var value = BinaryPrimitives.ReadInt32LittleEndian(content[offset..]);
        offset += 4;
        return value;
    }

    private static long ReadInt64(ReadOnlySpan<byte> content, ref int offset)
    {
        if (content.Length - offset < 8) throw new InvalidDataException("Serial capture integer is truncated.");
        var value = BinaryPrimitives.ReadInt64LittleEndian(content[offset..]);
        offset += 8;
        return value;
    }

    private static ReadOnlySpan<byte> ReadBytes(ReadOnlySpan<byte> content, ref int offset, int maximum)
    {
        var length = ReadInt32(content, ref offset);
        if (length is < 0 || length > maximum || content.Length - offset < length)
            throw new InvalidDataException("Serial capture byte segment is invalid.");
        var value = content.Slice(offset, length);
        offset += length;
        return value;
    }
}
