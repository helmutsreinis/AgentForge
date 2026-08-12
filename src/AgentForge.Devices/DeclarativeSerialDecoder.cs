using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using AgentForge.Abstractions.Devices;
using AgentForge.Domain.Devices;
using AgentForge.Domain.Primitives;

namespace AgentForge.Devices;

internal sealed class DeclarativeSerialDecoder : IDeclarativeDecoder
{
    public DomainResult<DecoderParseResult> Decode(
        DeclarativeDecoderDefinition definition,
        ReadOnlyMemory<byte> input)
    {
        if (definition is null || !definition.IsValid() || input.Length > 1_048_576)
            return DomainResult.Fail<DecoderParseResult>(new(FailureCode.ValidationFailure, "Decoder definition or input bounds are invalid."));
        var frames = ImmutableArray.CreateBuilder<DecodedSerialFrame>();
        var unframed = ImmutableArray.CreateBuilder<UnknownByteSegment>();
        var span = input.Span;
        var cursor = 0;
        var operations = 0;
        while (cursor + definition.FrameLength <= span.Length)
        {
            operations++;
            if (!span.Slice(cursor, definition.SyncPrefix.Length).SequenceEqual(definition.SyncPrefix.AsSpan()))
            {
                var start = cursor++;
                while (cursor + definition.FrameLength <= span.Length &&
                    !span.Slice(cursor, definition.SyncPrefix.Length).SequenceEqual(definition.SyncPrefix.AsSpan()))
                {
                    cursor++;
                    operations++;
                }
                unframed.Add(new(start, span[start..cursor].ToArray().ToImmutableArray()));
                continue;
            }
            var raw = span.Slice(cursor, definition.FrameLength);
            var fields = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            var claimed = new bool[definition.FrameLength];
            for (var index = 0; index < definition.SyncPrefix.Length; index++) claimed[index] = true;
            foreach (var field in definition.Fields)
            {
                fields[field.Name] = DecodeField(raw.Slice(field.Offset, field.Length), field.Encoding);
                for (var index = field.Offset; index < field.Offset + field.Length; index++) claimed[index] = true;
                operations += field.Length;
            }
            var unknown = ImmutableArray.CreateBuilder<UnknownByteSegment>();
            var unknownIndex = 0;
            while (unknownIndex < claimed.Length)
            {
                if (claimed[unknownIndex]) { unknownIndex++; continue; }
                var start = unknownIndex;
                while (unknownIndex < claimed.Length && !claimed[unknownIndex]) unknownIndex++;
                unknown.Add(new(cursor + start, raw[start..unknownIndex].ToArray().ToImmutableArray()));
            }
            frames.Add(new(cursor, fields.ToImmutable(), unknown.ToImmutable(), Hash(raw)));
            cursor += definition.FrameLength;
            operations += definition.FrameLength;
        }
        if (cursor < span.Length)
            unframed.Add(new(cursor, span[cursor..].ToArray().ToImmutableArray()));
        return DomainResult.Success(new DecoderParseResult(frames.ToImmutable(), unframed.ToImmutable(), input.Length, operations));
    }

    private static string DecodeField(ReadOnlySpan<byte> bytes, DecoderFieldEncoding encoding) => encoding switch
    {
        DecoderFieldEncoding.ByteUnsigned => bytes[0].ToString(CultureInfo.InvariantCulture),
        DecoderFieldEncoding.UInt16LittleEndian => BinaryPrimitives.ReadUInt16LittleEndian(bytes).ToString(CultureInfo.InvariantCulture),
        DecoderFieldEncoding.UInt16BigEndian => BinaryPrimitives.ReadUInt16BigEndian(bytes).ToString(CultureInfo.InvariantCulture),
        DecoderFieldEncoding.Int16LittleEndian => BinaryPrimitives.ReadInt16LittleEndian(bytes).ToString(CultureInfo.InvariantCulture),
        DecoderFieldEncoding.Int16BigEndian => BinaryPrimitives.ReadInt16BigEndian(bytes).ToString(CultureInfo.InvariantCulture),
        DecoderFieldEncoding.Ascii => System.Text.Encoding.ASCII.GetString(bytes),
        DecoderFieldEncoding.Bytes => Convert.ToHexStringLower(bytes),
        _ => throw new InvalidOperationException("Decoder field encoding is unsupported."),
    };

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";
}
