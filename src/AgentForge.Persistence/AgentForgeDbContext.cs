using AgentForge.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Persistence;

public sealed class AgentForgeDbContext(DbContextOptions<AgentForgeDbContext> options) : DbContext(options)
{
    internal DbSet<InstallationEntity> Installations => Set<InstallationEntity>();

    internal DbSet<AuditEventEntity> AuditEvents => Set<AuditEventEntity>();

    internal DbSet<ArtifactEntity> Artifacts => Set<ArtifactEntity>();

    internal DbSet<OutboxMessageEntity> OutboxMessages => Set<OutboxMessageEntity>();

    internal DbSet<ProviderProfileEntity> ProviderProfiles => Set<ProviderProfileEntity>();

    internal DbSet<AgentIdentityEntity> AgentIdentities => Set<AgentIdentityEntity>();

    internal DbSet<LocalAdministratorEntity> LocalAdministrators => Set<LocalAdministratorEntity>();

    internal DbSet<SetupProfileSnapshotEntity> SetupProfileSnapshots => Set<SetupProfileSnapshotEntity>();

    internal DbSet<CapabilityApprovalEntity> CapabilityApprovals => Set<CapabilityApprovalEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<InstallationEntity>(entity =>
        {
            entity.ToTable("installations");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.State).HasMaxLength(64).IsRequired();
            entity.Property(item => item.Version).IsConcurrencyToken();
            entity.Property(item => item.ActorId).HasMaxLength(256).IsRequired();
            entity.Property(item => item.CorrelationId).HasMaxLength(128).IsRequired();
            entity.Property(item => item.RecoveryReason).HasMaxLength(2048);
        });

        modelBuilder.Entity<AuditEventEntity>(entity =>
        {
            entity.ToTable("audit_events");
            entity.HasKey(item => item.EventId);
            entity.HasIndex(item => item.Sequence).IsUnique();
            entity.HasIndex(item => new { item.InstallationId, item.Sequence });
            entity.Property(item => item.ActorId).HasMaxLength(256).IsRequired();
            entity.Property(item => item.CorrelationId).HasMaxLength(128).IsRequired();
            entity.Property(item => item.CausationId).HasMaxLength(128);
            entity.Property(item => item.OperationType).HasMaxLength(256).IsRequired();
            entity.Property(item => item.Outcome).HasMaxLength(64).IsRequired();
            entity.Property(item => item.InputJson).IsRequired();
            entity.Property(item => item.OutputJson).IsRequired();
            entity.Property(item => item.ErrorClassification).HasMaxLength(256);
            entity.Property(item => item.PreviousHash).HasMaxLength(64).IsRequired();
            entity.Property(item => item.EventHash).HasMaxLength(64).IsRequired();
        });

        modelBuilder.Entity<ArtifactEntity>(entity =>
        {
            entity.ToTable("artifacts");
            entity.HasKey(item => item.ContentHash);
            entity.Property(item => item.ContentHash).HasMaxLength(71);
            entity.Property(item => item.MediaType).HasMaxLength(256).IsRequired();
            entity.Property(item => item.RelativePath).HasMaxLength(512).IsRequired();
        });

        modelBuilder.Entity<OutboxMessageEntity>(entity =>
        {
            entity.ToTable("outbox_messages");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.ProcessedAt, item.OccurredAt });
            entity.Property(item => item.MessageType).HasMaxLength(512).IsRequired();
            entity.Property(item => item.PayloadJson).IsRequired();
            entity.Property(item => item.Version).IsConcurrencyToken();
        });

        modelBuilder.Entity<ProviderProfileEntity>(entity =>
        {
            entity.ToTable("provider_profiles");
            entity.HasKey(item => item.Id);
            entity.HasOne<InstallationEntity>()
                .WithMany()
                .HasForeignKey(item => item.InstallationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(item => new { item.InstallationId, item.Name }).IsUnique();
            entity.Property(item => item.Name).HasMaxLength(128).IsRequired();
            entity.Property(item => item.ProviderType).HasMaxLength(64).IsRequired();
            entity.Property(item => item.Endpoint).HasMaxLength(2048).IsRequired();
            entity.Property(item => item.Model).HasMaxLength(256).IsRequired();
            entity.Property(item => item.SecretStore).HasMaxLength(128).IsRequired();
            entity.Property(item => item.SecretKey).HasMaxLength(512).IsRequired();
            entity.Property(item => item.EvidenceSource).HasMaxLength(256).IsRequired();
            entity.Property(item => item.Version).IsConcurrencyToken();
            entity.Property(item => item.ActorId).HasMaxLength(256).IsRequired();
            entity.Property(item => item.CorrelationId).HasMaxLength(128).IsRequired();
        });

        modelBuilder.Entity<AgentIdentityEntity>(entity =>
        {
            entity.ToTable("agent_identities");
            entity.HasKey(item => item.Id);
            entity.HasOne<InstallationEntity>()
                .WithMany()
                .HasForeignKey(item => item.InstallationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ProviderProfileEntity>()
                .WithMany()
                .HasForeignKey(item => item.PrimaryProviderProfileId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(item => new { item.InstallationId, item.Name }).IsUnique();
            entity.Property(item => item.Name).UseCollation("NOCASE").HasMaxLength(128).IsRequired();
            entity.Property(item => item.Expertise).HasMaxLength(512);
            entity.Property(item => item.Mission).HasMaxLength(4096);
            entity.Property(item => item.PreferredLanguage).HasMaxLength(35).IsRequired();
            entity.Property(item => item.TimeZone).HasMaxLength(128).IsRequired();
            entity.Property(item => item.ResponseStyle).HasMaxLength(512).IsRequired();
            entity.Property(item => item.DefaultWorkspace).HasMaxLength(1024);
            entity.Property(item => item.DataLocality).HasMaxLength(64).IsRequired();
            entity.Property(item => item.MemoryScope).HasMaxLength(64).IsRequired();
            entity.Property(item => item.NetworkPosture).HasMaxLength(64).IsRequired();
            entity.Property(item => item.ToolGrantsJson).IsRequired();
            entity.Property(item => item.SkillGrantsJson).IsRequired();
            entity.Property(item => item.LearningMode).HasMaxLength(64).IsRequired();
            entity.Property(item => item.MutableSkillScope).HasMaxLength(64).IsRequired();
            entity.Property(item => item.Version).IsConcurrencyToken();
            entity.Property(item => item.ActorId).HasMaxLength(256).IsRequired();
            entity.Property(item => item.CorrelationId).HasMaxLength(128).IsRequired();
        });

        modelBuilder.Entity<LocalAdministratorEntity>(entity =>
        {
            entity.ToTable("local_administrators");
            entity.HasKey(item => item.Id);
            entity.HasOne<InstallationEntity>()
                .WithMany()
                .HasForeignKey(item => item.InstallationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(item => item.InstallationId).IsUnique();
            entity.HasIndex(item => item.ActorId).IsUnique();
            entity.Property(item => item.ActorId).HasMaxLength(256).IsRequired();
            entity.Property(item => item.SecretStore).HasMaxLength(128).IsRequired();
            entity.Property(item => item.SecretKey).HasMaxLength(512).IsRequired();
            entity.Property(item => item.VerifierAlgorithm).HasMaxLength(64).IsRequired();
            entity.Property(item => item.VerifierSalt).HasMaxLength(128).IsRequired();
            entity.Property(item => item.Verifier).HasMaxLength(128).IsRequired();
            entity.Property(item => item.Version).IsConcurrencyToken();
            entity.Property(item => item.CorrelationId).HasMaxLength(128).IsRequired();
        });

        modelBuilder.Entity<SetupProfileSnapshotEntity>(entity =>
        {
            entity.ToTable("setup_profile_snapshots");
            entity.HasKey(item => item.Id);
            entity.HasOne<InstallationEntity>()
                .WithMany()
                .HasForeignKey(item => item.InstallationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ArtifactEntity>()
                .WithMany()
                .HasForeignKey(item => item.ArtifactContentHash)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(item => new { item.InstallationId, item.ProfileVersion, item.Kind });
            entity.Property(item => item.Kind).HasMaxLength(64).IsRequired();
            entity.Property(item => item.ArtifactContentHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.ArtifactMediaType).HasMaxLength(256).IsRequired();
            entity.Property(item => item.ActorId).HasMaxLength(256).IsRequired();
            entity.Property(item => item.CorrelationId).HasMaxLength(128).IsRequired();
        });

        modelBuilder.Entity<CapabilityApprovalEntity>(entity =>
        {
            entity.ToTable("capability_approvals");
            entity.HasKey(item => item.Id);
            entity.HasOne<InstallationEntity>()
                .WithMany()
                .HasForeignKey(item => item.InstallationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AgentIdentityEntity>()
                .WithMany()
                .HasForeignKey(item => item.AgentId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(item => new { item.InstallationId, item.IdempotencyKey }).IsUnique();
            entity.HasIndex(item => new { item.InstallationId, item.AgentId, item.RequestHash, item.CreatedAtUtcTicks });
            entity.Property(item => item.RequestActorId).HasMaxLength(256).IsRequired();
            entity.Property(item => item.CapabilityId).HasMaxLength(256).IsRequired();
            entity.Property(item => item.RiskClass).HasMaxLength(64).IsRequired();
            entity.Property(item => item.ToolId).HasMaxLength(256);
            entity.Property(item => item.ToolVersion).HasMaxLength(128);
            entity.Property(item => item.ParametersHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.TargetKind).HasMaxLength(64).IsRequired();
            entity.Property(item => item.TargetHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.WorkspaceHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.RequestHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.Disposition).HasMaxLength(64).IsRequired();
            entity.Property(item => item.State).HasMaxLength(64).IsRequired();
            entity.Property(item => item.DecidedBy).HasMaxLength(256).IsRequired();
            entity.Property(item => item.CorrelationId).HasMaxLength(128).IsRequired();
            entity.Property(item => item.PreviewHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.IdempotencyKey).HasMaxLength(256).IsRequired();
            entity.Property(item => item.Version).IsConcurrencyToken();
        });
    }
}
