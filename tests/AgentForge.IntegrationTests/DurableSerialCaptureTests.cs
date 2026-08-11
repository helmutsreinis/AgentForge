using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using AgentForge.Abstractions.Agents;
using AgentForge.Abstractions.Auditing;
using AgentForge.Abstractions.Devices;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Persistence;
using AgentForge.Abstractions.Providers;
using AgentForge.Abstractions.Time;
using AgentForge.Devices;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Devices;
using AgentForge.Domain.Installations;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;
using AgentForge.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.IntegrationTests;

public sealed partial class PersistenceFoundationTests
{
    [Fact]
    public async Task Serial_capture_persists_restart_safe_artifact_audit_and_exact_replay()
    {
        const string database = "serial-capture.db";
        var transport = new DurableFakeTransport();
        await using var services = BuildServices(_directory, database, collection =>
        {
            var clock = new SerialCaptureClock();
            collection.AddSingleton<IClock>(clock);
            collection.AddSingleton<IIdentifierGenerator>(clock);
            collection.AddSingleton<ISerialTransportCatalog>(new SerialTransportCatalog([transport]));
            collection.AddAgentForgeDevices();
        });
        await using (var initialize = services.CreateAsyncScope())
            await initialize.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync(CancellationToken.None);

        var installationId = new InstallationId(Guid.Parse("fb35dbb2-125c-473e-98da-da2af6a1d982"));
        var providerId = new ProviderProfileId(Guid.Parse("794de1f1-2164-48a5-8cf8-a264de2578e7"));
        var agentId = new AgentIdentityId(Guid.Parse("6b170501-6674-49cf-beb7-8b33801f4753"));
        await using (var seed = services.CreateAsyncScope())
        {
            await seed.ServiceProvider.GetRequiredService<IInstallationRepository>().AddAsync(
                InstallationSnapshot.CreateUninitialized(installationId, Now, new ActorId("device-operator"), new CorrelationId("device-seed")),
                CancellationToken.None);
            await seed.ServiceProvider.GetRequiredService<IProviderProfileRepository>().AddAsync(
                CreateProviderProfile(installationId, providerId, "device"), CancellationToken.None);
            var candidate = CreateAgentCandidate(providerId);
            await seed.ServiceProvider.GetRequiredService<IAgentIdentityRepository>().AddAsync(new AgentIdentity(
                agentId, installationId, candidate.Name, candidate.Expertise, candidate.Mission,
                candidate.PreferredLanguage, candidate.TimeZone, candidate.ResponseStyle, candidate.DefaultWorkspace,
                candidate.ModelPolicy, candidate.MemoryPolicy, candidate.CapabilityPolicy, candidate.Budget,
                candidate.ChildLimits, candidate.LearningPolicy, 0, Now, Now, new ActorId("device-operator"),
                new CorrelationId("device-seed")), CancellationToken.None);
            Assert.True((await seed.ServiceProvider.GetRequiredService<IUnitOfWork>()
                .CommitAsync(CancellationToken.None)).Succeeded);
        }

        var deviceId = SerialDeviceRecordValidator.PhysicalIdFromEvidence("linux", "usb:0403:6001:durable-device");
        var device = new SerialDeviceDescriptor(deviceId, "/dev/ttyUSB2", "linux", "0403", "6001", "durable-device",
            "usb:0403:6001:durable-device", SerialDeviceReadiness.Ready, null, Now,
            "sha256:" + new string('d', 64));
        var request = new CreateSerialCaptureRequest(
            new SerialCaptureId(Guid.Parse("9e3d4f46-833f-49d4-a93e-edfc59efe8b2")), installationId, agentId, device,
            SerialProfile.ConservativeDefault,
            new DeviceCapabilityGrant(deviceId, new[] { DeviceCapability.Capture }.ToImmutableSortedSet(),
                Now.AddDays(1), "sha256:" + new string('e', 64)),
            1024, TimeSpan.FromSeconds(5), "durable-capture-001", new ActorId("device-operator"),
            new CorrelationId("device-capture"), null);

        SerialCaptureRecord capture;
        await using (var create = services.CreateAsyncScope())
        {
            var result = await create.ServiceProvider.GetRequiredService<ISerialCaptureService>()
                .CaptureAsync(request, CancellationToken.None);
            Assert.True(result.IsSuccess, result.Failure?.Message);
            capture = result.Value;
        }

        await using (var restart = services.CreateAsyncScope())
        {
            var repository = restart.ServiceProvider.GetRequiredService<ISerialCaptureRepository>();
            var persisted = await repository.FindByIdAsync(capture.Id, CancellationToken.None);
            Assert.Equal(capture, persisted);
            var frames = new List<SerialCaptureFrame>();
            await foreach (var frame in restart.ServiceProvider.GetRequiredService<ISerialCaptureService>()
                .ReplayAsync(persisted!, CancellationToken.None)) frames.Add(frame);
            Assert.Equal("RAW-SERIAL-EVIDENCE", System.Text.Encoding.ASCII.GetString(frames.SelectMany(item => item.Bytes).ToArray()));

            var replay = await restart.ServiceProvider.GetRequiredService<ISerialCaptureService>()
                .CaptureAsync(request, CancellationToken.None);
            Assert.True(replay.IsSuccess);
            Assert.Equal(capture, replay.Value);
            Assert.Equal(1, transport.CaptureCount);
            Assert.True((await restart.ServiceProvider.GetRequiredService<IAuditIntegrityVerifier>()
                .VerifyAsync(CancellationToken.None)).IsValid);
        }

        var databaseText = System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Path.Combine(_directory, database)));
        Assert.DoesNotContain("RAW-SERIAL-EVIDENCE", databaseText, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_directory, "artifacts", "sha256", capture.Artifact.ContentHash[7..9], capture.Artifact.ContentHash[7..])));
    }

    private sealed class SerialCaptureClock : IClock, IIdentifierGenerator
    {
        public DateTimeOffset UtcNow => Now;
        public Guid NewGuid() => Guid.NewGuid();
    }

    private sealed class DurableFakeTransport : ISerialTransportAdapter
    {
        public string AdapterId => "durable-fake";
        public int CaptureCount { get; private set; }
        public bool Supports(string platform) => platform == "linux";
        public async IAsyncEnumerable<SerialTransportChunk> CaptureAsync(
            SerialTransportRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            CaptureCount++;
            yield return new(TimeSpan.Zero, "RAW-SERIAL-"u8.ToArray(), 0, false);
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new(TimeSpan.FromMilliseconds(3), "EVIDENCE"u8.ToArray(), 3, true);
        }
        public ValueTask<SerialTransportChunk> ReadAsync(
            SerialTransportRequest request, int maximumBytes, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask<int> WriteAsync(
            SerialTransportRequest request, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
