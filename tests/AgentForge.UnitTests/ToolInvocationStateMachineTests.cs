using System.Text;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Security;
using AgentForge.Domain.Tools;

namespace AgentForge.UnitTests;

public sealed class ToolInvocationStateMachineTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 7, 30, 0, TimeSpan.Zero);
    private const string DescriptorHash =
        "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void Authorized_invocation_completes_with_output_evidence_and_no_raw_output()
    {
        var authorized = Authorize();
        var capabilities = new ProcessSandboxCapabilities(
            ProcessSandboxKind.Container,
            true,
            ProcessIsolationFeature.NetworkIsolation,
            "fixture");
        var execution = new ProcessExecutionResult(
            0,
            Encoding.UTF8.GetBytes("output"),
            Encoding.UTF8.GetBytes("warning"),
            Now,
            Now.AddSeconds(1),
            TimeSpan.FromSeconds(1),
            capabilities);

        var completed = ToolInvocationStateMachine.Complete(authorized, execution);

        Assert.True(completed.IsSuccess, completed.Failure?.Message);
        Assert.Equal(ToolInvocationState.Succeeded, completed.Value.State);
        Assert.Equal(0, completed.Value.ExitCode);
        Assert.Equal(6, completed.Value.StandardOutputLength);
        Assert.Equal(7, completed.Value.StandardErrorLength);
        Assert.StartsWith("sha256:", completed.Value.StandardOutputHash, StringComparison.Ordinal);
        Assert.DoesNotContain("output", completed.Value.StandardOutputHash, StringComparison.Ordinal);
        Assert.Equal(1, completed.Value.Version);
    }

    [Fact]
    public void Nonzero_exit_is_a_completed_tool_failure()
    {
        var authorized = Authorize();
        var execution = new ProcessExecutionResult(
            17,
            [],
            [],
            Now,
            Now.AddSeconds(1),
            TimeSpan.FromSeconds(1),
            new ProcessSandboxCapabilities(
                ProcessSandboxKind.Container,
                true,
                ProcessIsolationFeature.None,
                "fixture"));

        var completed = ToolInvocationStateMachine.Complete(authorized, execution);

        Assert.Equal(ToolInvocationState.ToolFailed, completed.Value.State);
        Assert.Equal(17, completed.Value.ExitCode);
    }

    [Fact]
    public void Execution_failure_and_cancellation_are_terminal_and_cannot_transition_twice()
    {
        var authorized = Authorize();
        var failed = ToolInvocationStateMachine.Fail(
            authorized,
            new DomainFailure(FailureCode.UnsupportedCapability, "fixture"),
            Now.AddSeconds(1));
        var canceled = ToolInvocationStateMachine.Cancel(authorized, Now.AddSeconds(1));

        Assert.Equal(ToolInvocationState.ExecutionFailed, failed.Value.State);
        Assert.Equal(FailureCode.UnsupportedCapability, failed.Value.FailureCode);
        Assert.Equal(ToolInvocationState.Canceled, canceled.Value.State);
        Assert.Equal(
            FailureCode.InvalidStateTransition,
            ToolInvocationStateMachine.Cancel(failed.Value, Now.AddSeconds(2)).Failure?.Code);
    }

    [Fact]
    public void Authorization_rejects_descriptor_substitution()
    {
        var result = ToolInvocationStateMachine.Authorize(
            new ToolInvocationId(Guid.NewGuid()),
            CreateContext(),
            "sha256:" + new string('b', 64),
            null,
            "tool-invocation-001",
            Now);

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureCode.InvalidStateTransition, result.Failure?.Code);
    }

    private static ToolInvocationRecord Authorize()
    {
        var result = ToolInvocationStateMachine.Authorize(
            new ToolInvocationId(Guid.Parse("f66ff165-d5a1-4c2c-bca2-f118d14e536f")),
            CreateContext(),
            DescriptorHash,
            new CapabilityApprovalId(Guid.Parse("e4a26dfb-0c01-48c4-ab85-e4357c242f66")),
            "tool-invocation-001",
            Now);
        Assert.True(result.IsSuccess, result.Failure?.Message);
        return result.Value;
    }

    private static AuthorizationContext CreateContext() => new(
        new InstallationId(Guid.Parse("629a1a64-3aa5-4257-8bf9-b0d44f3abcf4")),
        7,
        new AgentIdentityId(Guid.Parse("24d89d17-48d1-429c-9236-cb1432b277a4")),
        3,
        new ActorId("worker"),
        "tool:repo.read",
        CapabilityRiskClass.Read,
        "tool:repo.read",
        "1.0.0",
        DescriptorHash,
        "{\"path\":\"src\"}",
        "sha256:" + new string('c', 64),
        AuthorizationTargetKind.FileSystemPath,
        Path.Combine(Path.GetTempPath(), "agentforge-tool-target"),
        "sha256:" + new string('d', 64),
        Path.GetTempPath(),
        "sha256:" + new string('e', 64),
        new CorrelationId("tool-invocation"),
        null,
        "sha256:" + new string('f', 64));
}
