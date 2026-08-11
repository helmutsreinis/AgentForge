using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using AgentForge.Abstractions.Coding;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Coding;
using AgentForge.Domain.Primitives;

namespace AgentForge.Coding;

internal sealed class GitCodingReviewer(IClock clock) : ICodingReviewer
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task<DomainResult<CodingReviewReport>> ReviewAsync(
        CodingWorkspace workspace,
        CodingPatchReceipt patch,
        CodingVerificationReceipt verification,
        CancellationToken cancellationToken)
    {
        if (!CodingRecordValidator.IsValid(workspace) || patch is null || verification is null ||
            !verification.Passed || !CodingRecordValidator.IsSha256(patch.ReceiptHash) ||
            !CodingRecordValidator.IsSha256(verification.ReceiptHash))
        {
            return Invalid("Review requires an exact workspace, patch, and passing verification receipt.");
        }

        var names = await GitAsync(workspace.WorktreeRoot,
            ["diff", "--name-only", "-z", "--no-ext-diff", workspace.BaselineCommit, "--"], cancellationToken);
        var diff = await GitAsync(workspace.WorktreeRoot,
            ["diff", "--binary", "--no-ext-diff", workspace.BaselineCommit, "--"], cancellationToken);
        var check = await GitAsync(workspace.WorktreeRoot,
            ["diff", "--check", "--no-ext-diff", workspace.BaselineCommit, "--"], cancellationToken, allowNonzero: true);
        if (!names.IsSuccess || !diff.IsSuccess || !check.IsSuccess)
        {
            return DomainResult.Fail<CodingReviewReport>(
                names.Failure ?? diff.Failure ?? check.Failure!);
        }

        string namesText;
        try
        {
            namesText = StrictUtf8.GetString(names.Value.Output);
        }
        catch (DecoderFallbackException)
        {
            return Invalid("Git returned a non-UTF-8 changed path.");
        }

        var changedPaths = namesText.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(path => path.Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();
        var expectedPaths = patch.Files.Select(item => item.RelativePath).Order(StringComparer.Ordinal).ToArray();
        var findings = new List<string>();
        if (changedPaths.Length == 0) findings.Add("EMPTY_DIFF");
        if (!changedPaths.SequenceEqual(expectedPaths, StringComparer.Ordinal)) findings.Add("UNRELATED_CHANGE");
        if (check.Value.ExitCode != 0) findings.Add("DIFF_CHECK_FAILED");
        var diffHash = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(diff.Value.Output))}";
        return DomainResult.Success(CodingSessionStateMachine.CreateReviewReport(
            changedPaths, diffHash, findings.Count == 0, findings, clock.UtcNow));
    }

    private static async Task<DomainResult<GitResult>> GitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        bool allowNonzero = false)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            if (!process.Start()) return External("Git review could not start.");
            var outputTask = ReadBoundedAsync(process.StandardOutput.BaseStream, cancellationToken);
            var errorTask = ReadBoundedAsync(process.StandardError.BaseStream, cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            var output = await outputTask;
            var error = await errorTask;
            return process.ExitCode == 0 || allowNonzero
                ? DomainResult.Success(new GitResult(process.ExitCode, output, error))
                : External("Git could not produce bounded review evidence.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return DomainResult.Fail<GitResult>(new DomainFailure(FailureCode.BudgetExceeded, "Git review timed out."));
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            TryKill(process);
            return External("Git review is unavailable.");
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (output.Length + read > 4_194_304) throw new IOException("Git review output exceeded its bound.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return output.ToArray();
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
    }

    private static DomainResult<CodingReviewReport> Invalid(string message) =>
        DomainResult.Fail<CodingReviewReport>(new DomainFailure(FailureCode.ValidationFailure, message));

    private static DomainResult<GitResult> External(string message) =>
        DomainResult.Fail<GitResult>(new DomainFailure(FailureCode.RecoverableExternalFailure, message));

    private sealed record GitResult(int ExitCode, byte[] Output, byte[] Error);
}
