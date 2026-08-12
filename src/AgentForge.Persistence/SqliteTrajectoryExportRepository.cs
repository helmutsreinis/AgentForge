using System.Text.Json;
using AgentForge.Abstractions.Auditing;
using AgentForge.Domain.Auditing;
using AgentForge.Domain.Primitives;
using AgentForge.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Persistence;

internal sealed class SqliteTrajectoryExportRepository(AgentForgeDbContext dbContext)
    : ITrajectoryExportRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<TrajectoryExportReceipt?> GetByIdempotencyAsync(
        InstallationId installationId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.TrajectoryExports.AsNoTracking().SingleOrDefaultAsync(item =>
            item.InstallationId == installationId.Value && item.IdempotencyKey == idempotencyKey,
            cancellationToken);
        return entity is null ? null : Deserialize(entity.ResultJson);
    }

    public async ValueTask AddAsync(
        TrajectoryExportReceipt receipt,
        CancellationToken cancellationToken)
    {
        await dbContext.TrajectoryExports.AddAsync(new TrajectoryExportEntity
        {
            Id = receipt.ExportId,
            InstallationId = receipt.InstallationId.Value,
            IdempotencyKey = receipt.IdempotencyKey,
            RequestHash = receipt.RequestHash,
            ArtifactContentHash = receipt.Artifact.ContentHash,
            ResultJson = JsonSerializer.Serialize(receipt, JsonOptions),
            CreatedAtUtcTicks = receipt.CreatedAt.UtcTicks,
        }, cancellationToken);
    }

    private static TrajectoryExportReceipt Deserialize(string json)
    {
        var receipt = JsonSerializer.Deserialize<TrajectoryExportReceipt>(json, JsonOptions);
        if (!TrajectoryExportValidation.ValidateReceipt(receipt).IsSuccess)
            throw new InvalidDataException("Stored trajectory export receipt is invalid.");
        return receipt!;
    }
}
