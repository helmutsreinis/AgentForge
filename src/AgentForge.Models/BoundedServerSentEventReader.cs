using System.Buffers;
using System.Text;

namespace AgentForge.Models;

internal sealed class BoundedServerSentEventReader : IAsyncDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Stream _stream;
    private readonly int _maximumEventBytes;
    private readonly long _maximumResponseBytes;
    private readonly byte[] _buffer = ArrayPool<byte>.Shared.Rent(8192);
    private int _bufferOffset;
    private int _bufferLength;
    private long _responseBytes;
    private bool _disposed;

    public BoundedServerSentEventReader(
        Stream stream,
        int maximumEventBytes,
        long maximumResponseBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _maximumEventBytes = maximumEventBytes;
        _maximumResponseBytes = maximumResponseBytes;
    }

    public async ValueTask<SseReadResult> ReadAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var data = new ArrayBufferWriter<byte>();
        var dataFieldCount = 0;
        while (true)
        {
            var line = await ReadLineAsync(cancellationToken);
            if (line.Error is not null)
            {
                return SseReadResult.Failed(line.Error);
            }

            if (line.IsEndOfStream)
            {
                return dataFieldCount == 0
                    ? SseReadResult.EndOfStream()
                    : Decode(data.WrittenSpan);
            }

            if (!IsStrictUtf8(line.Bytes.Span))
            {
                return SseReadResult.Failed("The provider stream was not strict UTF-8.");
            }

            if (line.Bytes.Length == 0)
            {
                if (dataFieldCount > 0)
                {
                    return Decode(data.WrittenSpan);
                }

                continue;
            }

            var span = line.Bytes.Span;
            if (span[0] == (byte)':')
            {
                continue;
            }

            var colon = span.IndexOf((byte)':');
            var field = colon < 0 ? span : span[..colon];
            if (!field.SequenceEqual("data"u8))
            {
                continue;
            }

            var value = colon < 0 ? ReadOnlySpan<byte>.Empty : span[(colon + 1)..];
            if (value.Length > 0 && value[0] == (byte)' ')
            {
                value = value[1..];
            }

            var separatorBytes = dataFieldCount == 0 ? 0 : 1;
            if (data.WrittenCount + separatorBytes + value.Length > _maximumEventBytes)
            {
                return SseReadResult.Failed("The provider stream event exceeded its byte bound.");
            }

            if (separatorBytes == 1)
            {
                data.GetSpan(1)[0] = (byte)'\n';
                data.Advance(1);
            }

            value.CopyTo(data.GetSpan(value.Length));
            data.Advance(value.Length);
            dataFieldCount++;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
        await _stream.DisposeAsync();
    }

    private async ValueTask<LineReadResult> ReadLineAsync(CancellationToken cancellationToken)
    {
        var line = new ArrayBufferWriter<byte>();
        while (true)
        {
            if (_bufferOffset >= _bufferLength)
            {
                _bufferLength = await _stream.ReadAsync(_buffer, cancellationToken);
                _bufferOffset = 0;
                if (_bufferLength == 0)
                {
                    return line.WrittenCount == 0
                        ? LineReadResult.EndOfStream()
                        : LineReadResult.Success(TrimCarriageReturn(line.WrittenMemory));
                }

                _responseBytes += _bufferLength;
                if (_responseBytes > _maximumResponseBytes)
                {
                    return LineReadResult.Failed("The provider response exceeded its byte bound.");
                }
            }

            var available = _buffer.AsSpan(_bufferOffset, _bufferLength - _bufferOffset);
            var newline = available.IndexOf((byte)'\n');
            var length = newline < 0 ? available.Length : newline;
            if (line.WrittenCount + length > _maximumEventBytes)
            {
                return LineReadResult.Failed("The provider stream line exceeded its byte bound.");
            }

            available[..length].CopyTo(line.GetSpan(length));
            line.Advance(length);
            _bufferOffset += length;
            if (newline >= 0)
            {
                _bufferOffset++;
                return LineReadResult.Success(TrimCarriageReturn(line.WrittenMemory));
            }
        }
    }

    private static SseReadResult Decode(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return SseReadResult.Event(StrictUtf8.GetString(bytes));
        }
        catch (DecoderFallbackException)
        {
            return SseReadResult.Failed("The provider stream event was not strict UTF-8.");
        }
    }

    private static bool IsStrictUtf8(ReadOnlySpan<byte> bytes)
    {
        try
        {
            _ = StrictUtf8.GetCharCount(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static ReadOnlyMemory<byte> TrimCarriageReturn(ReadOnlyMemory<byte> bytes) =>
        bytes.Length > 0 && bytes.Span[^1] == (byte)'\r' ? bytes[..^1] : bytes;

    private readonly record struct LineReadResult(
        ReadOnlyMemory<byte> Bytes,
        bool IsEndOfStream,
        string? Error)
    {
        public static LineReadResult Success(ReadOnlyMemory<byte> bytes) => new(bytes, false, null);

        public static LineReadResult EndOfStream() => new(ReadOnlyMemory<byte>.Empty, true, null);

        public static LineReadResult Failed(string error) => new(ReadOnlyMemory<byte>.Empty, false, error);
    }
}

internal readonly record struct SseReadResult(
    string? Data,
    bool IsEndOfStream,
    string? Error)
{
    public static SseReadResult Event(string data) => new(data, false, null);

    public static SseReadResult EndOfStream() => new(null, true, null);

    public static SseReadResult Failed(string error) => new(null, false, error);
}
