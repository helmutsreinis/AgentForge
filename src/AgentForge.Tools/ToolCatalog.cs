using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using AgentForge.Abstractions.Tools;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Security;
using AgentForge.Domain.Tools;

namespace AgentForge.Tools;

public sealed class ToolCatalog : IToolCatalog
{
    private const ProcessIsolationFeature KnownIsolationFeatures =
        ProcessIsolationFeature.DirectExecutable |
        ProcessIsolationFeature.ArgumentArray |
        ProcessIsolationFeature.EnvironmentAllowlist |
        ProcessIsolationFeature.WorkingDirectoryContainment |
        ProcessIsolationFeature.BoundedOutput |
        ProcessIsolationFeature.WallClockTimeout |
        ProcessIsolationFeature.ProcessTreeTermination |
        ProcessIsolationFeature.KillOnControllerExit |
        ProcessIsolationFeature.NetworkIsolation |
        ProcessIsolationFeature.FileSystemIsolation |
        ProcessIsolationFeature.CpuLimit |
        ProcessIsolationFeature.MemoryLimit |
        ProcessIsolationFeature.ProcessLimit;

    private const ToolSideEffectKind KnownSideEffects =
        ToolSideEffectKind.ReadsFileSystem |
        ToolSideEffectKind.WritesFileSystem |
        ToolSideEffectKind.ReadsNetwork |
        ToolSideEffectKind.ExternalMutation |
        ToolSideEffectKind.CredentialAccess |
        ToolSideEffectKind.PrivilegedOperation |
        ToolSideEffectKind.DestructiveOperation |
        ToolSideEffectKind.PhysicalControl;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly IReadOnlyDictionary<(string Id, string Version), ToolDescriptor> _descriptors;

    private ToolCatalog(IReadOnlyDictionary<(string Id, string Version), ToolDescriptor> descriptors)
    {
        _descriptors = descriptors;
    }

    public static DomainResult<ToolCatalog> Create(IEnumerable<ToolDescriptorDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var descriptors = new Dictionary<(string Id, string Version), ToolDescriptor>();
        foreach (var definition in definitions)
        {
            var normalized = Normalize(definition);
            if (!normalized.IsSuccess)
            {
                return DomainResult.Fail<ToolCatalog>(normalized.Failure!);
            }

            var key = (normalized.Value.Definition.Id, normalized.Value.Definition.Version);
            if (!descriptors.TryAdd(key, normalized.Value))
            {
                return Invalid<ToolCatalog>("Tool catalog contains a duplicate ID and version.");
            }
        }

        return DomainResult.Success(new ToolCatalog(
            new ReadOnlyDictionary<(string Id, string Version), ToolDescriptor>(descriptors)));
    }

    public ValueTask<DomainResult<IReadOnlyList<ToolSummary>>> SearchAsync(
        ToolSearchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsBoundedText(request.Query, 256, allowEmpty: true) ||
            !IsOptionalCatalogId(request.CapabilityId) || request.MaximumResults is < 1 or > 50 ||
            request.MaximumRiskClass is { } maximumRisk && !Enum.IsDefined(maximumRisk))
        {
            return ValueTask.FromResult(Invalid<IReadOnlyList<ToolSummary>>(
                "Tool search requires bounded query, filters, and result count."));
        }

        var tokens = request.Query.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var results = _descriptors.Values
            .Where(item => request.CapabilityId is null ||
                string.Equals(item.Definition.CapabilityId, request.CapabilityId, StringComparison.Ordinal))
            .Where(item => request.MaximumRiskClass is null ||
                item.Definition.RiskClass <= request.MaximumRiskClass)
            .Select(item => new { Descriptor = item, Score = Score(item.Definition, tokens) })
            .Where(item => tokens.Length == 0 || item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Descriptor.Definition.Id, StringComparer.Ordinal)
            .ThenByDescending(item => item.Descriptor.Definition.Version, SemanticVersionComparer.Instance)
            .Take(request.MaximumResults)
            .Select(item => ToSummary(item.Descriptor))
            .ToArray();
        return ValueTask.FromResult(DomainResult.Success<IReadOnlyList<ToolSummary>>(
            Array.AsReadOnly(results)));
    }

    public ValueTask<DomainResult<ToolDescriptor>> DescribeAsync(
        string toolId,
        string version,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCatalogId(toolId) || !IsSemanticVersion(version))
        {
            return ValueTask.FromResult(Invalid<ToolDescriptor>(
                "Tool description requires a normalized ID and semantic version."));
        }

        return ValueTask.FromResult(_descriptors.TryGetValue((toolId, version), out var descriptor)
            ? DomainResult.Success(descriptor)
            : DomainResult.Fail<ToolDescriptor>(new DomainFailure(
                FailureCode.UnsupportedCapability,
                "The exact tool version is not available in the authoritative catalog.")));
    }

    private static DomainResult<ToolDescriptor> Normalize(ToolDescriptorDefinition definition)
    {
        if (definition is null || !IsCatalogId(definition.Id) || !IsSemanticVersion(definition.Version) ||
            !IsBoundedText(definition.Name, 128) || !IsBoundedText(definition.Summary, 512) ||
            !IsBoundedText(definition.Description, 4096) || !IsCatalogId(definition.CapabilityId) ||
            !Enum.IsDefined(definition.RiskClass) || !Enum.IsDefined(definition.TargetKind) ||
            !Enum.IsDefined(definition.OutputSensitivity) || !Enum.IsDefined(definition.OperationKind) ||
            (definition.SideEffects & ~KnownSideEffects) != 0 ||
            definition.RiskClass < MinimumRisk(definition.SideEffects) ||
            !ValidateProvenance(definition.Provenance) || definition.Parameters is null ||
            definition.Process is null || definition.Parameters.Count > 64)
        {
            return Invalid<ToolDescriptor>("Tool descriptor identity, provenance, risk, or bounds are invalid.");
        }

        var parameterNames = new HashSet<string>(StringComparer.Ordinal);
        var parameters = new List<ToolParameterDescriptor>(definition.Parameters.Count);
        foreach (var parameter in definition.Parameters)
        {
            if (!ValidateParameter(parameter) || !parameterNames.Add(parameter.Name))
            {
                return Invalid<ToolDescriptor>("Tool parameters must be unique, typed, and bounded.");
            }

            parameters.Add(parameter with
            {
                AllowedValues = Array.AsReadOnly(parameter.AllowedValues.ToArray()),
            });
        }

        if (definition.TargetKind is AuthorizationTargetKind.None)
        {
            if (definition.TargetParameterName is not null)
            {
                return Invalid<ToolDescriptor>("A target parameter is forbidden for a targetless tool.");
            }
        }
        else if (definition.TargetParameterName is null ||
            parameters.Find(item => string.Equals(
                item.Name,
                definition.TargetParameterName,
                StringComparison.Ordinal)) is not { Type: ToolParameterType.Text, Required: true })
        {
            return Invalid<ToolDescriptor>(
                "A targeted tool must bind its target to a required text parameter.");
        }

        var process = NormalizeProcess(definition.Process, parameters);
        if (!process.IsSuccess)
        {
            return DomainResult.Fail<ToolDescriptor>(process.Failure!);
        }

        if (definition.OperationKind is ToolOperationKind.AvailabilityProbe &&
            !ValidateAvailabilityProbe(definition, process.Value))
        {
            return Invalid<ToolDescriptor>(
                "Availability probes require inventory-only authority and strict isolated bounds.");
        }

        var normalized = definition with
        {
            Parameters = new ReadOnlyCollection<ToolParameterDescriptor>(parameters),
            Process = process.Value,
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(normalized, SerializerOptions);
        return DomainResult.Success(new ToolDescriptor(
            normalized,
            $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}"));
    }

    private static DomainResult<ToolProcessDefinition> NormalizeProcess(
        ToolProcessDefinition process,
        IReadOnlyList<ToolParameterDescriptor> parameters)
    {
        if (string.IsNullOrWhiteSpace(process.ExecutablePath) || process.ExecutablePath.Length > 2048 ||
            process.ExecutablePath.Any(char.IsControl) || !Path.IsPathFullyQualified(process.ExecutablePath) ||
            process.FixedArguments is null || process.ArgumentBindings is null ||
            process.AllowedEnvironmentVariables is null || process.FixedArguments.Count > 256 ||
            process.ArgumentBindings.Count > 256 || process.AllowedEnvironmentVariables.Count > 64 ||
            !Enum.IsDefined(process.RequiredSandbox) || !Enum.IsDefined(process.NetworkPolicy) ||
            (process.RequiredFeatures & ~KnownIsolationFeatures) != 0 ||
            process.TimeoutSeconds is < 1 or > 3600 ||
            process.MaximumOutputBytes is < 1 or > 16_777_216 ||
            process.FixedArguments.Any(item => !IsBoundedArgument(item)))
        {
            return Invalid<ToolProcessDefinition>("Tool process definition is not bounded or portable.");
        }

        string executable;
        try
        {
            executable = Path.GetFullPath(process.ExecutablePath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Invalid<ToolProcessDefinition>("Tool executable path cannot be normalized.");
        }

        var parameterMap = parameters.ToDictionary(item => item.Name, StringComparer.Ordinal);
        var bound = new HashSet<string>(StringComparer.Ordinal);
        var bindings = new List<ToolArgumentBinding>(process.ArgumentBindings.Count);
        foreach (var binding in process.ArgumentBindings)
        {
            if (!ValidateBinding(binding, parameterMap) ||
                binding.ParameterName is not null && !bound.Add(binding.ParameterName))
            {
                return Invalid<ToolProcessDefinition>(
                    "Tool argument bindings must be valid and bind each parameter once.");
            }

            bindings.Add(binding);
        }

        if (!bound.SetEquals(parameterMap.Keys))
        {
            return Invalid<ToolProcessDefinition>("Every tool parameter must have one argument binding.");
        }

        var environmentComparer = StringComparer.OrdinalIgnoreCase;
        if (process.AllowedEnvironmentVariables.Any(item => !IsEnvironmentName(item)) ||
            process.AllowedEnvironmentVariables.Distinct(environmentComparer).Count() !=
            process.AllowedEnvironmentVariables.Count)
        {
            return Invalid<ToolProcessDefinition>("Tool environment names must be unique and portable.");
        }

        return DomainResult.Success(process with
        {
            ExecutablePath = executable,
            FixedArguments = Array.AsReadOnly(process.FixedArguments.ToArray()),
            ArgumentBindings = new ReadOnlyCollection<ToolArgumentBinding>(bindings),
            AllowedEnvironmentVariables = Array.AsReadOnly(process.AllowedEnvironmentVariables.ToArray()),
        });
    }

    private static bool ValidateParameter(ToolParameterDescriptor parameter)
    {
        if (parameter is null || !IsParameterName(parameter.Name) || !Enum.IsDefined(parameter.Type) ||
            !IsBoundedText(parameter.Description, 512) || parameter.AllowedValues is null ||
            parameter.AllowedValues.Count > 64 || parameter.AllowedValues.Any(item => !IsBoundedText(item, 1024)) ||
            parameter.AllowedValues.Distinct(StringComparer.Ordinal).Count() != parameter.AllowedValues.Count)
        {
            return false;
        }

        return parameter.Type switch
        {
            ToolParameterType.Text => parameter.MaximumLength is >= 1 and <= 8192 &&
                parameter.MinimumInteger is null && parameter.MaximumInteger is null &&
                parameter.AllowedValues.All(item => item.Length <= parameter.MaximumLength),
            ToolParameterType.WholeNumber => parameter.MaximumLength == 0 && parameter.AllowedValues.Count == 0 &&
                parameter.MinimumInteger is not null && parameter.MaximumInteger is not null &&
                parameter.MinimumInteger <= parameter.MaximumInteger,
            ToolParameterType.Switch => parameter.MaximumLength == 0 && parameter.AllowedValues.Count == 0 &&
                parameter.MinimumInteger is null && parameter.MaximumInteger is null,
            _ => false,
        };
    }

    private static bool ValidateAvailabilityProbe(
        ToolDescriptorDefinition definition,
        ToolProcessDefinition process) =>
        string.Equals(definition.CapabilityId, "tool:availability.probe", StringComparison.Ordinal) &&
        definition.RiskClass is CapabilityRiskClass.Inventory &&
        definition.TargetKind is AuthorizationTargetKind.None && definition.TargetParameterName is null &&
        definition.SideEffects is ToolSideEffectKind.None &&
        definition.OutputSensitivity is not ToolOutputSensitivity.PotentiallySensitive &&
        definition.Parameters.Count == 0 && process.AllowedEnvironmentVariables.Count == 0 &&
        process.RequiredSandbox is ProcessSandboxKind.Container &&
        process.NetworkPolicy is ProcessNetworkPolicy.Denied && process.TimeoutSeconds <= 30 &&
        process.MaximumOutputBytes <= 65_536 &&
        process.RequiredFeatures.HasFlag(ProcessIsolationFeature.NetworkIsolation) &&
        process.FixedArguments.Count + process.ArgumentBindings.Count > 0;

    private static bool ValidateBinding(
        ToolArgumentBinding binding,
        Dictionary<string, ToolParameterDescriptor> parameters)
    {
        if (binding is null || !Enum.IsDefined(binding.Kind))
        {
            return false;
        }

        if (binding.Kind is ToolArgumentBindingKind.Literal)
        {
            return binding.ParameterName is null && IsBoundedArgument(binding.Token);
        }

        if (binding.ParameterName is null || !parameters.TryGetValue(binding.ParameterName, out var parameter))
        {
            return false;
        }

        return binding.Kind switch
        {
            ToolArgumentBindingKind.Positional => parameter.Type is not ToolParameterType.Switch &&
                binding.Token is null,
            ToolArgumentBindingKind.NamedValue => parameter.Type is not ToolParameterType.Switch &&
                IsOptionToken(binding.Token),
            ToolArgumentBindingKind.BooleanSwitch => parameter.Type is ToolParameterType.Switch &&
                IsOptionToken(binding.Token),
            _ => false,
        };
    }

    private static CapabilityRiskClass MinimumRisk(ToolSideEffectKind sideEffects)
    {
        if (sideEffects.HasFlag(ToolSideEffectKind.PhysicalControl))
        {
            return CapabilityRiskClass.PhysicalControl;
        }

        if (sideEffects.HasFlag(ToolSideEffectKind.DestructiveOperation))
        {
            return CapabilityRiskClass.Destructive;
        }

        if (sideEffects.HasFlag(ToolSideEffectKind.PrivilegedOperation))
        {
            return CapabilityRiskClass.Privileged;
        }

        if (sideEffects.HasFlag(ToolSideEffectKind.CredentialAccess))
        {
            return CapabilityRiskClass.Credential;
        }

        if (sideEffects.HasFlag(ToolSideEffectKind.ExternalMutation))
        {
            return CapabilityRiskClass.ExternalMutation;
        }

        if (sideEffects.HasFlag(ToolSideEffectKind.WritesFileSystem))
        {
            return CapabilityRiskClass.Write;
        }

        return sideEffects is ToolSideEffectKind.None ? CapabilityRiskClass.Inventory : CapabilityRiskClass.Read;
    }

    private static int Score(ToolDescriptorDefinition definition, IReadOnlyList<string> tokens)
    {
        var score = 0;
        foreach (var token in tokens)
        {
            if (string.Equals(definition.Id, token, StringComparison.OrdinalIgnoreCase))
            {
                score += 100;
            }
            else if (definition.Id.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                score += 40;
            }

            if (definition.Name.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                score += 30;
            }

            if (definition.Summary.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                definition.CapabilityId.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                score += 10;
            }
        }

        return score;
    }

    private static ToolSummary ToSummary(ToolDescriptor descriptor) => new(
        descriptor.Definition.Id,
        descriptor.Definition.Version,
        descriptor.Definition.Name,
        descriptor.Definition.Summary,
        descriptor.Definition.CapabilityId,
        descriptor.Definition.RiskClass,
        descriptor.Definition.TargetKind,
        descriptor.Definition.SideEffects,
        descriptor.Definition.Provenance.SourceKind,
        descriptor.Definition.Provenance.TrustLevel,
        descriptor.Definition.OperationKind,
        descriptor.DescriptorHash);

    private static bool ValidateProvenance(ToolProvenance provenance) =>
        provenance is not null && Enum.IsDefined(provenance.SourceKind) && Enum.IsDefined(provenance.TrustLevel) &&
        provenance.SourceKind switch
        {
            ToolCatalogSourceKind.BuiltIn => provenance.TrustLevel is ToolTrustLevel.BuiltIn,
            ToolCatalogSourceKind.OperatorConfigured => provenance.TrustLevel is ToolTrustLevel.OperatorApproved,
            ToolCatalogSourceKind.SignatureVerifiedPlugin => provenance.TrustLevel is ToolTrustLevel.SignatureVerified,
            _ => false,
        } && IsCatalogId(provenance.SourceId) && IsSemanticVersion(provenance.SourceVersion) &&
        IsSha256(provenance.EvidenceHash);

    private static bool IsSemanticVersion(string? value) =>
        SemanticVersion.TryParse(value, out _);

    private static bool IsNumericVersionPart(string value) =>
        value.Length > 0 && (value.Length == 1 || value[0] != '0') && value.All(char.IsAsciiDigit);

    private static bool IsVersionIdentifier(string value) =>
        value.Length > 0 && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character == '-');

    private static bool IsCatalogId(string? value) =>
        IsBoundedText(value, 256) && IsCatalogIdBoundary(value![0]) && IsCatalogIdBoundary(value[^1]) &&
        value.All(character =>
            IsCatalogIdBoundary(character) || character is '.' or ':' or '_' or '-');

    private static bool IsCatalogIdBoundary(char character) =>
        character is >= 'a' and <= 'z' || char.IsAsciiDigit(character);

    private static bool IsOptionalCatalogId(string? value) => value is null || IsCatalogId(value);

    private static bool IsParameterName(string? value) =>
        IsBoundedText(value, 128) && (char.IsAsciiLetter(value![0]) || value[0] == '_') &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    private static bool IsEnvironmentName(string? value) =>
        IsParameterName(value);

    private static bool IsBoundedArgument(string? value) =>
        value is not null && value.Length <= 8192 && !value.Contains('\0');

    private static bool IsOptionToken(string? value) =>
        IsBoundedArgument(value) && value!.Length is >= 2 and <= 128 && value[0] == '-' &&
        value.Skip(1).All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static bool IsBoundedText(string? value, int maximumLength, bool allowEmpty = false) =>
        value is not null && value.Length <= maximumLength && (allowEmpty || !string.IsNullOrWhiteSpace(value)) &&
        !value.Any(char.IsControl) && (allowEmpty || string.Equals(value, value.Trim(), StringComparison.Ordinal));

    private static bool IsSha256(string value)
    {
        if (value.Length != 71 || !value.StartsWith("sha256:", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var character in value.AsSpan(7))
        {
            if (!char.IsAsciiDigit(character) && character is not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static DomainResult<T> Invalid<T>(string message) =>
        DomainResult.Fail<T>(new DomainFailure(FailureCode.ValidationFailure, message));

    private sealed class SemanticVersionComparer : IComparer<string>
    {
        public static SemanticVersionComparer Instance { get; } = new();

        public int Compare(string? first, string? second)
        {
            if (ReferenceEquals(first, second))
            {
                return 0;
            }

            if (first is null)
            {
                return -1;
            }

            if (second is null)
            {
                return 1;
            }

            _ = SemanticVersion.TryParse(first, out var firstVersion);
            _ = SemanticVersion.TryParse(second, out var secondVersion);
            return firstVersion!.CompareTo(secondVersion);
        }
    }

    private sealed class SemanticVersion : IComparable<SemanticVersion>
    {
        private SemanticVersion(string[] core, string[] prerelease)
        {
            Core = core;
            Prerelease = prerelease;
        }

        private string[] Core { get; }

        private string[] Prerelease { get; }

        public static bool TryParse(string? value, out SemanticVersion? version)
        {
            version = null;
            if (!IsBoundedText(value, 128))
            {
                return false;
            }

            var buildSeparator = value!.IndexOf('+');
            if (buildSeparator != value.LastIndexOf('+'))
            {
                return false;
            }

            var precedence = buildSeparator < 0 ? value : value[..buildSeparator];
            var build = buildSeparator < 0 ? null : value[(buildSeparator + 1)..];
            if (build is not null && !IsIdentifierList(build, rejectNumericLeadingZero: false))
            {
                return false;
            }

            var prereleaseSeparator = precedence.IndexOf('-');
            var coreText = prereleaseSeparator < 0 ? precedence : precedence[..prereleaseSeparator];
            var prereleaseText = prereleaseSeparator < 0 ? null : precedence[(prereleaseSeparator + 1)..];
            var core = coreText.Split('.');
            if (core.Length != 3 || core.Any(part => !IsNumericVersionPart(part)) ||
                prereleaseText is not null && !IsIdentifierList(prereleaseText, rejectNumericLeadingZero: true))
            {
                return false;
            }

            version = new SemanticVersion(core, prereleaseText?.Split('.') ?? []);
            return true;
        }

        public int CompareTo(SemanticVersion? other)
        {
            if (other is null)
            {
                return 1;
            }

            for (var index = 0; index < Core.Length; index++)
            {
                var comparison = CompareNumeric(Core[index], other.Core[index]);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            if (Prerelease.Length == 0 || other.Prerelease.Length == 0)
            {
                return Prerelease.Length == other.Prerelease.Length ? 0 : Prerelease.Length == 0 ? 1 : -1;
            }

            for (var index = 0; index < Math.Min(Prerelease.Length, other.Prerelease.Length); index++)
            {
                var firstNumeric = Prerelease[index].All(char.IsAsciiDigit);
                var secondNumeric = other.Prerelease[index].All(char.IsAsciiDigit);
                int comparison;
                if (firstNumeric && secondNumeric)
                {
                    comparison = CompareNumeric(Prerelease[index], other.Prerelease[index]);
                }
                else if (firstNumeric != secondNumeric)
                {
                    comparison = firstNumeric ? -1 : 1;
                }
                else
                {
                    comparison = string.CompareOrdinal(Prerelease[index], other.Prerelease[index]);
                }

                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return Prerelease.Length.CompareTo(other.Prerelease.Length);
        }

        private static bool IsIdentifierList(string value, bool rejectNumericLeadingZero) =>
            value.Split('.').All(identifier => IsVersionIdentifier(identifier) &&
                (!rejectNumericLeadingZero || !identifier.All(char.IsAsciiDigit) ||
                    identifier.Length == 1 || identifier[0] != '0'));

        private static int CompareNumeric(string first, string second)
        {
            var length = first.Length.CompareTo(second.Length);
            return length != 0 ? length : string.CompareOrdinal(first, second);
        }
    }
}
