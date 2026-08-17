using System.Formats.Tar;
using System.Text;
using System.Text.Json;
using AgentForge.Abstractions.Artifacts;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Learning;
using AgentForge.Abstractions.Skills;
using AgentForge.Domain.Learning;
using AgentForge.Domain.Models;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Skills;

namespace AgentForge.Learning;

internal sealed class LearningCandidateProposalService(
    ILearningRepository repository,
    ISkillRegistryService skills,
    IArtifactStore artifacts,
    ILearningGovernanceService governance,
    IDataDirectoryProvider dataDirectoryProvider) : ILearningCandidateProposalService
{
    private const string WorkspaceMediaType = "application/vnd.agentforge.learning-workspace+tar";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] SupportedOperatingSystems = ["windows", "linux"];
    private static readonly string[] TextGenerationCapabilities = ["text-generation"];
    private static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public async Task<DomainResult<ProposeNewSkillFromSignalResult>> ProposeNewSkillAsync(
        ProposeNewSkillFromSignalRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalized = Normalize(request);
        if (!normalized.IsSuccess)
        {
            return DomainResult.Fail<ProposeNewSkillFromSignalResult>(normalized.Failure!);
        }

        var existing = await repository.FindLatestCandidateAsync(request.CandidateId, cancellationToken);
        if (existing is not null && !ExistingIdentityMatches(existing, request, normalized.Value))
        {
            return Conflict<ProposeNewSkillFromSignalResult>(
                "The candidate ID is already bound to different proposal evidence.");
        }

        var signal = await repository.FindSignalAsync(request.SignalId, cancellationToken);
        if (signal is null || signal.Value.Classification.Action is not LearningAction.NewSkill)
        {
            return Invalid<ProposeNewSkillFromSignalResult>(
                "Only an existing signal classified as NewSkill can create a new-skill proposal.");
        }
        if (!GenerationMatchesSignal(request, normalized.Value, signal.Value.Signal))
        {
            return Invalid<ProposeNewSkillFromSignalResult>(
                "Local-model generation evidence does not match the exact candidate and classified source signal.");
        }
        var otherCandidates = await repository.ListCandidatesAsync(
            signal.Value.Signal.InstallationId, 500, cancellationToken);
        if (otherCandidates.Any(item => item.SignalId == request.SignalId && item.Id != request.CandidateId))
        {
            return Conflict<ProposeNewSkillFromSignalResult>(
                "This learning signal already owns a candidate; inspect its current lifecycle.");
        }

        var files = BuildPackageFiles(signal.Value.Signal, normalized.Value);
        var directory = PrepareWorkspaceDirectory(request.CandidateId);
        if (!directory.IsSuccess)
        {
            return DomainResult.Fail<ProposeNewSkillFromSignalResult>(directory.Failure!);
        }

        var written = await WriteExactWorkspaceAsync(directory.Value, files, cancellationToken);
        if (!written.IsSuccess)
        {
            return DomainResult.Fail<ProposeNewSkillFromSignalResult>(written.Failure!);
        }

        var installed = await skills.InstallAsync(
            signal.Value.Signal.InstallationId,
            directory.Value,
            SkillPackageProvenance.AgentProposal,
            request.Roles.Proposer,
            signal.Value.Signal.CorrelationId,
            cancellationToken);
        if (!installed.IsSuccess)
        {
            return DomainResult.Fail<ProposeNewSkillFromSignalResult>(installed.Failure!);
        }
        if (existing is not null)
        {
            return string.Equals(
                    existing.CandidatePackageHash,
                    installed.Value.Version.Package.PackageHash,
                    StringComparison.Ordinal)
                ? DomainResult.Success(new ProposeNewSkillFromSignalResult(existing, true))
                : Conflict<ProposeNewSkillFromSignalResult>(
                    "The replayed proposal content does not match the immutable candidate package.");
        }

        await using var tar = BuildWorkspaceArchive(files);
        var workspace = await artifacts.PutAsync(tar, WorkspaceMediaType, cancellationToken);
        var proposed = await governance.ProposeAsync(new ProposeLearningCandidateRequest(
            request.CandidateId,
            request.SignalId,
            request.SkillProposalId,
            normalized.Value.SkillId,
            normalized.Value.Version,
            workspace,
            request.Roles,
            request.GenerationEvidence), cancellationToken);
        return proposed.IsSuccess
            ? DomainResult.Success(new ProposeNewSkillFromSignalResult(proposed.Value, false))
            : DomainResult.Fail<ProposeNewSkillFromSignalResult>(proposed.Failure!);
    }

    private static DomainResult<NormalizedProposal> Normalize(ProposeNewSkillFromSignalRequest request)
    {
        var description = NormalizeLine(request.Description, 512);
        var permissions = (request.RequestedPermissions ?? [])
            .Select(value => NormalizeLine(value, 256))
            .ToArray();
        var requiredTools = (request.RequiredTools ?? [])
            .Select(value => NormalizeLine(value, 256))
            .ToArray();
        var packageShape = new SkillPackage(
            request.SkillId,
            request.CandidateVersion,
            description ?? string.Empty,
            "proposal",
            [],
            new SkillRequirements(["windows", "linux"], ["text-generation"],
                requiredTools.Where(value => value is not null).Select(value => value!)
                    .Order(StringComparer.Ordinal).ToArray()),
            permissions.Where(value => value is not null).Select(value => value!).Order(StringComparer.Ordinal).ToArray(),
            new Dictionary<string, string>
            {
                ["SKILL.md"] = EmptyHash,
                ["skill.harness.json"] = EmptyHash,
            },
            EmptyHash,
            EmptyHash,
            null);
        var validation = SkillPackageValidator.Validate(packageShape);
        var generatedMarkdown = request.GeneratedMarkdown?.Trim()
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var generation = request.GenerationEvidence;
        var generationValid = ValidGenerationEvidence(request, generatedMarkdown, generation);
        if (request.CandidateId.Value == Guid.Empty || request.SkillProposalId.Value == Guid.Empty ||
            description is null || permissions.Any(value => value is null) ||
            requiredTools.Any(value => value is null) ||
            packageShape.Permissions.Count != permissions.Length || !request.Roles.IsSeparated() ||
            !validation.IsSuccess || !generationValid)
        {
            return DomainResult.Fail<NormalizedProposal>(new DomainFailure(
                FailureCode.ValidationFailure,
                "A proposal requires bounded skill metadata, unique declared permissions, and five separated roles."));
        }

        return DomainResult.Success(new NormalizedProposal(
            request.SkillId,
            request.CandidateVersion,
            description!,
            packageShape.Permissions,
            packageShape.Requirements.ToolIds,
            generatedMarkdown,
            generation));
    }

    private static bool ValidGenerationEvidence(
        ProposeNewSkillFromSignalRequest request,
        string? generatedMarkdown,
        SkillCandidateGenerationEvidence? generation)
    {
        if (generatedMarkdown is null || generation is null)
        {
            return generatedMarkdown is null && generation is null;
        }
        return SkillCandidateDraftParser.Parse(JsonSerializer.Serialize(new { markdown = generatedMarkdown })).IsSuccess &&
            generation.SchemaVersion == 1 && generation.CandidateId == request.CandidateId &&
            generation.SignalId == request.SignalId && generation.SkillId == request.SkillId &&
            generation.CandidateVersion == request.CandidateVersion && generation.AgentId.Value != Guid.Empty &&
            generation.AgentVersion >= 0 && generation.ProviderId.Value != Guid.Empty && generation.ProviderVersion >= 0 &&
            Bounded(generation.Model, 256) && generation.ModelRequestId.Value != Guid.Empty &&
            SkillPackageValidator.IsHash(generation.ModelEvidenceHash) &&
            SkillPackageValidator.IsHash(generation.RawResponseHash) &&
            SkillPackageValidator.IsHash(generation.SelectedMarkdownHash) &&
            SkillPackageValidator.IsHash(generation.GenerationRequestHash) &&
            generation.ContextRedactionCount >= 0 && generation.FinishReason == nameof(ModelFinishReason.Stop) &&
            (generation.RequiredTools ?? []).SequenceEqual(
                (request.RequiredTools ?? []).Order(StringComparer.Ordinal), StringComparer.Ordinal) &&
            string.Equals(generation.SelectedMarkdownHash, Hash(generatedMarkdown), StringComparison.Ordinal);
    }

    private static SortedDictionary<string, byte[]> BuildPackageFiles(
        LearningSignal signal,
        NormalizedProposal proposal)
    {
        var name = proposal.SkillId.Value["skill:".Length..];
        var markdown = proposal.GeneratedMarkdown is null ? $"""
---
name: {name}
description: {JsonSerializer.Serialize(proposal.Description)}
---

# Proposed {name}

This immutable scaffold was generated from governed evidence and is not active.

## Source evidence

> {signal.RedactedSummary.Replace("\n", " ", StringComparison.Ordinal)}

## Proposed behavior

Develop a bounded, repeatable procedure that addresses the source evidence. Preserve explicit inputs, outputs,
failure conditions, verification evidence, and the declared permission boundary. A verifier must replace or refine
this scaffold and pass target, holdout, adversarial, baseline, and permission-diff evaluation before approval.
""" : $"""
---
name: {name}
description: {JsonSerializer.Serialize(proposal.Description)}
---

# Proposed {name}

This immutable candidate was authored by a pinned local model from governed, redacted evidence. It is not active and
the generated procedure is untrusted until the independent evaluation, critique, approval, and canary gates pass.

{SkillCandidateDraftParser.GeneratedStartMarker}
{proposal.GeneratedMarkdown}
{SkillCandidateDraftParser.GeneratedEndMarker}
""";
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            id = proposal.SkillId.Value,
            version = proposal.Version.Value,
            description = proposal.Description,
            dependencies = Array.Empty<object>(),
            requirements = new
            {
                operatingSystems = SupportedOperatingSystems,
                modelCapabilities = TextGenerationCapabilities,
                tools = proposal.RequiredTools,
            },
            permissions = proposal.Permissions,
            signature = (object?)null,
        }, ManifestJson);
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["SKILL.md"] = StrictUtf8.GetBytes(markdown.Replace("\r\n", "\n", StringComparison.Ordinal)),
            ["skill.harness.json"] = manifest,
        };
        if (proposal.GenerationEvidence is not null)
        {
            files["generation.harness.json"] = JsonSerializer.SerializeToUtf8Bytes(
                proposal.GenerationEvidence, ManifestJson);
        }
        return files;
    }

    private static bool GenerationMatchesSignal(
        ProposeNewSkillFromSignalRequest request,
        NormalizedProposal proposal,
        LearningSignal signal) => proposal.GenerationEvidence is null ||
        proposal.GeneratedMarkdown is not null &&
        proposal.GenerationEvidence.SignalId == signal.Id &&
        string.Equals(proposal.GenerationEvidence.SignalHash, signal.SignalHash, StringComparison.Ordinal) &&
        string.Equals(proposal.GenerationEvidence.SourceEvidenceHash, signal.SourceEvidenceHash, StringComparison.Ordinal) &&
        proposal.GenerationEvidence.SkillId == proposal.SkillId &&
        proposal.GenerationEvidence.CandidateVersion == proposal.Version &&
        (proposal.GenerationEvidence.RequiredTools ?? []).SequenceEqual(
            proposal.RequiredTools, StringComparer.Ordinal);

    private DomainResult<string> PrepareWorkspaceDirectory(LearningCandidateId candidateId)
    {
        try
        {
            var dataDirectory = Path.GetFullPath(dataDirectoryProvider.GetDataDirectory());
            var root = Path.GetFullPath(Path.Combine(dataDirectory, "learning", "proposal-workspaces"));
            var directory = Path.GetFullPath(Path.Combine(root, candidateId.Value.ToString("N")));
            var prefix = root.EndsWith(Path.DirectorySeparatorChar)
                ? root
                : root + Path.DirectorySeparatorChar;
            if (!directory.StartsWith(prefix, PathComparison) ||
                Directory.Exists(directory) && IsLinked(directory))
            {
                return Invalid<string>("The proposal workspace escaped its isolated data directory.");
            }

            Directory.CreateDirectory(directory);
            return DomainResult.Success(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or PathTooLongException)
        {
            return External<string>("The isolated proposal workspace could not be created.");
        }
    }

    private static async Task<DomainResult<bool>> WriteExactWorkspaceAsync(
        string directory,
        IReadOnlyDictionary<string, byte[]> files,
        CancellationToken cancellationToken)
    {
        try
        {
            var unexpected = Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/'))
                .Except(files.Keys, StringComparer.Ordinal)
                .Any();
            if (unexpected)
            {
                return Invalid<bool>("The proposal workspace contains unexpected prior content.");
            }

            foreach (var file in files)
            {
                var path = Path.Combine(directory, file.Key);
                if (File.Exists(path))
                {
                    if (IsLinked(path) || !(await File.ReadAllBytesAsync(path, cancellationToken)).AsSpan()
                        .SequenceEqual(file.Value))
                    {
                        return Invalid<bool>("The proposal workspace conflicts with existing content.");
                    }
                    continue;
                }

                await using var stream = new FileStream(
                    path,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    16_384,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
                await stream.WriteAsync(file.Value, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            return DomainResult.Success(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return External<bool>("The isolated proposal workspace could not be materialized safely.");
        }
    }

    private static MemoryStream BuildWorkspaceArchive(IReadOnlyDictionary<string, byte[]> files)
    {
        var output = new MemoryStream();
        using (var writer = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var file in files.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, file.Key)
                {
                    DataStream = new MemoryStream(file.Value, writable: false),
                    ModificationTime = DateTimeOffset.UnixEpoch,
                    Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite |
                        UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                    Uid = 0,
                    Gid = 0,
                    UserName = string.Empty,
                    GroupName = string.Empty,
                };
                writer.WriteEntry(entry);
            }
        }
        output.Position = 0;
        return output;
    }

    private static bool ExistingIdentityMatches(
        LearningCandidate existing,
        ProposeNewSkillFromSignalRequest request,
        NormalizedProposal normalized) =>
        existing.SignalId == request.SignalId && existing.SkillProposalId == request.SkillProposalId &&
        existing.SkillId == normalized.SkillId && existing.CandidateVersion == normalized.Version &&
        existing.RequestedPermissions.SequenceEqual(
            normalized.Permissions, StringComparer.Ordinal) &&
        existing.Roles == request.Roles &&
        (request.RequiredTools ?? []).Order(StringComparer.Ordinal).SequenceEqual(
            normalized.RequiredTools, StringComparer.Ordinal);

    private static string? NormalizeLine(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl))
        {
            return null;
        }
        return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool Bounded(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);

    private static bool IsLinked(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private const string EmptyHash =
        "sha256:0000000000000000000000000000000000000000000000000000000000000000";

    private static string Hash(string value) =>
        $"sha256:{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(StrictUtf8.GetBytes(value)))}";

    private static DomainResult<T> Invalid<T>(string message) =>
        DomainResult.Fail<T>(new DomainFailure(FailureCode.ValidationFailure, message));

    private static DomainResult<T> Conflict<T>(string message) =>
        DomainResult.Fail<T>(new DomainFailure(FailureCode.ConcurrencyConflict, message));

    private static DomainResult<T> External<T>(string message) =>
        DomainResult.Fail<T>(new DomainFailure(FailureCode.RecoverableExternalFailure, message, IsRetryable: true));

    private sealed record NormalizedProposal(
        SkillId SkillId,
        SkillVersion Version,
        string Description,
        IReadOnlyList<string> Permissions,
        IReadOnlyList<string> RequiredTools,
        string? GeneratedMarkdown,
        SkillCandidateGenerationEvidence? GenerationEvidence);
}
