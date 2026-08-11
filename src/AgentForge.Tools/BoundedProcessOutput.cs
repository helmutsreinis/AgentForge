using AgentForge.Abstractions.Tools;
using AgentForge.Domain.Tools;

namespace AgentForge.Tools;

internal sealed class BoundedProcessOutput(int maximumBytes) : IDisposable
{
    private readonly SemaphoreSlim _appendGate = new(1, 1);
    private readonly MemoryStream _standardOutput = new();
    private readonly MemoryStream _standardError = new();
    private readonly TaskCompletionSource _limitExceeded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _totalBytes;
    private long _sequence;

    public Task LimitExceeded => _limitExceeded.Task;

    public async ValueTask<ProcessOutputChunk?> AppendAsync(
        ProcessOutputChannel channel,
        ReadOnlyMemory<byte> data,
        IProcessOutputObserver? observer,
        CancellationToken cancellationToken)
    {
        await _appendGate.WaitAsync(cancellationToken);
        try
        {
            var available = Math.Max(0, maximumBytes - _totalBytes);
            var accepted = Math.Min(available, data.Length);
            ProcessOutputChunk? chunk = null;
            if (accepted > 0)
            {
                var copy = data.Span[..accepted].ToArray();
                var destination = channel is ProcessOutputChannel.StandardOutput
                    ? _standardOutput
                    : _standardError;
                destination.Write(copy);
                _totalBytes += accepted;
                chunk = new ProcessOutputChunk(++_sequence, channel, copy);
                if (observer is not null)
                {
                    await observer.ObserveAsync(chunk, cancellationToken);
                }
            }

            if (accepted != data.Length)
            {
                _limitExceeded.TrySetResult();
            }

            return chunk;
        }
        finally
        {
            _appendGate.Release();
        }
    }

    public (byte[] StandardOutput, byte[] StandardError) Snapshot() =>
        (_standardOutput.ToArray(), _standardError.ToArray());

    public void Dispose()
    {
        _standardOutput.Dispose();
        _standardError.Dispose();
        _appendGate.Dispose();
    }
}
