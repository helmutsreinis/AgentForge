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

    internal DbSet<ToolInvocationEntity> ToolInvocations => Set<ToolInvocationEntity>();

    internal DbSet<ModelRunEntity> ModelRuns => Set<ModelRunEntity>();

    internal DbSet<ModelRunAttemptEntity> ModelRunAttempts => Set<ModelRunAttemptEntity>();

    internal DbSet<ModelBudgetLedgerEntity> ModelBudgetLedgers => Set<ModelBudgetLedgerEntity>();

    internal DbSet<ModelProviderHealthEntity> ModelProviderHealth => Set<ModelProviderHealthEntity>();

    internal DbSet<AgentLoopSnapshotEntity> AgentLoopSnapshots => Set<AgentLoopSnapshotEntity>();

    internal DbSet<OrchestrationTaskSnapshotEntity> OrchestrationTaskSnapshots =>
        Set<OrchestrationTaskSnapshotEntity>();

    internal DbSet<DelegationGrantEntity> DelegationGrants => Set<DelegationGrantEntity>();

    internal DbSet<ScheduleSnapshotEntity> ScheduleSnapshots => Set<ScheduleSnapshotEntity>();

    internal DbSet<SkillVersionEntity> SkillVersions => Set<SkillVersionEntity>();

    internal DbSet<SkillActiveVersionEntity> SkillActiveVersions => Set<SkillActiveVersionEntity>();

    internal DbSet<SkillProposalSnapshotEntity> SkillProposalSnapshots => Set<SkillProposalSnapshotEntity>();

    internal DbSet<SkillRunSnapshotEntity> SkillRunSnapshots => Set<SkillRunSnapshotEntity>();

    internal DbSet<CodingSessionSnapshotEntity> CodingSessionSnapshots => Set<CodingSessionSnapshotEntity>();

    internal DbSet<MemoryEntryEntity> MemoryEntries => Set<MemoryEntryEntity>();

    internal DbSet<ChannelIdentityBindingEntity> ChannelIdentityBindings => Set<ChannelIdentityBindingEntity>();

    internal DbSet<ChannelInboundMessageEntity> ChannelInboundMessages => Set<ChannelInboundMessageEntity>();

    internal DbSet<ChannelDeliveryEntity> ChannelDeliveries => Set<ChannelDeliveryEntity>();

    internal DbSet<SerialCaptureEntity> SerialCaptures => Set<SerialCaptureEntity>();

    internal DbSet<DecoderProposalSnapshotEntity> DecoderProposalSnapshots => Set<DecoderProposalSnapshotEntity>();

    internal DbSet<DecoderActiveVersionEntity> DecoderActiveVersions => Set<DecoderActiveVersionEntity>();

    internal DbSet<LearningSignalEntity> LearningSignals => Set<LearningSignalEntity>();

    internal DbSet<LearningCandidateSnapshotEntity> LearningCandidateSnapshots =>
        Set<LearningCandidateSnapshotEntity>();

    internal DbSet<SkillBundleEntity> SkillBundles => Set<SkillBundleEntity>();

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

        modelBuilder.Entity<SerialCaptureEntity>(entity =>
        {
            entity.ToTable("serial_captures");
            entity.HasKey(item => item.Id);
            entity.HasOne<InstallationEntity>().WithMany().HasForeignKey(item => item.InstallationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AgentIdentityEntity>().WithMany().HasForeignKey(item => item.AgentId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ArtifactEntity>().WithMany().HasForeignKey(item => item.ArtifactContentHash)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(item => new { item.InstallationId, item.IdempotencyKey }).IsUnique();
            entity.HasIndex(item => new { item.InstallationId, item.PhysicalDeviceId, item.StartedAtUtcTicks });
            entity.Property(item => item.PhysicalDeviceId).HasMaxLength(78).IsRequired();
            entity.Property(item => item.ArtifactContentHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.StreamHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.RequestHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.IdempotencyKey).HasMaxLength(128).IsRequired();
            entity.Property(item => item.Version).IsConcurrencyToken();
            entity.Property(item => item.CaptureJson).IsRequired();
        });

        modelBuilder.Entity<DecoderProposalSnapshotEntity>(entity =>
        {
            entity.ToTable("decoder_proposal_snapshots");
            entity.HasKey(item => new { item.ProposalId, item.Version });
            entity.HasOne<InstallationEntity>().WithMany().HasForeignKey(item => item.InstallationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(item => new { item.InstallationId, item.DecoderId, item.UpdatedAtUtcTicks });
            entity.Property(item => item.DecoderId).HasMaxLength(128).IsRequired();
            entity.Property(item => item.State).HasMaxLength(32).IsRequired();
            entity.Property(item => item.CandidateHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.BaselineHash).HasMaxLength(71);
            entity.Property(item => item.PreviousSnapshotHash).HasMaxLength(64).IsRequired();
            entity.Property(item => item.SnapshotHash).HasMaxLength(64).IsRequired();
            entity.Property(item => item.SnapshotJson).IsRequired();
        });

        modelBuilder.Entity<DecoderActiveVersionEntity>(entity =>
        {
            entity.ToTable("decoder_active_versions");
            entity.HasKey(item => new { item.InstallationId, item.DecoderId });
            entity.HasOne<InstallationEntity>().WithMany().HasForeignKey(item => item.InstallationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.Property(item => item.DecoderId).HasMaxLength(128).IsRequired();
            entity.Property(item => item.CandidateHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.Version).IsConcurrencyToken();
        });

        modelBuilder.Entity<LearningSignalEntity>(entity =>
        {
            entity.ToTable("learning_signals");
            entity.HasKey(item => item.Id);
            entity.HasOne<InstallationEntity>().WithMany().HasForeignKey(item => item.InstallationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(item => new { item.InstallationId, item.CapturedAtUtcTicks });
            entity.Property(item => item.Kind).HasMaxLength(32).IsRequired();
            entity.Property(item => item.Action).HasMaxLength(32).IsRequired();
            entity.Property(item => item.SignalHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.ClassificationHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.SignalJson).IsRequired();
            entity.Property(item => item.ClassificationJson).IsRequired();
        });

        modelBuilder.Entity<LearningCandidateSnapshotEntity>(entity =>
        {
            entity.ToTable("learning_candidate_snapshots");
            entity.HasKey(item => new { item.Id, item.Version });
            entity.HasOne<InstallationEntity>().WithMany().HasForeignKey(item => item.InstallationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(item => new { item.InstallationId, item.SkillId, item.UpdatedAtUtcTicks });
            entity.Property(item => item.SkillId).HasMaxLength(256).IsRequired();
            entity.Property(item => item.State).HasMaxLength(32).IsRequired();
            entity.Property(item => item.CandidatePackageHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.BaselinePackageHash).HasMaxLength(71);
            entity.Property(item => item.PreviousSnapshotHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.SnapshotHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.SnapshotJson).IsRequired();
        });

        modelBuilder.Entity<SkillBundleEntity>(entity =>
        {
            entity.ToTable("skill_bundles");
            entity.HasKey(item => new { item.Id, item.Version });
            entity.Property(item => item.Id).HasMaxLength(256).IsRequired();
            entity.Property(item => item.Version).HasMaxLength(128).IsRequired();
            entity.Property(item => item.DefinitionHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.SourceSignalHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.DefinitionJson).IsRequired();
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
            entity.Property(item => item.ToolDescriptorHash).HasMaxLength(71);
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

        modelBuilder.Entity<ToolInvocationEntity>(entity =>
        {
            entity.ToTable("tool_invocations");
            entity.HasKey(item => item.Id);
            entity.HasOne<InstallationEntity>()
                .WithMany()
                .HasForeignKey(item => item.InstallationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AgentIdentityEntity>()
                .WithMany()
                .HasForeignKey(item => item.AgentId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<CapabilityApprovalEntity>()
                .WithMany()
                .HasForeignKey(item => item.ApprovalId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(item => new { item.InstallationId, item.IdempotencyKey }).IsUnique();
            entity.HasIndex(item => new { item.InstallationId, item.AgentId, item.CreatedAtUtcTicks });
            entity.Property(item => item.ActorId).HasMaxLength(256).IsRequired();
            entity.Property(item => item.ToolId).HasMaxLength(256).IsRequired();
            entity.Property(item => item.ToolVersion).HasMaxLength(128).IsRequired();
            entity.Property(item => item.ToolDescriptorHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.CapabilityId).HasMaxLength(256).IsRequired();
            entity.Property(item => item.RiskClass).HasMaxLength(64).IsRequired();
            entity.Property(item => item.ParametersHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.TargetKind).HasMaxLength(64).IsRequired();
            entity.Property(item => item.TargetHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.WorkspaceHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.RequestHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.State).HasMaxLength(64).IsRequired();
            entity.Property(item => item.IdempotencyKey).HasMaxLength(256).IsRequired();
            entity.Property(item => item.CorrelationId).HasMaxLength(128).IsRequired();
            entity.Property(item => item.CausationId).HasMaxLength(128);
            entity.Property(item => item.StandardOutputHash).HasMaxLength(71);
            entity.Property(item => item.StandardErrorHash).HasMaxLength(71);
            entity.Property(item => item.FailureCode).HasMaxLength(64);
            entity.Property(item => item.Version).IsConcurrencyToken();
        });

        modelBuilder.Entity<ModelRunEntity>(entity =>
        {
            entity.ToTable("model_runs");
            entity.HasKey(item => item.Id);
            entity.HasOne<InstallationEntity>()
                .WithMany()
                .HasForeignKey(item => item.InstallationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AgentIdentityEntity>()
                .WithMany()
                .HasForeignKey(item => item.AgentId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ProviderProfileEntity>()
                .WithMany()
                .HasForeignKey(item => item.ProviderProfileId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(item => new { item.InstallationId, item.IdempotencyKey }).IsUnique();
            entity.HasIndex(item => new { item.InstallationId, item.AgentId, item.CreatedAtUtcTicks });
            entity.Property(item => item.ProviderType).HasMaxLength(64).IsRequired();
            entity.Property(item => item.Model).HasMaxLength(256).IsRequired();
            entity.Property(item => item.AttemptedProfileIdsJson).HasMaxLength(512).IsRequired();
            entity.Property(item => item.RequiredCapabilitiesJson).HasMaxLength(1024).IsRequired();
            entity.Property(item => item.SelectionEvidenceHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.PlanEvidenceHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.PreparedInputHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.HealthEvidenceHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.ContextPreparationPolicy).HasMaxLength(128).IsRequired();
            entity.Property(item => item.AdmissionRequestHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.LeaseOwner).HasMaxLength(256);
            entity.Property(item => item.LeaseTokenHash).HasMaxLength(71);
            entity.Property(item => item.EventStreamHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.Currency).HasMaxLength(3);
            entity.Property(item => item.State).HasMaxLength(64).IsRequired();
            entity.Property(item => item.ActorId).HasMaxLength(256).IsRequired();
            entity.Property(item => item.IdempotencyKey).HasMaxLength(256).IsRequired();
            entity.Property(item => item.CorrelationId).HasMaxLength(128).IsRequired();
            entity.Property(item => item.CausationId).HasMaxLength(128);
            entity.Property(item => item.FinishReason).HasMaxLength(64);
            entity.Property(item => item.FailureCode).HasMaxLength(64);
            entity.Property(item => item.Version).IsConcurrencyToken();
        });

        modelBuilder.Entity<ModelRunAttemptEntity>(entity =>
        {
            entity.ToTable("model_run_attempts");
            entity.HasKey(item => item.Id);
            entity.HasOne<ModelRunEntity>()
                .WithMany()
                .HasForeignKey(item => item.RunId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ProviderProfileEntity>()
                .WithMany()
                .HasForeignKey(item => item.ProviderProfileId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(item => new { item.RunId, item.Sequence }).IsUnique();
            entity.Property(item => item.ProviderType).HasMaxLength(64).IsRequired();
            entity.Property(item => item.Model).HasMaxLength(256).IsRequired();
            entity.Property(item => item.RequiredCapabilitiesJson).HasMaxLength(1024).IsRequired();
            entity.Property(item => item.SelectionEvidenceHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.PlanEvidenceHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.EventStreamHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.State).HasMaxLength(64).IsRequired();
            entity.Property(item => item.Currency).HasMaxLength(3);
            entity.Property(item => item.FinishReason).HasMaxLength(64);
            entity.Property(item => item.FailureCode).HasMaxLength(64);
            entity.Property(item => item.Version).IsConcurrencyToken();
        });

        modelBuilder.Entity<ModelBudgetLedgerEntity>(entity =>
        {
            entity.ToTable("model_budget_ledgers");
            entity.HasKey(item => item.AgentId);
            entity.HasOne<InstallationEntity>()
                .WithMany()
                .HasForeignKey(item => item.InstallationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AgentIdentityEntity>()
                .WithMany()
                .HasForeignKey(item => item.AgentId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(item => new { item.InstallationId, item.AgentId }).IsUnique();
            entity.Property(item => item.Version).IsConcurrencyToken();
        });

        modelBuilder.Entity<ModelProviderHealthEntity>(entity =>
        {
            entity.ToTable("model_provider_health");
            entity.HasKey(item => item.ProfileId);
            entity.HasOne<InstallationEntity>()
                .WithMany()
                .HasForeignKey(item => item.InstallationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ProviderProfileEntity>()
                .WithMany()
                .HasForeignKey(item => item.ProfileId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ModelRunEntity>()
                .WithMany()
                .HasForeignKey(item => item.LastRunId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ModelRunAttemptEntity>()
                .WithMany()
                .HasForeignKey(item => item.LastAttemptId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(item => new { item.InstallationId, item.UpdatedAtUtcTicks });
            entity.Property(item => item.Status).HasMaxLength(64).IsRequired();
            entity.Property(item => item.Source).HasMaxLength(64).IsRequired();
            entity.Property(item => item.EvidenceCode).HasMaxLength(64).IsRequired();
            entity.Property(item => item.ActorId).HasMaxLength(256).IsRequired();
            entity.Property(item => item.CorrelationId).HasMaxLength(128).IsRequired();
            entity.Property(item => item.CausationId).HasMaxLength(128);
            entity.Property(item => item.Version).IsConcurrencyToken();
        });

        modelBuilder.Entity<AgentLoopSnapshotEntity>(entity =>
        {
            entity.ToTable("agent_loop_snapshots");
            entity.HasKey(item => new { item.LoopId, item.Sequence });
            entity.HasOne<InstallationEntity>()
                .WithMany()
                .HasForeignKey(item => item.InstallationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AgentIdentityEntity>()
                .WithMany()
                .HasForeignKey(item => item.AgentId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(item => new { item.InstallationId, item.IdempotencyKey, item.Sequence }).IsUnique();
            entity.HasIndex(item => new { item.InstallationId, item.AgentId, item.UpdatedAtUtcTicks });
            entity.Property(item => item.Phase).HasMaxLength(64).IsRequired();
            entity.Property(item => item.State).HasMaxLength(64).IsRequired();
            entity.Property(item => item.InitialStateHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.LastProgressEvidenceHash).HasMaxLength(71);
            entity.Property(item => item.StepEvidenceHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.PreviousSnapshotHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.SnapshotHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.ActorId).HasMaxLength(256).IsRequired();
            entity.Property(item => item.IdempotencyKey).HasMaxLength(256).IsRequired();
            entity.Property(item => item.CorrelationId).HasMaxLength(128).IsRequired();
            entity.Property(item => item.CausationId).HasMaxLength(128);
            entity.Property(item => item.FailureCode).HasMaxLength(64);
        });

        modelBuilder.Entity<OrchestrationTaskSnapshotEntity>(entity =>
        {
            entity.ToTable("orchestration_task_snapshots");
            entity.HasKey(item => new { item.TaskId, item.Version });
            entity.HasOne<InstallationEntity>()
                .WithMany()
                .HasForeignKey(item => item.InstallationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AgentIdentityEntity>()
                .WithMany()
                .HasForeignKey(item => item.AgentId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(item => new { item.InstallationId, item.IdempotencyKey, item.Version }).IsUnique();
            entity.HasIndex(item => new { item.InstallationId, item.AgentId, item.UpdatedAtUtcTicks });
            entity.Property(item => item.State).HasMaxLength(64).IsRequired();
            entity.Property(item => item.PreviousSnapshotHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.SnapshotHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.SnapshotJson).IsRequired();
            entity.Property(item => item.ActorId).HasMaxLength(256).IsRequired();
            entity.Property(item => item.IdempotencyKey).HasMaxLength(256).IsRequired();
            entity.Property(item => item.CorrelationId).HasMaxLength(128).IsRequired();
            entity.Property(item => item.CausationId).HasMaxLength(128);
        });

        modelBuilder.Entity<DelegationGrantEntity>(entity =>
        {
            entity.ToTable("delegation_grants");
            entity.HasKey(item => item.Id);
            entity.HasOne<InstallationEntity>()
                .WithMany()
                .HasForeignKey(item => item.InstallationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AgentIdentityEntity>()
                .WithMany()
                .HasForeignKey(item => item.ParentAgentId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AgentIdentityEntity>()
                .WithMany()
                .HasForeignKey(item => item.ChildAgentId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(item => new { item.InstallationId, item.ParentTaskId, item.IssuedAtUtcTicks });
            entity.Property(item => item.GrantHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.GrantJson).IsRequired();
            entity.Property(item => item.ActorId).HasMaxLength(256).IsRequired();
            entity.Property(item => item.CorrelationId).HasMaxLength(128).IsRequired();
            entity.Property(item => item.CausationId).HasMaxLength(128);
        });

        modelBuilder.Entity<ScheduleSnapshotEntity>(entity =>
        {
            entity.ToTable("schedule_snapshots");
            entity.HasKey(item => new { item.ScheduleId, item.Version });
            entity.HasOne<InstallationEntity>()
                .WithMany()
                .HasForeignKey(item => item.InstallationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AgentIdentityEntity>()
                .WithMany()
                .HasForeignKey(item => item.AgentId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(item => new { item.InstallationId, item.IdempotencyKey, item.Version }).IsUnique();
            entity.HasIndex(item => new { item.State, item.NextDueAtUtcTicks });
            entity.Property(item => item.State).HasMaxLength(64).IsRequired();
            entity.Property(item => item.PreviousSnapshotHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.SnapshotHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.SnapshotJson).IsRequired();
            entity.Property(item => item.ActorId).HasMaxLength(256).IsRequired();
            entity.Property(item => item.IdempotencyKey).HasMaxLength(256).IsRequired();
            entity.Property(item => item.CorrelationId).HasMaxLength(128).IsRequired();
            entity.Property(item => item.CausationId).HasMaxLength(128);
        });

        modelBuilder.Entity<SkillVersionEntity>(entity =>
        {
            entity.ToTable("skill_versions");
            entity.HasKey(item => new { item.InstallationId, item.SkillId, item.Version });
            entity.HasOne<InstallationEntity>().WithMany().HasForeignKey(item => item.InstallationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ArtifactEntity>().WithMany().HasForeignKey(item => item.ArtifactContentHash)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(item => new { item.InstallationId, item.SkillId, item.Status });
            entity.Property(item => item.SkillId).HasMaxLength(256).IsRequired();
            entity.Property(item => item.Version).HasMaxLength(128).IsRequired();
            entity.Property(item => item.ArtifactContentHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.PackageHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.ManifestHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.Status).HasMaxLength(64).IsRequired();
            entity.Property(item => item.Provenance).HasMaxLength(64).IsRequired();
            entity.Property(item => item.DescriptorJson).IsRequired();
            entity.Property(item => item.RecordVersion).IsConcurrencyToken();
            entity.Property(item => item.ActorId).HasMaxLength(256).IsRequired();
            entity.Property(item => item.CorrelationId).HasMaxLength(128).IsRequired();
        });

        modelBuilder.Entity<SkillActiveVersionEntity>(entity =>
        {
            entity.ToTable("skill_active_versions");
            entity.HasKey(item => new { item.InstallationId, item.SkillId });
            entity.HasOne<SkillVersionEntity>().WithMany()
                .HasForeignKey(item => new { item.InstallationId, item.SkillId, item.Version })
                .OnDelete(DeleteBehavior.Restrict);
            entity.Property(item => item.SkillId).HasMaxLength(256).IsRequired();
            entity.Property(item => item.Version).HasMaxLength(128).IsRequired();
        });

        modelBuilder.Entity<SkillProposalSnapshotEntity>(entity =>
        {
            entity.ToTable("skill_proposal_snapshots");
            entity.HasKey(item => new { item.ProposalId, item.Version });
            entity.HasOne<InstallationEntity>().WithMany().HasForeignKey(item => item.InstallationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(item => new { item.InstallationId, item.SkillId, item.UpdatedAtUtcTicks });
            entity.Property(item => item.SkillId).HasMaxLength(256).IsRequired();
            entity.Property(item => item.State).HasMaxLength(64).IsRequired();
            entity.Property(item => item.PreviousSnapshotHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.SnapshotHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.ProposalJson).IsRequired();
            entity.Property(item => item.CorrelationId).HasMaxLength(128).IsRequired();
        });

        modelBuilder.Entity<SkillRunSnapshotEntity>(entity =>
        {
            entity.ToTable("skill_run_snapshots");
            entity.HasKey(item => item.Id);
            entity.HasOne<InstallationEntity>().WithMany().HasForeignKey(item => item.InstallationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(item => new { item.InstallationId, item.IdempotencyKey }).IsUnique();
            entity.Property(item => item.IdempotencyKey).HasMaxLength(256).IsRequired();
            entity.Property(item => item.SnapshotHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.SnapshotJson).IsRequired();
            entity.Property(item => item.ActorId).HasMaxLength(256).IsRequired();
            entity.Property(item => item.CorrelationId).HasMaxLength(128).IsRequired();
            entity.Property(item => item.CausationId).HasMaxLength(128);
        });

        modelBuilder.Entity<CodingSessionSnapshotEntity>(entity =>
        {
            entity.ToTable("coding_session_snapshots");
            entity.HasKey(item => new { item.SessionId, item.Version });
            entity.HasOne<InstallationEntity>().WithMany().HasForeignKey(item => item.InstallationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(item => new { item.InstallationId, item.IdempotencyKey, item.Version }).IsUnique();
            entity.HasIndex(item => new { item.InstallationId, item.AgentId, item.UpdatedAtUtcTicks });
            entity.Property(item => item.State).HasMaxLength(64).IsRequired();
            entity.Property(item => item.PreviousSnapshotHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.SnapshotHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.SnapshotJson).IsRequired();
            entity.Property(item => item.ActorId).HasMaxLength(256).IsRequired();
            entity.Property(item => item.IdempotencyKey).HasMaxLength(256).IsRequired();
            entity.Property(item => item.CorrelationId).HasMaxLength(128).IsRequired();
            entity.Property(item => item.CausationId).HasMaxLength(128);
        });

        modelBuilder.Entity<MemoryEntryEntity>(entity =>
        {
            entity.ToTable("memory_entries");
            entity.HasKey(item => item.Id);
            entity.HasOne<InstallationEntity>().WithMany().HasForeignKey(item => item.InstallationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AgentIdentityEntity>().WithMany().HasForeignKey(item => item.AgentId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(item => new { item.InstallationId, item.IdempotencyKey }).IsUnique();
            entity.HasIndex(item => new { item.InstallationId, item.AgentId, item.ScopeId, item.Kind, item.ExpiresAtUtcTicks });
            entity.Property(item => item.ScopeId).HasMaxLength(256).IsRequired();
            entity.Property(item => item.Kind).HasMaxLength(64).IsRequired();
            entity.Property(item => item.Content).IsRequired();
            entity.Property(item => item.ContentHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.SourceKind).HasMaxLength(64).IsRequired();
            entity.Property(item => item.SourceId).HasMaxLength(512).IsRequired();
            entity.Property(item => item.SourceEvidenceHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.SourceUri).HasMaxLength(2048);
            entity.Property(item => item.Version).IsConcurrencyToken();
            entity.Property(item => item.ActorId).HasMaxLength(256).IsRequired();
            entity.Property(item => item.CorrelationId).HasMaxLength(128).IsRequired();
            entity.Property(item => item.CausationId).HasMaxLength(128);
            entity.Property(item => item.IdempotencyKey).HasMaxLength(128).IsRequired();
        });

        modelBuilder.Entity<ChannelIdentityBindingEntity>(entity =>
        {
            entity.ToTable("channel_identity_bindings");
            entity.HasKey(item => new { item.Channel, item.AccountId, item.ExternalSenderId });
            entity.HasOne<InstallationEntity>().WithMany().HasForeignKey(item => item.InstallationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AgentIdentityEntity>().WithMany().HasForeignKey(item => item.AgentId).OnDelete(DeleteBehavior.Restrict);
            entity.Property(item => item.Channel).HasMaxLength(64).IsRequired();
            entity.Property(item => item.AccountId).HasMaxLength(128).IsRequired();
            entity.Property(item => item.ExternalSenderId).HasMaxLength(256).IsRequired();
            entity.Property(item => item.ActorId).HasMaxLength(256).IsRequired();
            entity.Property(item => item.EvidenceHash).HasMaxLength(71).IsRequired();
        });

        modelBuilder.Entity<ChannelInboundMessageEntity>(entity =>
        {
            entity.ToTable("channel_inbound_messages");
            entity.HasKey(item => item.Id);
            entity.HasOne<InstallationEntity>().WithMany().HasForeignKey(item => item.InstallationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AgentIdentityEntity>().WithMany().HasForeignKey(item => item.AgentId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(item => new { item.Channel, item.AccountId, item.ExternalMessageId }).IsUnique();
            entity.HasIndex(item => new { item.InstallationId, item.AgentId, item.OrderKey });
            entity.Property(item => item.Channel).HasMaxLength(64).IsRequired();
            entity.Property(item => item.AccountId).HasMaxLength(128).IsRequired();
            entity.Property(item => item.ExternalMessageId).HasMaxLength(256).IsRequired();
            entity.Property(item => item.MessageHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.OrderKey).HasMaxLength(512).IsRequired();
            entity.Property(item => item.MessageJson).IsRequired();
        });

        modelBuilder.Entity<ChannelDeliveryEntity>(entity =>
        {
            entity.ToTable("channel_deliveries");
            entity.HasKey(item => item.Id);
            entity.HasOne<InstallationEntity>().WithMany().HasForeignKey(item => item.InstallationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AgentIdentityEntity>().WithMany().HasForeignKey(item => item.AgentId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(item => new { item.InstallationId, item.IdempotencyKey }).IsUnique();
            entity.HasIndex(item => new { item.InstallationId, item.AgentId, item.Channel, item.State, item.UpdatedAtUtcTicks });
            entity.Property(item => item.Channel).HasMaxLength(64).IsRequired();
            entity.Property(item => item.State).HasMaxLength(64).IsRequired();
            entity.Property(item => item.IdempotencyKey).HasMaxLength(128).IsRequired();
            entity.Property(item => item.RequestHash).HasMaxLength(71).IsRequired();
            entity.Property(item => item.Version).IsConcurrencyToken();
            entity.Property(item => item.DeliveryJson).IsRequired();
        });
    }
}
