using System.Collections.Immutable;
using AgentForge.Abstractions.Auditing;
using AgentForge.Abstractions.Devices;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Persistence;
using AgentForge.Abstractions.Time;
using AgentForge.Devices;
using AgentForge.Domain.Devices;
using AgentForge.Domain.Installations;
using AgentForge.Domain.Primitives;
using AgentForge.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.IntegrationTests;

public sealed partial class PersistenceFoundationTests
{
    [Fact]
    public async Task Decoder_governance_is_durable_separated_stale_safe_and_atomically_rollbackable()
    {
        var clock = new DecoderClock();
        await using var services = BuildServices(_directory, "decoder-governance.db", collection =>
        {
            collection.AddSingleton<IClock>(clock);
            collection.AddSingleton<IIdentifierGenerator>(clock);
            collection.AddAgentForgeDevices();
        });
        await using (var initialize = services.CreateAsyncScope())
            await initialize.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync(CancellationToken.None);
        var installationId = new InstallationId(Guid.Parse("436e3118-eb63-41f7-9931-af4dab1f4882"));
        await using (var seed = services.CreateAsyncScope())
        {
            await seed.ServiceProvider.GetRequiredService<IInstallationRepository>().AddAsync(
                InstallationSnapshot.CreateUninitialized(installationId, Now, new ActorId("decoder-admin"),
                    new CorrelationId("decoder-seed")), CancellationToken.None);
            Assert.True((await seed.ServiceProvider.GetRequiredService<IUnitOfWork>()
                .CommitAsync(CancellationToken.None)).Succeeded);
        }

        var firstId = new DecoderProposalId(Guid.Parse("de6d28b0-cf2a-4407-b16c-c22c7be50fe6"));
        var secondId = new DecoderProposalId(Guid.Parse("0b55b621-0998-49bf-870b-4e129f5a308c"));
        var thirdId = new DecoderProposalId(Guid.Parse("b59547de-3696-4b78-a55a-e58f9e3a9a47"));
        var first = DecoderDefinition("1.0.0");
        var second = DecoderDefinition("2.0.0");
        var suite = DecoderSuite();

        DecoderProposalSnapshot firstCanary;
        DecoderProposalSnapshot secondCanary;
        await using (var scope = services.CreateAsyncScope())
        {
            var governance = scope.ServiceProvider.GetRequiredService<IDecoderGovernanceService>();
            var proposed = await governance.ProposeAsync(new(firstId, installationId, first, null,
                new ActorId("proposer-a"), new CorrelationId("decoder-propose-a")), CancellationToken.None);
            Assert.True(proposed.IsSuccess, proposed.Failure?.Message);
            var selfEvaluation = await governance.EvaluateAsync(new(firstId, 0, suite,
                new ActorId("proposer-a"), new CorrelationId("decoder-self-eval")), CancellationToken.None);
            Assert.Equal(FailureCode.PolicyDenied, selfEvaluation.Failure?.Code);
            var evaluated = await governance.EvaluateAsync(new(firstId, 0, suite,
                new ActorId("verifier-a"), new CorrelationId("decoder-eval-a")), CancellationToken.None);
            Assert.True(evaluated.IsSuccess, evaluated.Failure?.Message);
            var selfApproval = await governance.ApproveAsync(new(firstId, 1,
                new ActorId("proposer-a"), new CorrelationId("decoder-self-approve")), CancellationToken.None);
            Assert.Equal(FailureCode.PolicyDenied, selfApproval.Failure?.Code);
            var approved = await governance.ApproveAsync(new(firstId, 1,
                new ActorId("governor-a"), new CorrelationId("decoder-approve-a")), CancellationToken.None);
            Assert.True(approved.IsSuccess, approved.Failure?.Message);
            firstCanary = approved.Value;

            var proposedSecond = await governance.ProposeAsync(new(secondId, installationId, second, null,
                new ActorId("proposer-b"), new CorrelationId("decoder-propose-b")), CancellationToken.None);
            var evaluatedSecond = await governance.EvaluateAsync(new(secondId, proposedSecond.Value.Version, suite,
                new ActorId("verifier-b"), new CorrelationId("decoder-eval-b")), CancellationToken.None);
            var approvedSecond = await governance.ApproveAsync(new(secondId, evaluatedSecond.Value.Version,
                new ActorId("governor-b"), new CorrelationId("decoder-approve-b")), CancellationToken.None);
            Assert.True(approvedSecond.IsSuccess, approvedSecond.Failure?.Message);
            secondCanary = approvedSecond.Value;

            var promoted = await governance.PromoteAsync(new(firstId, firstCanary.Version,
                PassingCanary('a'), new ActorId("release-governor"), new CorrelationId("decoder-promote-a")),
                CancellationToken.None);
            Assert.True(promoted.IsSuccess, promoted.Failure?.Message);
            var stale = await governance.PromoteAsync(new(secondId, secondCanary.Version,
                PassingCanary('b'), new ActorId("release-governor"), new CorrelationId("decoder-promote-b")),
                CancellationToken.None);
            Assert.Equal(FailureCode.ConcurrencyConflict, stale.Failure?.Code);

            var third = DecoderDefinition("3.0.0");
            var proposedThird = await governance.ProposeAsync(new(thirdId, installationId, third, first.DefinitionHash,
                new ActorId("proposer-c"), new CorrelationId("decoder-propose-c")), CancellationToken.None);
            var evaluatedThird = await governance.EvaluateAsync(new(thirdId, proposedThird.Value.Version, suite,
                new ActorId("verifier-c"), new CorrelationId("decoder-eval-c")), CancellationToken.None);
            var approvedThird = await governance.ApproveAsync(new(thirdId, evaluatedThird.Value.Version,
                new ActorId("governor-c"), new CorrelationId("decoder-approve-c")), CancellationToken.None);
            var failedCanary = new DecoderCanaryEvidence("device-group-a", 100, 2, true,
                "sha256:" + new string('c', 64));
            var quarantined = await governance.PromoteAsync(new(thirdId, approvedThird.Value.Version, failedCanary,
                new ActorId("release-governor"), new CorrelationId("decoder-canary-c")), CancellationToken.None);
            Assert.True(quarantined.IsSuccess, quarantined.Failure?.Message);
            Assert.Equal(DecoderProposalState.Quarantined, quarantined.Value.State);
        }

        await using (var restart = services.CreateAsyncScope())
        {
            var repository = restart.ServiceProvider.GetRequiredService<IDecoderProposalRepository>();
            var history = await repository.ListAsync(firstId, CancellationToken.None);
            Assert.Equal(4, history.Count);
            Assert.Equal(first.DefinitionHash, await repository.GetActiveHashAsync(
                installationId, first.DecoderId, CancellationToken.None));
            Assert.Equal(history.Take(3).Select(item => item.SnapshotHash),
                history.Skip(1).Select(item => item.PreviousSnapshotHash));
            Assert.All(history, item => Assert.True(item.IsConsistent()));

            var rollback = await restart.ServiceProvider.GetRequiredService<IDecoderGovernanceService>()
                .RollbackAsync(new(firstId, history[^1].Version, new ActorId("release-governor"),
                    new CorrelationId("decoder-rollback")), CancellationToken.None);
            Assert.True(rollback.IsSuccess, rollback.Failure?.Message);
        }

        await using (var verify = services.CreateAsyncScope())
        {
            var repository = verify.ServiceProvider.GetRequiredService<IDecoderProposalRepository>();
            Assert.Null(await repository.GetActiveHashAsync(installationId, first.DecoderId, CancellationToken.None));
            Assert.Equal(DecoderProposalState.RolledBack,
                (await repository.GetLatestAsync(firstId, CancellationToken.None))!.State);
            Assert.True((await verify.ServiceProvider.GetRequiredService<IAuditIntegrityVerifier>()
                .VerifyAsync(CancellationToken.None)).IsValid);
        }
    }

    private static DeclarativeDecoderDefinition DecoderDefinition(string version)
    {
        var definition = new DeclarativeDecoderDefinition("durable.decoder", version, 8, [0xaa, 0x55],
            [new("value", 2, 2, DecoderFieldEncoding.UInt16LittleEndian),
             new("status", 4, 1, DecoderFieldEncoding.ByteUnsigned)],
            new[] { DecoderAuthority.ProtocolDecode }.ToImmutableSortedSet(), string.Empty);
        return definition with { DefinitionHash = DeclarativeDecoderDefinition.CalculateHash(definition) };
    }

    private static DecoderEvaluationSuite DecoderSuite()
    {
        var frame = new byte[] { 0xaa, 0x55, 1, 0, 2, 0xde, 0xad, 0xbe }.ToImmutableArray();
        var suite = new DecoderEvaluationSuite([new("target", frame, 1, 3)],
            [new("holdout", new byte[] { 0x99 }.Concat(frame).ToImmutableArray(), 1, 4)], 64, 16, string.Empty);
        return suite with { SuiteHash = DecoderEvaluationSuiteHasher.Calculate(suite) };
    }

    private static DecoderCanaryEvidence PassingCanary(char value) =>
        new("device-group-a", 100, 0, false, $"sha256:{new string(value, 64)}");

    private sealed class DecoderClock : IClock, IIdentifierGenerator
    {
        public DateTimeOffset UtcNow => Now;
        public Guid NewGuid() => Guid.NewGuid();
    }
}
