using System.Threading.Channels;

namespace BaseClean.Domain.Audit;

public sealed class AuditQueue
{
    private readonly Channel<AuditEntry> _channel =
        Channel.CreateBounded<AuditEntry>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });

    public bool TryWrite(AuditEntry entry) => _channel.Writer.TryWrite(entry);

    public IAsyncEnumerable<AuditEntry> ReadAllAsync(CancellationToken ct)
        => _channel.Reader.ReadAllAsync(ct);
}
