using Microsoft.Agents.AI.Workflows;

namespace AgentFrameworkSpike;

public sealed class WorkflowCompatibilityTests
{
    [Fact]
    public async Task Typed_workflow_builds_and_streams_observable_output()
    {
        var uppercase = new UppercaseExecutor();
        var output = new OutputExecutor();
        var workflow = new WorkflowBuilder(uppercase)
            .AddEdge(uppercase, output)
            .WithOutputFrom(output)
            .Build();

        var run = await InProcessExecution.RunStreamingAsync(workflow, "agentforge");
        var outputs = new List<string>();
        var events = new List<string>();

        await foreach (var workflowEvent in run.WatchStreamAsync())
        {
            events.Add(workflowEvent.GetType().Name);
            if (workflowEvent is WorkflowOutputEvent outputEvent && outputEvent.Data is string value)
            {
                outputs.Add(value);
            }
        }

        Assert.True(
            outputs.SequenceEqual(["AGENTFORGE"]),
            $"Expected one workflow output. Events: {string.Join(", ", events)}; outputs: {string.Join(", ", outputs)}");
    }

    [Fact]
    public async Task Run_cancellation_token_reaches_an_executor()
    {
        using var source = new CancellationTokenSource();
        var executor = new CancellationExecutor();
        var workflow = new WorkflowBuilder(executor).WithOutputFrom(executor).Build();

        var outputs = new List<bool>();
        var run = await InProcessExecution.RunStreamingAsync(
            workflow,
            "cancel",
            cancellationToken: source.Token);
        await foreach (var workflowEvent in run.WatchStreamAsync(CancellationToken.None))
        {
            if (workflowEvent is WorkflowOutputEvent { Data: bool canBeCanceled })
            {
                outputs.Add(canBeCanceled);
            }
        }

        Assert.Equal([true], outputs);
    }

}

internal sealed partial class UppercaseExecutor() : Executor("uppercase")
{
    [MessageHandler]
    private ValueTask<string> HandleAsync(string message, IWorkflowContext context) =>
        ValueTask.FromResult(message.ToUpperInvariant());
}

internal sealed partial class OutputExecutor() : Executor("output")
{
    [MessageHandler(Yield = [typeof(string)])]
    private async ValueTask HandleAsync(string message, IWorkflowContext context)
    {
        await context.YieldOutputAsync(message);
    }
}

internal sealed partial class CancellationExecutor() : Executor("cancellation")
{
    [MessageHandler]
    private ValueTask<bool> HandleAsync(
        string message,
        IWorkflowContext context,
        CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(cancellationToken.CanBeCanceled);
    }
}
