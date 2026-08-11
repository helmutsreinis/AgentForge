using AgentForge.Abstractions.Channels;
using AgentForge.Domain.Channels;

namespace AgentForge.Channels;

internal sealed class RejectingAttachmentScanner : IChannelAttachmentScanner
{
    public ValueTask<AttachmentScanStatus> ScanAsync(
        ChannelAttachment attachment,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(AttachmentScanStatus.Rejected);
    }
}
