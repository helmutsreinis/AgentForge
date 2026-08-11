using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AgentForge.Abstractions.Coding;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Coding;
using AgentForge.Domain.Primitives;

namespace AgentForge.Coding;

internal sealed partial class HashBoundPatchApplier(IClock clock) : ICodingPatchApplier
{
    private const int MaximumFileBytes = 1_048_576;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task<DomainResult<CodingPatchReceipt>> ApplyAsync(
        CodingWorkspace workspace,
        CodingPatchSet patch,
        CancellationToken cancellationToken)
    {
        if (!CodingRecordValidator.IsValid(workspace) || !CodingPatchValidator.IsValid(patch) ||
            !string.Equals(workspace.BaselineTreeHash, patch.BaselineTreeHash, StringComparison.Ordinal) ||
            !Directory.Exists(workspace.WorktreeRoot))
        {
            return Invalid("The patch does not bind the exact valid coding workspace baseline.");
        }

        var changes = new List<PreparedChange>();
        foreach (var file in patch.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryTarget(workspace.WorktreeRoot, file.RelativePath, out var target))
            {
                return Invalid("A patch path escaped the worktree or crossed a filesystem link.");
            }

            var exists = File.Exists(target);
            byte[] beforeBytes;
            try
            {
                beforeBytes = exists ? await File.ReadAllBytesAsync(target!, cancellationToken) : [];
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return External("A patch target could not be read safely.");
            }

            if (beforeBytes.Length > MaximumFileBytes || Hash(beforeBytes) != file.ExpectedContentHash)
            {
                return DomainResult.Fail<CodingPatchReceipt>(new DomainFailure(
                    FailureCode.ConcurrencyConflict, "A patch target changed from its expected content hash."));
            }

            var transformed = ApplyUnifiedDiff(file, beforeBytes, exists);
            if (!transformed.IsSuccess)
            {
                return DomainResult.Fail<CodingPatchReceipt>(transformed.Failure!);
            }

            changes.Add(new PreparedChange(
                file.RelativePath,
                target!,
                beforeBytes,
                transformed.Value.Bytes,
                transformed.Value.Delete,
                transformed.Value.AddedLines,
                transformed.Value.RemovedLines));
        }

        var transactionRoot = Path.Combine(workspace.WorktreeRoot, $".agentforge-patch-{Guid.NewGuid():N}");
        var committed = new List<CommittedChange>();
        try
        {
            Directory.CreateDirectory(transactionRoot);
            for (var index = 0; index < changes.Count; index++)
            {
                var change = changes[index];
                var stage = Path.Combine(transactionRoot, $"{index:D4}.new");
                var backup = Path.Combine(transactionRoot, $"{index:D4}.old");
                if (!change.Delete)
                {
                    await File.WriteAllBytesAsync(stage, change.AfterBytes, cancellationToken);
                }

                var existed = File.Exists(change.TargetPath);
                if (existed)
                {
                    File.Move(change.TargetPath, backup);
                }

                committed.Add(new CommittedChange(change.TargetPath, backup, existed));

                if (!change.Delete)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(change.TargetPath)!);
                    File.Move(stage, change.TargetPath);
                }

            }

            var evidence = changes.Select(change => new CodingFileChangeEvidence(
                change.RelativePath,
                Hash(change.BeforeBytes),
                Hash(change.AfterBytes),
                change.AddedLines,
                change.RemovedLines)).ToArray();
            var appliedAt = clock.UtcNow;
            var receipt = CodingPatchValidator.CreatePatchReceipt(patch.PatchHash, evidence, appliedAt);
            return receipt.IsSuccess
                ? receipt
                : DomainResult.Fail<CodingPatchReceipt>(receipt.Failure!);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Rollback(committed);
            return External("The patch could not be committed atomically to the worktree.");
        }
        finally
        {
            if (Directory.Exists(transactionRoot)) Directory.Delete(transactionRoot, recursive: true);
        }
    }

    private static DomainResult<TransformedFile> ApplyUnifiedDiff(
        CodingFilePatch patch,
        byte[] beforeBytes,
        bool exists)
    {
        string before;
        try
        {
            before = StrictUtf8.GetString(beforeBytes);
        }
        catch (DecoderFallbackException)
        {
            return InvalidFile("Patch targets must be strict UTF-8 text.");
        }

        var diff = patch.UnifiedDiff.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (diff.Contains('\r'))
        {
            return InvalidFile("Unified patches must use canonical LF line endings.");
        }

        var lines = diff.Split('\n');
        if (lines.Length < 3 || !TryHeader(lines[0], "--- ", patch.RelativePath, out var creates) ||
            !TryHeader(lines[1], "+++ ", patch.RelativePath, out var deletes) || creates && deletes ||
            creates != !exists || deletes && !exists)
        {
            return InvalidFile("Unified patch headers do not match the exact target state and path.");
        }

        var newline = before.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var normalized = before.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (normalized.Contains('\r'))
        {
            return InvalidFile("Patch targets with lone carriage returns are unsupported.");
        }

        var hadFinalNewline = normalized.EndsWith('\n');
        var original = normalized.Split('\n').ToList();
        if (hadFinalNewline) original.RemoveAt(original.Count - 1);
        if (!exists) original.Clear();
        var output = new List<string>();
        var sourceIndex = 0;
        var lineIndex = 2;
        var added = 0;
        var removed = 0;
        var outputFinalNewline = hadFinalNewline || creates;
        while (lineIndex < lines.Length && lines[lineIndex].Length > 0)
        {
            var match = HunkHeader().Match(lines[lineIndex++]);
            if (!match.Success || !TryHunkNumber(match.Groups[1].Value, out var oldStart) ||
                !TryCount(match.Groups[2].Value, out var oldCount) ||
                !TryHunkNumber(match.Groups[3].Value, out var newStart) ||
                !TryCount(match.Groups[4].Value, out var newCount))
            {
                return InvalidFile("A unified patch hunk header is invalid.");
            }

            var targetIndex = oldStart == 0 ? 0 : oldStart - 1;
            if (targetIndex < sourceIndex || targetIndex > original.Count || newStart < 0)
            {
                return InvalidFile("A unified patch hunk is overlapping or outside the target.");
            }

            output.AddRange(original.GetRange(sourceIndex, targetIndex - sourceIndex));
            sourceIndex = targetIndex;
            var expectedOutputIndex = newStart == 0 ? 0 : newStart - 1;
            if (output.Count != expectedOutputIndex)
            {
                return InvalidFile("A unified patch new-file position is inconsistent.");
            }
            var observedOld = 0;
            var observedNew = 0;
            while (lineIndex < lines.Length && !lines[lineIndex].StartsWith("@@ ", StringComparison.Ordinal) &&
                   lines[lineIndex].Length > 0)
            {
                var line = lines[lineIndex++];
                if (line == "\\ No newline at end of file")
                {
                    outputFinalNewline = false;
                    continue;
                }

                if (line[0] is ' ' or '-')
                {
                    if (sourceIndex >= original.Count || original[sourceIndex] != line[1..])
                    {
                        return DomainResult.Fail<TransformedFile>(new DomainFailure(
                            FailureCode.ConcurrencyConflict, "Unified patch context no longer matches the target."));
                    }

                    if (line[0] == ' ')
                    {
                        output.Add(original[sourceIndex]);
                        observedNew++;
                    }
                    else
                    {
                        removed++;
                    }

                    sourceIndex++;
                    observedOld++;
                }
                else if (line[0] == '+')
                {
                    output.Add(line[1..]);
                    observedNew++;
                    added++;
                }
                else
                {
                    return InvalidFile("A unified patch contains an unsupported hunk line.");
                }
            }

            if (observedOld != oldCount || observedNew != newCount)
            {
                return InvalidFile("A unified patch hunk count does not match its body.");
            }
        }

        output.AddRange(original.Skip(sourceIndex));
        if (deletes && output.Count != 0)
        {
            return InvalidFile("A delete patch must remove the complete file.");
        }

        var afterText = string.Join(newline, output) + (!deletes && outputFinalNewline ? newline : string.Empty);
        var bytes = deletes ? [] : StrictUtf8.GetBytes(afterText);
        return bytes.Length > MaximumFileBytes
            ? InvalidFile("A patched file exceeded its byte bound.")
            : DomainResult.Success(new TransformedFile(bytes, deletes, added, removed));
    }

    private static bool TryHeader(string line, string prefix, string path, out bool nullPath)
    {
        nullPath = false;
        if (!line.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var value = line[prefix.Length..];
        nullPath = value == "/dev/null";
        var expectedPrefix = prefix == "--- " ? "a/" : "b/";
        return nullPath || value == expectedPrefix + path;
    }

    private static bool TryHunkNumber(string value, out int number) =>
        int.TryParse(value, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out number) && number >= 0;

    private static bool TryCount(string value, out int count)
    {
        if (value.Length == 0) { count = 1; return true; }
        return int.TryParse(value[1..], System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out count) && count >= 0;
    }

    private static bool TryTarget(string root, string relativePath, out string? target)
    {
        target = null;
        if (!CodingPatchValidator.IsPath(relativePath)) return false;
        try
        {
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            target = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!target.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison)) return false;
            var current = Path.GetDirectoryName(target);
            while (current is not null && !string.Equals(current, fullRoot, comparison))
            {
                if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
                current = Path.GetDirectoryName(current);
            }
            return current is not null && (!File.Exists(target) || (File.GetAttributes(target) & FileAttributes.ReparsePoint) == 0);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void Rollback(IEnumerable<CommittedChange> committed)
    {
        foreach (var change in committed.Reverse())
        {
            try
            {
                if (File.Exists(change.TargetPath)) File.Delete(change.TargetPath);
                if (change.Existed && File.Exists(change.BackupPath)) File.Move(change.BackupPath, change.TargetPath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string Hash(ReadOnlySpan<byte> value) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(value))}";

    private static DomainResult<CodingPatchReceipt> Invalid(string message) =>
        DomainResult.Fail<CodingPatchReceipt>(new DomainFailure(FailureCode.ValidationFailure, message));

    private static DomainResult<TransformedFile> InvalidFile(string message) =>
        DomainResult.Fail<TransformedFile>(new DomainFailure(FailureCode.ValidationFailure, message));

    private static DomainResult<CodingPatchReceipt> External(string message) =>
        DomainResult.Fail<CodingPatchReceipt>(new DomainFailure(FailureCode.RecoverableExternalFailure, message));

    [GeneratedRegex("^@@ -(\\d+)(,\\d+)? \\+(\\d+)(,\\d+)? @@(?: .*)?$")]
    private static partial Regex HunkHeader();

    private sealed record TransformedFile(byte[] Bytes, bool Delete, int AddedLines, int RemovedLines);
    private sealed record PreparedChange(string RelativePath, string TargetPath, byte[] BeforeBytes,
        byte[] AfterBytes, bool Delete, int AddedLines, int RemovedLines);
    private sealed record CommittedChange(string TargetPath, string BackupPath, bool Existed);
}
