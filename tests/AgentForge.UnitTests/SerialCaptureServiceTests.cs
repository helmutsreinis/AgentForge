using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using AgentForge.Abstractions.Artifacts;
using AgentForge.Abstractions.Auditing;
using AgentForge.Abstractions.Devices;
using AgentForge.Abstractions.Persistence;
using AgentForge.Abstractions.Time;
using AgentForge.Devices;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Artifacts;
using AgentForge.Domain.Auditing;
using AgentForge.Domain.Devices;
using AgentForge.Domain.Persistence;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Security;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.UnitTests;

public sealed class SerialCaptureServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 12, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Capture_is_bounded_durable_replayable_and_idempotent()
    {
        var adapter = new FakeTransport([
            new(TimeSpan.Zero, new byte[] { 1, 2, 3 }, 0, false),
            new(TimeSpan.FromMilliseconds(10), new byte[] { 4, 5, 6, 7 }, 2, false),
        ]);
        var repository = new FakeCaptureRepository();
        var artifacts = new MemoryArtifactStore();
        await using var provider = Services(adapter, repository, artifacts).BuildServiceProvider();
        var service = provider.GetRequiredService<ISerialCaptureService>();
        var request = CaptureRequest(DeviceCapability.Capture, maximumBytes: 5);

        var result = await service.CaptureAsync(request, CancellationToken.None);
        var replay = await ReadReplayAsync(service, result.Value);
        var exactReplay = await service.CaptureAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Failure?.Message);
        Assert.Equal(5, result.Value.CapturedBytes);
        Assert.Equal(2, result.Value.DroppedBytes);
        Assert.True(result.Value.Truncated);
        Assert.Equal([1, 2, 3, 4, 5], replay.SelectMany(frame => frame.Bytes).ToArray());
        Assert.Equal(result.Value, exactReplay.Value);
        Assert.Equal(1, adapter.CaptureCount);
        Assert.Single(repository.Captures);
        Assert.Equal(1, artifacts.PutCount);
    }

    [Fact]
    public async Task Drop_and_disconnect_evidence_survives_empty_frame_replay()
    {
        var adapter = new FakeTransport([
            new(TimeSpan.FromMilliseconds(1), ReadOnlyMemory<byte>.Empty, 7, true),
        ]);
        await using var provider = Services(adapter).BuildServiceProvider();
        var service = provider.GetRequiredService<ISerialCaptureService>();

        var result = await service.CaptureAsync(CaptureRequest(DeviceCapability.Capture), CancellationToken.None);
        var replay = await ReadReplayAsync(service, result.Value);

        Assert.True(result.IsSuccess, result.Failure?.Message);
        Assert.Equal(0, result.Value.CapturedBytes);
        Assert.Equal(7, result.Value.DroppedBytes);
        Assert.True(result.Value.Disconnected);
        var frame = Assert.Single(replay);
        Assert.Empty(frame.Bytes);
        Assert.Equal(7, frame.DroppedBefore);
        Assert.True(frame.DisconnectedAfter);
    }

    [Fact]
    public async Task Capture_read_and_write_require_independent_exact_grants()
    {
        var adapter = new FakeTransport([]) { ReadChunk = new(TimeSpan.Zero, new byte[] { 9 }, 0, false) };
        await using var provider = Services(adapter).BuildServiceProvider();
        var sessions = provider.GetRequiredService<ISerialSessionService>();
        var device = Device();
        var captureOnly = Grant(DeviceCapability.Capture);

        var deniedRead = await sessions.ReadAsync(
            new(device, SerialProfile.ConservativeDefault, captureOnly, 16), CancellationToken.None);
        var deniedWrite = await sessions.WriteAsync(
            new(device, SerialProfile.ConservativeDefault, captureOnly, [8, 7]), CancellationToken.None);
        var allowedRead = await sessions.ReadAsync(
            new(device, SerialProfile.ConservativeDefault, Grant(DeviceCapability.Read), 16), CancellationToken.None);
        var allowedWrite = await sessions.WriteAsync(
            new(device, SerialProfile.ConservativeDefault, Grant(DeviceCapability.Write), [8, 7]), CancellationToken.None);
        adapter.ConfirmedWriteLength = 1;
        var partialWrite = await sessions.WriteAsync(
            new(device, SerialProfile.ConservativeDefault, Grant(DeviceCapability.Write), [8, 7]), CancellationToken.None);

        Assert.Equal(FailureCode.PolicyDenied, deniedRead.Failure?.Code);
        Assert.Equal(FailureCode.PolicyDenied, deniedWrite.Failure?.Code);
        Assert.True(allowedRead.IsSuccess);
        Assert.True(allowedWrite.IsSuccess);
        Assert.Equal(1, adapter.ReadCount);
        Assert.Equal(2, adapter.WriteCount);
        Assert.Equal(2, allowedWrite.Value.ByteCount);
        Assert.Equal(FailureCode.RecoverableExternalFailure, partialWrite.Failure?.Code);
    }

    [Fact]
    public async Task Missing_production_transport_fails_typed_without_opening_a_device()
    {
        var services = BaseServices(new FakeCaptureRepository(), new MemoryArtifactStore());
        services.AddAgentForgeDevices();
        await using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<ISerialCaptureService>()
            .CaptureAsync(CaptureRequest(DeviceCapability.Capture), CancellationToken.None);

        Assert.Equal(FailureCode.UnsupportedCapability, result.Failure?.Code);
    }

    [Fact]
    public async Task Replay_rejects_tampered_artifact_content()
    {
        var artifacts = new MemoryArtifactStore();
        await using var provider = Services(new FakeTransport([
            new(TimeSpan.Zero, new byte[] { 1, 2 }, 0, false),
        ]), artifacts: artifacts).BuildServiceProvider();
        var service = provider.GetRequiredService<ISerialCaptureService>();
        var result = await service.CaptureAsync(CaptureRequest(DeviceCapability.Capture), CancellationToken.None);
        artifacts.Tamper(result.Value.Artifact.ContentHash);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await ReadReplayAsync(service, result.Value));
    }

    private static ServiceCollection Services(
        FakeTransport adapter,
        FakeCaptureRepository? repository = null,
        MemoryArtifactStore? artifacts = null)
    {
        var services = BaseServices(repository ?? new FakeCaptureRepository(), artifacts ?? new MemoryArtifactStore());
        services.AddSingleton<ISerialTransportCatalog>(new SerialTransportCatalog([adapter]));
        services.AddAgentForgeDevices();
        return services;
    }

    private static ServiceCollection BaseServices(FakeCaptureRepository repository, MemoryArtifactStore artifacts)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IClock>(new FixedClock());
        services.AddSingleton<ISerialCaptureRepository>(repository);
        services.AddSingleton<IArtifactStore>(artifacts);
        services.AddSingleton<IAuditRecorder, FakeAudit>();
        services.AddSingleton<IUnitOfWork, SuccessfulUnitOfWork>();
        return services;
    }

    private static CreateSerialCaptureRequest CaptureRequest(DeviceCapability capability, int maximumBytes = 1024)
    {
        var device = Device();
        return new(new SerialCaptureId(Guid.Parse("331f6f2e-ae97-47f3-a482-f665a92343e1")),
            new InstallationId(Guid.Parse("56c5dd2d-cfb9-4b30-b767-37e99f0f4c5e")),
            new AgentIdentityId(Guid.Parse("18de5f49-6527-4e2f-825e-f75100e3f09e")), device,
            SerialProfile.ConservativeDefault, Grant(capability), maximumBytes, TimeSpan.FromSeconds(10),
            "capture-001", new ActorId("operator"), new CorrelationId("serial-capture"), null);
    }

    private static SerialDeviceDescriptor Device()
    {
        var id = SerialDeviceRecordValidator.PhysicalIdFromEvidence("windows", "usb:1234:5678:serial-one");
        return new(id, "COM9", "windows", "1234", "5678", "serial-one", "usb:1234:5678:serial-one",
            SerialDeviceReadiness.Ready, null, Now, Hash(new byte[] { 1 }));
    }

    private static DeviceCapabilityGrant Grant(DeviceCapability capability) =>
        new(Device().PhysicalId, new[] { capability }.ToImmutableSortedSet(), Now.AddMinutes(10), Hash(new byte[] { (byte)capability }));

    private static async Task<IReadOnlyList<SerialCaptureFrame>> ReadReplayAsync(
        ISerialCaptureService service, SerialCaptureRecord capture)
    {
        var frames = new List<SerialCaptureFrame>();
        await foreach (var frame in service.ReplayAsync(capture, CancellationToken.None)) frames.Add(frame);
        return frames;
    }

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";

    private sealed class FixedClock : IClock { public DateTimeOffset UtcNow => Now; }

    private sealed class SuccessfulUnitOfWork : IUnitOfWork
    {
        public Task<CommitResult> CommitAsync(CancellationToken cancellationToken) =>
            Task.FromResult(CommitResult.Success(1));
    }

    private sealed class FakeAudit : IAuditRecorder
    {
        public Task<AuditRecordResult> RecordAsync(AuditRecordRequest request, CancellationToken cancellationToken)
        {
            var record = new AuditEventRecord(Guid.NewGuid(), 1, Now, request.InstallationId, request.ActorId,
                request.CorrelationId, request.CausationId, request.OperationType, request.Outcome,
                RedactedData.Empty, RedactedData.Empty, null, new string('0', 64), new string('1', 64));
            return Task.FromResult(new AuditRecordResult(record, 0, 0));
        }
    }

    private sealed class FakeCaptureRepository : ISerialCaptureRepository
    {
        public List<SerialCaptureRecord> Captures { get; } = [];
        public ValueTask<SerialCaptureRecord?> FindByIdAsync(SerialCaptureId id, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Captures.SingleOrDefault(item => item.Id == id));
        public ValueTask<SerialCaptureRecord?> FindByIdempotencyKeyAsync(
            InstallationId installationId, string idempotencyKey, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Captures.SingleOrDefault(item => item.InstallationId == installationId && item.IdempotencyKey == idempotencyKey));
        public ValueTask AddAsync(SerialCaptureRecord capture, CancellationToken cancellationToken)
        {
            Captures.Add(capture);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MemoryArtifactStore : IArtifactStore
    {
        private readonly Dictionary<string, byte[]> _content = new(StringComparer.Ordinal);
        public int PutCount { get; private set; }
        public async Task<ArtifactReference> PutAsync(Stream content, string mediaType, CancellationToken cancellationToken)
        {
            using var memory = new MemoryStream();
            await content.CopyToAsync(memory, cancellationToken);
            var bytes = memory.ToArray();
            var hash = Hash(bytes);
            _content[hash] = bytes;
            PutCount++;
            return new(hash, bytes.Length, mediaType, Now);
        }
        public Task<Stream> OpenReadAsync(ArtifactReference artifact, CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new MemoryStream(_content[artifact.ContentHash], writable: false));
        public void Tamper(string hash) => _content[hash][0] ^= 0xff;
    }

    private sealed class FakeTransport(IReadOnlyList<SerialTransportChunk> chunks) : ISerialTransportAdapter
    {
        public string AdapterId => "fake-serial";
        public int CaptureCount { get; private set; }
        public int ReadCount { get; private set; }
        public int WriteCount { get; private set; }
        public int? ConfirmedWriteLength { get; set; }
        public SerialTransportChunk ReadChunk { get; init; } = new(TimeSpan.Zero, ReadOnlyMemory<byte>.Empty, 0, false);
        public bool Supports(string platform) => platform == "windows";
        public async IAsyncEnumerable<SerialTransportChunk> CaptureAsync(
            SerialTransportRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            CaptureCount++;
            foreach (var chunk in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return chunk;
                await Task.Yield();
            }
        }
        public ValueTask<SerialTransportChunk> ReadAsync(
            SerialTransportRequest request, int maximumBytes, CancellationToken cancellationToken)
        {
            ReadCount++;
            return ValueTask.FromResult(ReadChunk);
        }
        public ValueTask<int> WriteAsync(
            SerialTransportRequest request, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            WriteCount++;
            return ValueTask.FromResult(ConfirmedWriteLength ?? bytes.Length);
        }
    }
}
