using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using AgentForge.Abstractions.Devices;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Devices;

namespace AgentForge.Devices;

internal sealed class PassiveSerialDiscoveryService(
    IPassiveSerialInventorySource source,
    IClock clock) : ISerialDiscoveryService, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SerialInventorySnapshot? _previous;

    public async ValueTask<SerialInventorySnapshot> InspectAsync(CancellationToken cancellationToken)
    {
        var candidates = await source.InspectAsync(cancellationToken);
        if (candidates.Count > 4096) throw new InvalidDataException("Serial inventory exceeds the bounded device count.");
        var observedAt = clock.UtcNow;
        var devices = candidates.Select(candidate => Normalize(candidate, observedAt))
            .OrderBy(device => device.PhysicalId.Value, StringComparer.Ordinal)
            .ThenBy(device => device.Endpoint, StringComparer.Ordinal)
            .ToImmutableArray();
        if (devices.Select(device => device.Endpoint).Distinct(StringComparer.Ordinal).Count() != devices.Length)
            throw new InvalidDataException("Serial inventory contains a duplicate endpoint.");
        var snapshot = new SerialInventorySnapshot(observedAt, devices, SnapshotHash(devices));
        return snapshot.IsValid() ? snapshot : throw new InvalidDataException("Serial inventory failed validation.");
    }

    public async ValueTask<IReadOnlyList<SerialInventoryChange>> InspectChangesAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = await InspectAsync(cancellationToken);
            IReadOnlyList<SerialInventoryChange> changes = _previous is null ? current.Devices.Select(device => new SerialInventoryChange(
                    SerialInventoryChangeKind.Attached, device.PhysicalId, null, device.Endpoint,
                    SerialDeviceReadiness.Detached, device.Readiness, current.ObservedAtUtc)).ToArray()
                : Diff(_previous, current);
            _previous = current;
            return changes;
        }
        finally { _gate.Release(); }
    }

    private static SerialDeviceDescriptor Normalize(PassiveSerialCandidate candidate, DateTimeOffset observedAt)
    {
        if (!SerialDeviceRecordValidator.Text(candidate.Endpoint, 1024) ||
            !SerialDeviceRecordValidator.Text(candidate.Platform, 32) ||
            !SerialDeviceRecordValidator.Text(candidate.IdentityEvidence, 2048))
            throw new InvalidDataException("Passive serial candidate is invalid.");
        var platform = candidate.Platform.Trim().ToLowerInvariant();
        var evidence = candidate.IdentityEvidence.Trim();
        var endpoint = candidate.Endpoint.Trim();
        var physicalId = SerialDeviceRecordValidator.PhysicalIdFromEvidence(platform, evidence);
        var hash = Hash($"{physicalId.Value}\n{endpoint}\n{candidate.Readiness}\n{candidate.VendorId}\n{candidate.ProductId}\n{candidate.SerialNumber}");
        return new SerialDeviceDescriptor(physicalId, endpoint, platform, candidate.VendorId?.Trim(),
            candidate.ProductId?.Trim(), candidate.SerialNumber?.Trim(), evidence, candidate.Readiness,
            candidate.ReadinessReason?.Trim(), observedAt, hash);
    }

    public void Dispose() => _gate.Dispose();

    private static List<SerialInventoryChange> Diff(SerialInventorySnapshot previous, SerialInventorySnapshot current)
    {
        var changes = new List<SerialInventoryChange>();
        var old = previous.Devices.GroupBy(device => device.PhysicalId).ToDictionary(group => group.Key, group => group.First());
        var now = current.Devices.GroupBy(device => device.PhysicalId).ToDictionary(group => group.Key, group => group.First());
        foreach (var pair in old.Where(pair => !now.ContainsKey(pair.Key)).OrderBy(pair => pair.Key.Value, StringComparer.Ordinal))
            changes.Add(new(SerialInventoryChangeKind.Detached, pair.Key, pair.Value.Endpoint, null,
                pair.Value.Readiness, SerialDeviceReadiness.Detached, current.ObservedAtUtc));
        foreach (var pair in now.OrderBy(pair => pair.Key.Value, StringComparer.Ordinal))
        {
            if (!old.TryGetValue(pair.Key, out var prior))
                changes.Add(new(SerialInventoryChangeKind.Attached, pair.Key, null, pair.Value.Endpoint,
                    SerialDeviceReadiness.Detached, pair.Value.Readiness, current.ObservedAtUtc));
            else if (!string.Equals(prior.Endpoint, pair.Value.Endpoint, StringComparison.Ordinal))
                changes.Add(new(SerialInventoryChangeKind.Reenumerated, pair.Key, prior.Endpoint, pair.Value.Endpoint,
                    prior.Readiness, pair.Value.Readiness, current.ObservedAtUtc));
            else if (prior.Readiness != pair.Value.Readiness)
                changes.Add(new(SerialInventoryChangeKind.ReadinessChanged, pair.Key, prior.Endpoint, pair.Value.Endpoint,
                    prior.Readiness, pair.Value.Readiness, current.ObservedAtUtc));
        }
        return changes;
    }

    private static string SnapshotHash(IEnumerable<SerialDeviceDescriptor> devices) => Hash(string.Join('\n',
        devices.Select(device => $"{device.PhysicalId.Value}|{device.Endpoint}|{device.Readiness}|{device.EvidenceHash}")));

    private static string Hash(string value) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))}";
}
