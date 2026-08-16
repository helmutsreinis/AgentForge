using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentForge.Abstractions.Artifacts;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Learning;
using AgentForge.Abstractions.Skills;
using AgentForge.Domain.Learning;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Skills;

namespace AgentForge.Learning;

internal sealed class IsolatedLearningCandidateEvaluator(
    ILearningRepository repository,
    ISkillRegistryRepository skillRegistry,
    ISkillPackageLoader packageLoader,
    IArtifactStore artifacts,
    IDataDirectoryProvider dataDirectoryProvider,
    ILearningGovernanceService governance) : ILearningCandidateEvaluator
{
    private const int MaximumFiles = 16;
    private const int MaximumFileBytes = 1_048_576;
    private const int MaximumWorkspaceBytes = 4_194_304;
    private const string ReceiptMediaType = "application/vnd.agentforge.learning-evaluation+json";
    private const string EvaluatorName = "agentforge-managed-isolated-v1";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions ReceiptJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private static readonly string[] ProhibitedAuthorityFragments =
    [
        "ignore previous instructions",
        "ignore all previous",
        "bypass policy",
        "disable policy",
        "skip approval",
        "without approval",
        "reveal secret",
        "exfiltrate",
        "grant yourself",
        "system prompt",
    ];

    public async Task<DomainResult<AutomatedLearningEvaluationResult>> EvaluateAsync(
        LearningCandidateId candidateId,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        if (candidateId.Value == Guid.Empty || expectedVersion < 0)
        {
            return Invalid<AutomatedLearningEvaluationResult>("A candidate ID and current version are required.");
        }

        var candidate = await repository.FindLatestCandidateAsync(candidateId, cancellationToken);
        if (candidate is null)
        {
            return Invalid<AutomatedLearningEvaluationResult>("The learning candidate does not exist.");
        }
        if (candidate.Version != expectedVersion || candidate.State is not LearningCandidateState.Proposed ||
            !LearningCandidateStateMachine.IsConsistent(candidate))
        {
            return Conflict<AutomatedLearningEvaluationResult>(
                "Only the current immutable Proposed candidate can be evaluated.");
        }

        var registered = await skillRegistry.FindAsync(
            candidate.InstallationId, candidate.SkillId, candidate.CandidateVersion, cancellationToken);
        if (registered is null)
        {
            return Invalid<AutomatedLearningEvaluationResult>("The exact candidate package is not installed.");
        }

        var isolatedDirectory = PrepareIsolatedDirectory(candidate);
        if (!isolatedDirectory.IsSuccess)
        {
            return DomainResult.Fail<AutomatedLearningEvaluationResult>(isolatedDirectory.Failure!);
        }

        try
        {
            var checks = new List<LearningEvaluationCheck>();
            var extraction = await ExtractWorkspaceAsync(
                candidate.ProposalWorkspace, isolatedDirectory.Value, cancellationToken);
            checks.Add(new LearningEvaluationCheck(
                "workspace.integrity", extraction.IsSuccess,
                extraction.IsSuccess
                    ? "The content-addressed archive was bounded, hash-matched, and contained only regular files."
                    : extraction.Failure!.Message));

            LoadedSkillPackage? loaded = null;
            if (extraction.IsSuccess)
            {
                var firstLoad = await packageLoader.LoadAsync(isolatedDirectory.Value, cancellationToken);
                if (firstLoad.IsSuccess)
                {
                    loaded = firstLoad.Value;
                }
                checks.Add(new LearningEvaluationCheck(
                    "target.package-contract", firstLoad.IsSuccess && ExactCandidate(candidate, registered, firstLoad.Value),
                    firstLoad.IsSuccess
                        ? ExactCandidate(candidate, registered, firstLoad.Value)
                            ? "The loaded package identity, version, hash, and declared permissions match immutable candidate state."
                            : "The loaded package does not match immutable candidate state."
                        : firstLoad.Failure!.Message));

                var secondLoad = await packageLoader.LoadAsync(isolatedDirectory.Value, cancellationToken);
                var deterministic = firstLoad.IsSuccess && secondLoad.IsSuccess &&
                    firstLoad.Value.CanonicalBytes.Span.SequenceEqual(secondLoad.Value.CanonicalBytes.Span) &&
                    string.Equals(firstLoad.Value.Package.PackageHash, secondLoad.Value.Package.PackageHash,
                        StringComparison.Ordinal);
                checks.Add(new LearningEvaluationCheck(
                    "holdout.deterministic-reload", deterministic,
                    deterministic
                        ? "Two independent bounded loads produced identical canonical bytes and package hashes."
                        : "The package did not reload deterministically in the isolated evaluator."));
            }
            else
            {
                checks.Add(new LearningEvaluationCheck(
                    "target.package-contract", false, "The package contract was not evaluated because workspace integrity failed."));
                checks.Add(new LearningEvaluationCheck(
                    "holdout.deterministic-reload", false, "The holdout reload was not evaluated because workspace integrity failed."));
            }

            var adversarialPassed = loaded is not null && !ContainsProhibitedAuthority(loaded.Package);
            checks.Add(new LearningEvaluationCheck(
                "adversarial.authority-escalation", adversarialPassed,
                adversarialPassed
                    ? "The hostile authority-escalation corpus found no prohibited instruction patterns."
                    : "The package contains an instruction pattern that attempts to bypass policy, approval, or secret boundaries."));

            var permissionPassed = loaded is not null && PermissionDiffIsBounded(candidate, loaded.Package);
            checks.Add(new LearningEvaluationCheck(
                "permissions.exact-readonly-diff", permissionPassed,
                permissionPassed
                    ? "Declared permissions exactly match the candidate and use only automatically allowed read-only forms."
                    : "The permission diff is mismatched or requires explicit high-risk authorization that this evaluator cannot grant."));

            var targetPassed = checks.Where(check => check.Code is "workspace.integrity" or "target.package-contract")
                .All(check => check.Passed);
            var holdoutPassed = checks.Single(check => check.Code == "holdout.deterministic-reload").Passed;
            var baselineScore = candidate.BaselinePackageHash is null ? 0m : 100m;
            var candidateScore = decimal.Round(
                checks.Count(check => check.Passed) * 100m / checks.Count, 2, MidpointRounding.ToEven);
            var receiptDocument = new EvaluationReceiptDocument(
                1,
                candidate.Id.Value,
                candidate.Version,
                candidate.SnapshotHash,
                candidate.CandidatePackageHash,
                candidate.ProposalWorkspace.ContentHash,
                EvaluatorName,
                checks.OrderBy(check => check.Code, StringComparer.Ordinal).ToArray(),
                targetPassed,
                holdoutPassed,
                adversarialPassed,
                permissionPassed,
                baselineScore,
                candidateScore);
            await using var receiptBytes = new MemoryStream(
                JsonSerializer.SerializeToUtf8Bytes(receiptDocument, ReceiptJson), writable: false);
            var evidence = await artifacts.PutAsync(receiptBytes, ReceiptMediaType, cancellationToken);
            var evaluation = new LearningCandidateEvaluation(
                targetPassed,
                holdoutPassed,
                adversarialPassed,
                permissionPassed,
                baselineScore,
                candidateScore,
                evidence.ContentHash);
            var transitioned = await governance.VerifyAsync(
                candidate.Id, candidate.Version, candidate.Roles.Verifier, evaluation, cancellationToken);
            if (!transitioned.IsSuccess)
            {
                return DomainResult.Fail<AutomatedLearningEvaluationResult>(transitioned.Failure!);
            }

            var receipt = new AutomatedLearningEvaluationReceipt(
                candidate.Id,
                candidate.Version,
                candidate.SnapshotHash,
                candidate.CandidatePackageHash,
                candidate.ProposalWorkspace.ContentHash,
                EvaluatorName,
                checks.OrderBy(check => check.Code, StringComparer.Ordinal).ToArray(),
                evaluation,
                evidence);
            return DomainResult.Success(new AutomatedLearningEvaluationResult(
                transitioned.Value, receipt, false));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return External<AutomatedLearningEvaluationResult>(
                "The isolated evaluator could not access its bounded workspace.");
        }
        finally
        {
            TryDeleteIsolatedDirectory(isolatedDirectory.Value);
        }
    }

    private DomainResult<string> PrepareIsolatedDirectory(LearningCandidate candidate)
    {
        try
        {
            var dataDirectory = Path.GetFullPath(dataDirectoryProvider.GetDataDirectory());
            var root = Path.GetFullPath(Path.Combine(dataDirectory, "learning", "evaluation-sandboxes"));
            var directory = Path.GetFullPath(Path.Combine(
                root, $"{candidate.Id.Value:N}-{candidate.Version}-{Guid.NewGuid():N}"));
            if (!IsContained(root, directory) || Directory.Exists(directory))
            {
                return Invalid<string>("The evaluator workspace could not be isolated safely.");
            }
            Directory.CreateDirectory(directory);
            return DomainResult.Success(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or PathTooLongException)
        {
            return External<string>("The evaluator workspace could not be created.");
        }
    }

    private async Task<DomainResult<bool>> ExtractWorkspaceAsync(
        Domain.Artifacts.ArtifactReference workspace,
        string destination,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(workspace.MediaType, "application/vnd.agentforge.learning-workspace+tar", StringComparison.Ordinal) ||
            workspace.Length is < 1 or > MaximumWorkspaceBytes)
        {
            return Invalid<bool>("The proposal workspace reference is outside evaluator bounds.");
        }

        await using var source = await artifacts.OpenReadAsync(workspace, cancellationToken);
        await using var bounded = new MemoryStream((int)workspace.Length);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81_920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total = checked(total + read);
            if (total > workspace.Length || total > MaximumWorkspaceBytes)
            {
                return Invalid<bool>("The proposal workspace exceeded its content-addressed byte bound.");
            }
            hash.AppendData(buffer, 0, read);
            await bounded.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        var actualHash = $"sha256:{Convert.ToHexStringLower(hash.GetHashAndReset())}";
        if (total != workspace.Length || !string.Equals(actualHash, workspace.ContentHash, StringComparison.Ordinal))
        {
            return Invalid<bool>("The proposal workspace bytes do not match their immutable reference.");
        }

        bounded.Position = 0;
        using var reader = new TarReader(bounded, leaveOpen: true);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        TarEntry? entry;
        while ((entry = reader.GetNextEntry(copyData: false)) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            count++;
            var name = entry.Name.Replace('\\', '/');
            if (count > MaximumFiles || entry.EntryType is not TarEntryType.RegularFile ||
                !SafeRelativePath(name) || !names.Add(name) || entry.Length is < 0 or > MaximumFileBytes ||
                entry.DataStream is null)
            {
                return Invalid<bool>("The proposal workspace contains an unsafe, duplicate, linked, or oversized entry.");
            }
            var outputPath = Path.GetFullPath(Path.Combine(destination, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsContained(destination, outputPath))
            {
                return Invalid<bool>("The proposal workspace entry escaped evaluator containment.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await using var output = new FileStream(
                outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16_384,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await entry.DataStream.CopyToAsync(output, 16_384, cancellationToken);
            await output.FlushAsync(cancellationToken);
            if (output.Length != entry.Length)
            {
                return Invalid<bool>("A proposal workspace entry changed length during extraction.");
            }
        }

        return names.SetEquals(["SKILL.md", "skill.harness.json"])
            ? DomainResult.Success(true)
            : Invalid<bool>("The evaluator requires exactly SKILL.md and skill.harness.json at the workspace root.");
    }

    private static bool ExactCandidate(
        LearningCandidate candidate,
        RegisteredSkillVersion registered,
        LoadedSkillPackage loaded) =>
        loaded.Package.Id == candidate.SkillId &&
        loaded.Package.Version == candidate.CandidateVersion &&
        string.Equals(loaded.Package.PackageHash, candidate.CandidatePackageHash, StringComparison.Ordinal) &&
        string.Equals(registered.Package.PackageHash, candidate.CandidatePackageHash, StringComparison.Ordinal) &&
        registered.Provenance is SkillPackageProvenance.AgentProposal &&
        registered.Status is SkillPackageStatus.Installed &&
        loaded.Package.Permissions.Order(StringComparer.Ordinal).SequenceEqual(
            candidate.RequestedPermissions, StringComparer.Ordinal);

    private static bool ContainsProhibitedAuthority(SkillPackage package)
    {
        var corpus = string.Join('\n', new[] { package.Description, package.Markdown }
            .Concat(package.Permissions)).ToLowerInvariant();
        return ProhibitedAuthorityFragments.Any(corpus.Contains);
    }

    private static bool PermissionDiffIsBounded(LearningCandidate candidate, SkillPackage package) =>
        package.Permissions.Order(StringComparer.Ordinal).SequenceEqual(
            candidate.RequestedPermissions, StringComparer.Ordinal) &&
        package.Permissions.All(IsAutomaticallyAllowedReadOnlyPermission);

    private static bool IsAutomaticallyAllowedReadOnlyPermission(string permission)
    {
        var normalized = permission.ToLowerInvariant();
        return normalized.EndsWith(":read", StringComparison.Ordinal) ||
            normalized.EndsWith(".read", StringComparison.Ordinal) ||
            normalized.EndsWith(":metadata", StringComparison.Ordinal) ||
            normalized.EndsWith(".metadata", StringComparison.Ordinal);
    }

    private static bool SafeRelativePath(string path) =>
        !string.IsNullOrWhiteSpace(path) && path.Length <= 512 && !Path.IsPathRooted(path) &&
        !path.Contains(':') && path.Split('/').All(part => part is not ("" or "." or ".."));

    private static bool IsContained(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root);
        var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, PathComparison);
    }

    private static void TryDeleteIsolatedDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A failed cleanup leaves only bounded, non-secret proposal material in the AgentForge data directory.
        }
        catch (UnauthorizedAccessException)
        {
            // Recovery/doctor can remove a bounded evaluator directory after an OS handle is released.
        }
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static DomainResult<T> Invalid<T>(string message) => DomainResult.Fail<T>(new DomainFailure(
        FailureCode.ValidationFailure, message));

    private static DomainResult<T> Conflict<T>(string message) => DomainResult.Fail<T>(new DomainFailure(
        FailureCode.ConcurrencyConflict, message));

    private static DomainResult<T> External<T>(string message) => DomainResult.Fail<T>(new DomainFailure(
        FailureCode.RecoverableExternalFailure, message, IsRetryable: true));

    private sealed record EvaluationReceiptDocument(
        int SchemaVersion,
        Guid CandidateId,
        long CandidateVersion,
        string CandidateSnapshotHash,
        string CandidatePackageHash,
        string ProposalWorkspaceHash,
        string Evaluator,
        IReadOnlyList<LearningEvaluationCheck> Checks,
        bool TargetPassed,
        bool HoldoutPassed,
        bool AdversarialPassed,
        bool PermissionDiffApproved,
        decimal BaselineScore,
        decimal CandidateScore);
}
