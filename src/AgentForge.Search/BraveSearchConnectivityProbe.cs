using System.Diagnostics;
using System.Security.Cryptography;
using AgentForge.Abstractions.Search;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Search;
using AgentForge.Domain.Security;

namespace AgentForge.Search;

internal sealed class BraveSearchConnectivityProbe(IClock clock) : IBraveSearchConnectivityProbe
{
    private static readonly Uri Endpoint = new("https://api.search.brave.com/res/v1/web/search");

    public async Task<DomainResult<BraveSearchProbeEvidence>> ProbeAsync(
        ReadOnlyMemory<char> credential,
        BraveSearchConfigurationCandidate candidate,
        CancellationToken cancellationToken)
    {
        if (credential.IsEmpty || credential.Length > 512 || credential.Span.Contains('\r') ||
            credential.Span.Contains('\n') || credential.Span.Contains('\0'))
        {
            return Invalid("A bounded Brave API key is required for validation.");
        }

        using var secretStore = new EphemeralSecretStore(credential.Span);
        var options = new SearchHttpProviderOptions(
            Endpoint,
            secretStore.Reference,
            null,
            Timeout: TimeSpan.FromSeconds(15),
            SafeSearch: candidate.SafeSearch,
            CountryCode: candidate.CountryCode,
            SearchLanguage: candidate.SearchLanguage);
        var created = SearchHttpProvider.CreateBrave("brave", options, secretStore, clock);
        if (!created.IsSuccess)
        {
            return DomainResult.Fail<BraveSearchProbeEvidence>(created.Failure!);
        }

        using var provider = created.Value;
        var started = Stopwatch.GetTimestamp();
        var response = await provider.SearchAsync(new SearchRequest(
            "AgentForge software",
            1,
            ["brave"],
            "configuration-probe",
            "configuration-probe",
            "configuration-probe",
            clock.UtcNow,
            TimeSpan.Zero), cancellationToken);
        var duration = Stopwatch.GetElapsedTime(started);
        if (response.Failure is not null || response.Hits.Length == 0)
        {
            return DomainResult.Fail<BraveSearchProbeEvidence>(new DomainFailure(
                response.Failure?.Kind is SearchFailureKind.QuotaExceeded
                    ? FailureCode.BudgetExceeded
                    : FailureCode.RecoverableExternalFailure,
                "Brave Search credential verification did not return a usable web result.",
                response.Failure?.IsRetryable == true));
        }

        var evidence = SearchContractValidator.Hash(
            $"brave-probe-v1\n{response.Hits.Length}\n{string.Join('\n', response.Hits.Select(item => item.Source.AbsoluteUri))}");
        return DomainResult.Success(new BraveSearchProbeEvidence(response.Hits.Length, duration, evidence));
    }

    private static DomainResult<BraveSearchProbeEvidence> Invalid(string message) =>
        DomainResult.Fail<BraveSearchProbeEvidence>(new DomainFailure(FailureCode.ValidationFailure, message));

    private sealed class EphemeralSecretStore : ISecretStore, IDisposable
    {
        private const string Name = "ephemeral-brave-probe";
        private char[] _credential;
        private int _materialized;

        public EphemeralSecretStore(ReadOnlySpan<char> credential)
        {
            _credential = credential.ToArray();
            Reference = new SecretReference(Name, "single-use");
        }

        public SecretReference Reference { get; }

        public string StoreName => Name;

        public SecretStoreCapability GetCapability() => new(Name, true, null);

        public Task<DomainResult<SecretReference>> StoreAsync(
            string logicalName,
            ReadOnlyMemory<char> secret,
            CancellationToken cancellationToken) =>
            Task.FromResult(DomainResult.Fail<SecretReference>(new DomainFailure(
                FailureCode.UnsupportedCapability,
                "The ephemeral probe store cannot persist secrets.")));

        public Task<DomainResult<SecretLease>> MaterializeAsync(
            SecretReference secretReference,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (secretReference != Reference || Interlocked.Exchange(ref _materialized, 1) != 0)
            {
                return Task.FromResult(DomainResult.Fail<SecretLease>(new DomainFailure(
                    FailureCode.PolicyDenied,
                    "The ephemeral credential lease is unavailable.")));
            }
            return Task.FromResult(DomainResult.Success(new SecretLease(_credential.ToArray())));
        }

        public Task<DomainResult<bool>> DeleteAsync(
            SecretReference secretReference,
            CancellationToken cancellationToken) =>
            Task.FromResult(DomainResult.Success(secretReference == Reference));

        public void Dispose()
        {
            Array.Clear(_credential);
            _credential = [];
        }
    }
}
