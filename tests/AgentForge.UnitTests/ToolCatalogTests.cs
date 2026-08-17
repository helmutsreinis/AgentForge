using AgentForge.Domain.Primitives;
using AgentForge.Domain.Security;
using AgentForge.Domain.Tools;
using AgentForge.Tools;

namespace AgentForge.UnitTests;

public sealed class ToolCatalogTests
{
    private const string EvidenceHash =
        "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task Catalog_snapshots_mutable_input_and_hashes_the_normalized_descriptor()
    {
        var allowedValues = new List<string> { "json" };
        var parameters = new List<ToolParameterDescriptor>
        {
            TextParameter("path"),
            TextParameter("format") with { AllowedValues = allowedValues },
        };
        var fixedArguments = new List<string> { "inspect" };
        var bindings = new List<ToolArgumentBinding>
        {
            new(ToolArgumentBindingKind.NamedValue, "path", "--path"),
            new(ToolArgumentBindingKind.NamedValue, "format", "--format"),
        };
        var environment = new List<string> { "AF_FORMAT" };
        var definition = ValidDefinition() with
        {
            Parameters = parameters,
            Process = ValidDefinition().Process with
            {
                FixedArguments = fixedArguments,
                ArgumentBindings = bindings,
                AllowedEnvironmentVariables = environment,
            },
        };

        var result = ToolCatalog.Create([definition]);
        Assert.True(result.IsSuccess, result.Failure?.Message);
        var originalHash = (await result.Value.DescribeAsync(
            definition.Id,
            definition.Version,
            CancellationToken.None)).Value.DescriptorHash;

        allowedValues.Add("xml");
        parameters.Clear();
        fixedArguments.Clear();
        bindings.Clear();
        environment.Clear();

        var descriptor = await result.Value.DescribeAsync(
            definition.Id,
            definition.Version,
            CancellationToken.None);
        var recreated = ToolCatalog.Create([definition with
        {
            Parameters =
            [
                TextParameter("path"),
                TextParameter("format") with { AllowedValues = ["json"] },
            ],
            Process = definition.Process with
            {
                FixedArguments = ["inspect"],
                ArgumentBindings =
                [
                    new(ToolArgumentBindingKind.NamedValue, "path", "--path"),
                    new(ToolArgumentBindingKind.NamedValue, "format", "--format"),
                ],
                AllowedEnvironmentVariables = ["AF_FORMAT"],
            },
        }]);
        var recreatedDescriptor = await recreated.Value.DescribeAsync(
            definition.Id,
            definition.Version,
            CancellationToken.None);

        Assert.True(descriptor.IsSuccess, descriptor.Failure?.Message);
        Assert.Equal(2, descriptor.Value.Definition.Parameters.Count);
        Assert.Equal(["json"], descriptor.Value.Definition.Parameters[1].AllowedValues);
        Assert.Equal(["inspect"], descriptor.Value.Definition.Process.FixedArguments);
        Assert.Equal(["AF_FORMAT"], descriptor.Value.Definition.Process.AllowedEnvironmentVariables);
        Assert.Equal(originalHash, recreatedDescriptor.Value.DescriptorHash);
    }

    [Fact]
    public async Task Search_is_progressive_filtered_and_uses_semantic_version_precedence()
    {
        var versions = new[] { "1.2.0", "2.0.0-rc.1", "1.10.0+build.7", "2.0.0" };
        var definitions = versions.Select(version => ValidDefinition() with { Version = version }).Append(
            ReadDefinition());
        var catalog = ToolCatalog.Create(definitions).Value;

        var results = await catalog.SearchAsync(
            new ToolSearchRequest("file", null, null, 10),
            CancellationToken.None);
        var writeVersions = results.Value
            .Where(item => item.Id == "tool:file.write")
            .Select(item => item.Version)
            .ToArray();
        var readOnly = await catalog.SearchAsync(
            new ToolSearchRequest("", null, CapabilityRiskClass.Read, 10),
            CancellationToken.None);
        var capabilityOnly = await catalog.SearchAsync(
            new ToolSearchRequest("", "tool:file.write", null, 2),
            CancellationToken.None);

        Assert.Equal(["2.0.0", "2.0.0-rc.1", "1.10.0+build.7", "1.2.0"], writeVersions);
        Assert.Single(readOnly.Value);
        Assert.Equal("tool:file.read", readOnly.Value[0].Id);
        Assert.Equal(2, capabilityOnly.Value.Count);
        Assert.DoesNotContain(
            typeof(ToolSummary).GetProperties(),
            property => property.PropertyType == typeof(ToolProcessDefinition));
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("0.0.0-alpha")]
    [InlineData("1.2.3-alpha.1+windows.20260811")]
    [InlineData("999999999999999999999999.0.1")]
    public void Catalog_accepts_semantic_version_two_values(string version)
    {
        var result = ToolCatalog.Create([ValidDefinition() with { Version = version }]);

        Assert.True(result.IsSuccess, result.Failure?.Message);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("1.0")]
    [InlineData("01.0.0")]
    [InlineData("1.0.0-01")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0+")]
    [InlineData("1.0.0+build+second")]
    [InlineData("1.0.0 alpha")]
    public void Catalog_rejects_invalid_semantic_versions(string version)
    {
        var result = ToolCatalog.Create([ValidDefinition() with { Version = version }]);

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureCode.ValidationFailure, result.Failure?.Code);
    }

    [Fact]
    public void Catalog_rejects_duplicate_exact_versions()
    {
        var definition = ValidDefinition();

        var result = ToolCatalog.Create([definition, definition]);

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureCode.ValidationFailure, result.Failure?.Code);
    }

    [Fact]
    public void Catalog_rejects_unsafe_or_ambiguous_descriptors()
    {
        var definition = ValidDefinition();
        var invalid = new ToolDescriptorDefinition[]
        {
            definition with { Id = "Tool:Uppercase" },
            definition with { Id = ":tool:file.write" },
            definition with { RiskClass = CapabilityRiskClass.Read },
            definition with { SideEffects = (ToolSideEffectKind)(1 << 20) },
            definition with { OutputSensitivity = (ToolOutputSensitivity)999 },
            definition with { OperationKind = (ToolOperationKind)999 },
            definition with { TargetParameterName = null },
            definition with
            {
                Parameters = [TextParameter("path") with { Required = false }],
            },
            definition with
            {
                Parameters = [SwitchParameter("path")],
                Process = definition.Process with
                {
                    ArgumentBindings =
                    [
                        new(ToolArgumentBindingKind.BooleanSwitch, "path", "--path"),
                    ],
                },
            },
            definition with
            {
                Parameters = [TextParameter("path") with { MaximumLength = 2, AllowedValues = ["long"] }],
            },
            definition with
            {
                Provenance = definition.Provenance with { EvidenceHash = "sha256:not-a-hash" },
            },
            definition with
            {
                Provenance = definition.Provenance with
                {
                    SourceKind = ToolCatalogSourceKind.SignatureVerifiedPlugin,
                },
            },
            definition with { Process = definition.Process with { ExecutablePath = "relative-tool" } },
            definition with
            {
                Process = definition.Process with
                {
                    AllowedEnvironmentVariables = ["PATH", "Path"],
                },
            },
            definition with
            {
                Process = definition.Process with { RequiredFeatures = (ProcessIsolationFeature)(1 << 20) },
            },
            definition with { Process = definition.Process with { TimeoutSeconds = 0 } },
            definition with { Process = definition.Process with { ArgumentBindings = [] } },
            definition with
            {
                Parameters = [TextParameter("path"), TextParameter("path")],
                Process = definition.Process with
                {
                    ArgumentBindings =
                    [
                        new(ToolArgumentBindingKind.NamedValue, "path", "--path"),
                    ],
                },
            },
        };

        foreach (var candidate in invalid)
        {
            var result = ToolCatalog.Create([candidate]);
            Assert.False(result.IsSuccess);
            Assert.Equal(FailureCode.ValidationFailure, result.Failure?.Code);
        }
    }

    [Fact]
    public async Task Catalog_admits_only_strict_inventory_only_availability_probes()
    {
        var definition = ValidProbeDefinition();

        var result = ToolCatalog.Create([definition]);

        Assert.True(result.IsSuccess, result.Failure?.Message);
        var descriptor = await result.Value.DescribeAsync(
            definition.Id,
            definition.Version,
            CancellationToken.None);
        var summaries = await result.Value.SearchAsync(
            new ToolSearchRequest("availability", null, CapabilityRiskClass.Inventory),
            CancellationToken.None);
        Assert.True(descriptor.IsSuccess, descriptor.Failure?.Message);
        Assert.Equal(ToolOperationKind.AvailabilityProbe, descriptor.Value.Definition.OperationKind);
        Assert.Single(summaries.Value);
        Assert.Equal(ToolOperationKind.AvailabilityProbe, summaries.Value[0].OperationKind);
    }

    [Fact]
    public void Catalog_rejects_availability_probes_that_can_expand_authority_or_bounds()
    {
        var definition = ValidProbeDefinition();
        var pathParameter = TextParameter("path");
        var invalid = new ToolDescriptorDefinition[]
        {
            definition with { CapabilityId = "tool:repo.read" },
            definition with { RiskClass = CapabilityRiskClass.Read },
            definition with
            {
                TargetKind = AuthorizationTargetKind.FileSystemPath,
                TargetParameterName = "path",
                Parameters = [pathParameter],
                Process = definition.Process with
                {
                    ArgumentBindings =
                    [
                        new ToolArgumentBinding(ToolArgumentBindingKind.NamedValue, "path", "--path"),
                    ],
                },
            },
            definition with
            {
                RiskClass = CapabilityRiskClass.Read,
                SideEffects = ToolSideEffectKind.ReadsFileSystem,
            },
            definition with { OutputSensitivity = ToolOutputSensitivity.PotentiallySensitive },
            definition with
            {
                Process = definition.Process with { AllowedEnvironmentVariables = ["PATH"] },
            },
            definition with
            {
                Process = definition.Process with { NetworkPolicy = ProcessNetworkPolicy.LoopbackOnly },
            },
            definition with
            {
                Process = definition.Process with { RequiredSandbox = ProcessSandboxKind.RestrictedHost },
            },
            definition with { Process = definition.Process with { TimeoutSeconds = 31 } },
            definition with { Process = definition.Process with { MaximumOutputBytes = 65_537 } },
            definition with
            {
                Process = definition.Process with
                {
                    RequiredFeatures = ProcessIsolationFeature.DirectExecutable |
                        ProcessIsolationFeature.ArgumentArray,
                },
            },
            definition with { Process = definition.Process with { FixedArguments = [] } },
        };

        foreach (var candidate in invalid)
        {
            var result = ToolCatalog.Create([candidate]);
            Assert.False(result.IsSuccess);
            Assert.Equal(FailureCode.ValidationFailure, result.Failure?.Code);
        }
    }

    [Fact]
    public void Catalog_admission_does_not_inspect_or_execute_the_candidate_path()
    {
        var sentinel = Path.Combine(Path.GetTempPath(), $"agentforge-catalog-{Guid.NewGuid():N}");
        Assert.False(File.Exists(sentinel));

        var result = ToolCatalog.Create([ValidDefinition() with
        {
            Process = ValidDefinition().Process with { ExecutablePath = sentinel },
        }]);

        Assert.True(result.IsSuccess, result.Failure?.Message);
        Assert.False(File.Exists(sentinel));
    }

    [Fact]
    public async Task Describe_requires_an_exact_catalog_identity()
    {
        var catalog = ToolCatalog.Create([ValidDefinition()]).Value;

        var wrongVersion = await catalog.DescribeAsync(
            "tool:file.write",
            "2.0.0",
            CancellationToken.None);
        var malformedIdentity = await catalog.DescribeAsync(
            "TOOL:file.write",
            "1.0.0",
            CancellationToken.None);

        Assert.Equal(FailureCode.UnsupportedCapability, wrongVersion.Failure?.Code);
        Assert.Equal(FailureCode.ValidationFailure, malformedIdentity.Failure?.Code);
    }

    [Fact]
    public async Task Search_rejects_unbounded_inputs_and_honors_cancellation()
    {
        var catalog = ToolCatalog.Create([ValidDefinition()]).Value;
        var invalid = await catalog.SearchAsync(
            new ToolSearchRequest(new string('x', 257), null, null, 51),
            CancellationToken.None);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        Assert.Equal(FailureCode.ValidationFailure, invalid.Failure?.Code);
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await catalog.SearchAsync(
                new ToolSearchRequest("", null, null),
                canceled.Token));
    }

    [Fact]
    public async Task Descriptor_hash_changes_with_security_relevant_configuration()
    {
        var first = ValidDefinition();
        var second = first with
        {
            Process = first.Process with
            {
                ExecutablePath = Path.Combine(Path.GetTempPath(), "agentforge-other-tool"),
            },
        };
        var firstCatalog = ToolCatalog.Create([first]).Value;
        var secondCatalog = ToolCatalog.Create([second]).Value;
        var firstDescriptor = await firstCatalog.DescribeAsync(first.Id, first.Version, CancellationToken.None);
        var secondDescriptor = await secondCatalog.DescribeAsync(second.Id, second.Version, CancellationToken.None);

        Assert.NotEqual(firstDescriptor.Value.DescriptorHash, secondDescriptor.Value.DescriptorHash);
    }

    [Fact]
    public void Built_in_network_tool_requires_one_fixed_https_target_without_mutation()
    {
        var valid = FixedEndpointDefinition();
        Assert.True(ToolCatalog.Create([valid]).IsSuccess);

        var target = valid.Parameters.Single(item => item.Name == "endpoint");
        var invalid = new[]
        {
            valid with { Parameters = [target with { AllowedValues = [] }] },
            valid with { Parameters = [target with { AllowedValues = ["http://api.example.test/search"] }] },
            valid with { SideEffects = valid.SideEffects | ToolSideEffectKind.ExternalMutation },
            valid with { Process = valid.Process with { NetworkPolicy = ProcessNetworkPolicy.InheritHost } },
        };
        Assert.All(invalid, item => Assert.False(ToolCatalog.Create([item]).IsSuccess));
    }

    [Fact]
    public void Generated_skill_http_tool_allows_only_the_exact_managed_dynamic_target_contract()
    {
        var fixedTool = FixedEndpointDefinition();
        var valid = fixedTool with
        {
            Id = "tool:http-api.get",
            Name = "Read configured API",
            Summary = "Reads one operator-configured HTTPS API profile.",
            Description = "Resolves and revalidates one exact profile endpoint inside the built-in handler.",
            CapabilityId = "tool:http-api.read",
            OutputSensitivity = ToolOutputSensitivity.PotentiallySensitive,
            Parameters = [fixedTool.Parameters[0] with { AllowedValues = [] }],
            Provenance = fixedTool.Provenance with { SourceId = "agentforge.generated-skill-http-api" },
            BuiltInHandlerId = "http-api.get",
        };

        Assert.True(ToolCatalog.Create([valid]).IsSuccess);
        Assert.False(ToolCatalog.Create([valid with { Id = "tool:http-api.other" }]).IsSuccess);
        Assert.False(ToolCatalog.Create([valid with
        {
            Provenance = valid.Provenance with { SourceId = "untrusted.dynamic-target" },
        }]).IsSuccess);
        Assert.False(ToolCatalog.Create([valid with
        {
            SideEffects = ToolSideEffectKind.ReadsNetwork,
            RiskClass = CapabilityRiskClass.Read,
        }]).IsSuccess);
    }

    private static ToolDescriptorDefinition ValidDefinition() => new(
        "tool:file.write",
        "1.0.0",
        "Write file",
        "Writes bounded content to one workspace file.",
        "Writes a bounded payload to the exact authorized workspace path.",
        "tool:file.write",
        CapabilityRiskClass.Write,
        AuthorizationTargetKind.FileSystemPath,
        "path",
        ToolSideEffectKind.WritesFileSystem,
        ToolOutputSensitivity.PotentiallySensitive,
        [TextParameter("path")],
        new ToolProcessDefinition(
            Path.Combine(Path.GetTempPath(), "agentforge-tool"),
            [],
            [new ToolArgumentBinding(ToolArgumentBindingKind.NamedValue, "path", "--path")],
            [],
            ProcessSandboxKind.RestrictedHost,
            ProcessNetworkPolicy.InheritHost,
            ProcessIsolationFeature.DirectExecutable | ProcessIsolationFeature.ArgumentArray,
            30,
            65_536),
        new ToolProvenance(
            ToolCatalogSourceKind.BuiltIn,
            ToolTrustLevel.BuiltIn,
            "agentforge.tools",
            "1.0.0",
            EvidenceHash));

    private static ToolDescriptorDefinition ReadDefinition() => ValidDefinition() with
    {
        Id = "tool:file.read",
        Name = "Read file",
        Summary = "Reads one workspace file.",
        Description = "Reads the exact authorized workspace file.",
        CapabilityId = "tool:file.read",
        RiskClass = CapabilityRiskClass.Read,
        SideEffects = ToolSideEffectKind.ReadsFileSystem,
    };

    private static ToolDescriptorDefinition FixedEndpointDefinition() => new(
        "tool:search.fixture",
        "1.0.0",
        "Search fixture",
        "Searches one fixed endpoint.",
        "Returns bounded public citations from an exact HTTPS target.",
        "tool:search.web",
        CapabilityRiskClass.Credential,
        AuthorizationTargetKind.Uri,
        "endpoint",
        ToolSideEffectKind.ReadsNetwork | ToolSideEffectKind.CredentialAccess,
        ToolOutputSensitivity.Public,
        [TextParameter("endpoint") with { AllowedValues = ["https://api.example.test/search"] }],
        new ToolProcessDefinition(
            Path.Combine(Path.GetTempPath(), "agentforge-search-tool"),
            [], [new ToolArgumentBinding(ToolArgumentBindingKind.Positional, "endpoint", null)], [],
            ProcessSandboxKind.BuiltIn, ProcessNetworkPolicy.FixedEndpointOnly,
            ProcessIsolationFeature.BoundedOutput | ProcessIsolationFeature.NetworkIsolation,
            30, 65_536),
        new ToolProvenance(
            ToolCatalogSourceKind.BuiltIn,
            ToolTrustLevel.BuiltIn,
            "agentforge.search",
            "1.0.0",
            EvidenceHash),
        ExecutionKind: ToolExecutionKind.BuiltIn,
        BuiltInHandlerId: "search.fixture");

    private static ToolDescriptorDefinition ValidProbeDefinition() => new(
        "tool:fixture.availability",
        "1.0.0",
        "Fixture availability",
        "Checks whether the fixture tool is available.",
        "Runs one bounded and isolated version probe without parameters.",
        "tool:availability.probe",
        CapabilityRiskClass.Inventory,
        AuthorizationTargetKind.None,
        null,
        ToolSideEffectKind.None,
        ToolOutputSensitivity.LocalMetadata,
        [],
        new ToolProcessDefinition(
            Path.Combine(Path.GetTempPath(), "agentforge-probe-tool"),
            ["--version"],
            [],
            [],
            ProcessSandboxKind.Container,
            ProcessNetworkPolicy.Denied,
            ProcessIsolationFeature.DirectExecutable |
                ProcessIsolationFeature.ArgumentArray |
                ProcessIsolationFeature.NetworkIsolation,
            5,
            4096),
        new ToolProvenance(
            ToolCatalogSourceKind.BuiltIn,
            ToolTrustLevel.BuiltIn,
            "agentforge.tools",
            "1.0.0",
            EvidenceHash),
        ToolOperationKind.AvailabilityProbe);

    private static ToolParameterDescriptor TextParameter(string name) => new(
        name,
        ToolParameterType.Text,
        true,
        2048,
        null,
        null,
        [],
        "A bounded path or value.");

    private static ToolParameterDescriptor SwitchParameter(string name) => new(
        name,
        ToolParameterType.Switch,
        true,
        0,
        null,
        null,
        [],
        "A boolean switch.");
}
