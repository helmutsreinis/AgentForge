using System.Text.Json;
using AgentForge.Abstractions.Devices;
using AgentForge.Domain.Devices;
using AgentForge.Domain.Primitives;
using AgentForge.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Persistence;

internal sealed class SqliteSerialCaptureRepository(AgentForgeDbContext dbContext) : ISerialCaptureRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async ValueTask<SerialCaptureRecord?> FindByIdAsync(SerialCaptureId id, CancellationToken cancellationToken) =>
        Map(await dbContext.SerialCaptures.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == id.Value, cancellationToken));

    public async ValueTask<SerialCaptureRecord?> FindByIdempotencyKeyAsync(
        InstallationId installationId, string idempotencyKey, CancellationToken cancellationToken) =>
        Map(await dbContext.SerialCaptures.AsNoTracking().SingleOrDefaultAsync(item =>
            item.InstallationId == installationId.Value && item.IdempotencyKey == idempotencyKey, cancellationToken));

    public async ValueTask AddAsync(SerialCaptureRecord capture, CancellationToken cancellationToken)
    {
        if (!capture.IsValid()) throw new InvalidDataException("Serial capture failed repository validation.");
        await dbContext.SerialCaptures.AddAsync(new SerialCaptureEntity
        {
            Id = capture.Id.Value,
            InstallationId = capture.InstallationId.Value,
            AgentId = capture.AgentId.Value,
            PhysicalDeviceId = capture.PhysicalDeviceId.Value,
            ArtifactContentHash = capture.Artifact.ContentHash,
            StreamHash = capture.StreamHash,
            RequestHash = capture.RequestHash,
            IdempotencyKey = capture.IdempotencyKey,
            StartedAtUtcTicks = capture.StartedAtUtc.UtcTicks,
            Version = capture.Version,
            CaptureJson = JsonSerializer.Serialize(capture, JsonOptions),
        }, cancellationToken);
    }

    private static SerialCaptureRecord? Map(SerialCaptureEntity? entity)
    {
        if (entity is null) return null;
        var capture = JsonSerializer.Deserialize<SerialCaptureRecord>(entity.CaptureJson, JsonOptions)
            ?? throw new InvalidDataException("Persisted serial capture is empty.");
        return capture.IsValid() && capture.Id.Value == entity.Id &&
            capture.InstallationId.Value == entity.InstallationId && capture.AgentId.Value == entity.AgentId &&
            capture.PhysicalDeviceId.Value == entity.PhysicalDeviceId &&
            capture.Artifact.ContentHash == entity.ArtifactContentHash && capture.StreamHash == entity.StreamHash &&
            capture.RequestHash == entity.RequestHash && capture.IdempotencyKey == entity.IdempotencyKey &&
            capture.StartedAtUtc.UtcTicks == entity.StartedAtUtcTicks && capture.Version == entity.Version
            ? capture : throw new InvalidDataException("Persisted serial capture failed integrity validation.");
    }
}
