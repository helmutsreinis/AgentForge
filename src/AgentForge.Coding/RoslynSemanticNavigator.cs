using System.Security.Cryptography;
using System.Text;
using AgentForge.Abstractions.Coding;
using AgentForge.Domain.Coding;
using AgentForge.Domain.Primitives;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace AgentForge.Coding;

internal sealed class RoslynSemanticNavigator : ISemanticNavigator, ILanguageServerAdapter
{
    private static readonly object RegistrationLock = new();

    public string Language => "C#";

    public Task<DomainResult<SemanticResult>> NavigateAsync(
        RepositoryProfile repository,
        SemanticQuery query,
        CancellationToken cancellationToken) => AnalyzeAsync(repository, query, cancellationToken);

    public async Task<DomainResult<SemanticResult>> AnalyzeAsync(
        RepositoryProfile repository,
        SemanticQuery query,
        CancellationToken cancellationToken)
    {
        if (repository is null || !CodingRecordValidator.IsSha256(repository.ProfileHash) ||
            query is null || query.Line < 0 || query.Column < 0 || query.MaximumReferences is < 1 or > 512 ||
            !TryContainedFile(repository.RootPath, query.RelativePath, out var sourcePath) ||
            !string.Equals(Path.GetExtension(sourcePath), ".cs", StringComparison.OrdinalIgnoreCase))
        {
            return Failure("The semantic query or repository evidence is invalid.");
        }

        try
        {
            EnsureMsBuildRegistered();
            using var workspace = MSBuildWorkspace.Create();
            workspace.SkipUnrecognizedProjects = true;
            var descriptor = repository.Projects.Where(item => item.Language == "C#")
                .OrderByDescending(item => item.RelativePath.Length)
                .FirstOrDefault(item => query.RelativePath.StartsWith(
                    (Path.GetDirectoryName(item.RelativePath)?.Replace('\\', '/') ?? string.Empty) + '/',
                    StringComparison.Ordinal));
            if (descriptor is null)
            {
                return Failure("The source file is not contained by a discovered C# project.");
            }

            var projectPath = Path.Combine(
                repository.RootPath,
                descriptor.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            var project = await workspace.OpenProjectAsync(projectPath, cancellationToken: cancellationToken);
            var document = project.Documents.SingleOrDefault(item =>
                item.FilePath is not null && PathEquals(item.FilePath, sourcePath!));
            if (document is null)
            {
                return Failure("The source file is not part of a discovered C# project.");
            }

            var text = await document.GetTextAsync(cancellationToken);
            if (query.Line >= text.Lines.Count || query.Column > text.Lines[query.Line].Span.Length)
            {
                return Failure("The semantic position is outside the source file.");
            }

            var position = text.Lines[query.Line].Start + query.Column;
            var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, position, cancellationToken);
            var semanticSymbol = symbol is null
                ? null
                : await MapSymbolAsync(symbol, project.Solution, repository.RootPath, query.MaximumReferences, cancellationToken);
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null)
            {
                return Failure("Roslyn could not compile the discovered project graph.");
            }

            var diagnostics = compilation.GetDiagnostics(cancellationToken)
                .Where(item => item.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning or DiagnosticSeverity.Info)
                .OrderBy(item => item.Location.SourceTree?.FilePath, StringComparer.Ordinal)
                .ThenBy(item => item.Location.SourceSpan.Start)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .Take(256)
                .Select(item => MapDiagnostic(item, repository.RootPath))
                .ToArray();
            var result = new SemanticResult(semanticSymbol, diagnostics, string.Empty);
            return DomainResult.Success(result with { EvidenceHash = ComputeHash(result) });
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return DomainResult.Fail<SemanticResult>(new DomainFailure(
                FailureCode.RecoverableExternalFailure,
                "The MSBuild/Roslyn graph could not be loaded in the current environment."));
        }
    }

    private static async Task<SemanticSymbol> MapSymbolAsync(
        ISymbol symbol,
        Solution solution,
        string root,
        int maximumReferences,
        CancellationToken cancellationToken)
    {
        var definition = symbol.Locations.FirstOrDefault(location => location.IsInSource);
        var references = new List<SemanticLocation>();
        var groups = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken);
        foreach (var group in groups)
        {
            foreach (var location in group.Locations.Where(item => item.Location.IsInSource))
            {
                if (MapLocation(location.Location, root) is { } mapped)
                {
                    references.Add(mapped);
                    if (references.Count >= maximumReferences)
                    {
                        break;
                    }
                }
            }

            if (references.Count >= maximumReferences)
            {
                break;
            }
        }

        return new SemanticSymbol(
            symbol.Name,
            symbol.Kind.ToString(),
            symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            MapLocation(definition!, root)!,
            references.OrderBy(item => item.RelativePath, StringComparer.Ordinal)
                .ThenBy(item => item.StartLine).ThenBy(item => item.StartColumn).ToArray());
    }

    private static SemanticDiagnostic MapDiagnostic(Diagnostic diagnostic, string root) => new(
        diagnostic.Id,
        diagnostic.Severity.ToString(),
        Bound(diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture), 2_048),
        diagnostic.Location.IsInSource ? MapLocation(diagnostic.Location, root) : null);

    private static SemanticLocation? MapLocation(Location location, string root)
    {
        var path = location.SourceTree?.FilePath;
        if (path is null || !IsWithin(root, path))
        {
            return null;
        }

        var span = location.GetLineSpan().Span;
        return new SemanticLocation(
            Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'),
            span.Start.Line,
            span.Start.Character,
            span.End.Line,
            span.End.Character);
    }

    private static void EnsureMsBuildRegistered()
    {
        lock (RegistrationLock)
        {
            if (!MSBuildLocator.IsRegistered)
            {
                MSBuildLocator.RegisterDefaults();
            }
        }
    }

    private static bool TryContainedFile(string root, string relativePath, out string? fullPath)
    {
        fullPath = null;
        if (!Path.IsPathFullyQualified(root) || string.IsNullOrWhiteSpace(relativePath) ||
            Path.IsPathRooted(relativePath) || relativePath.Contains('\\') ||
            relativePath.Split('/').Any(part => part is "" or "." or ".."))
        {
            return false;
        }

        try
        {
            fullPath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            return IsWithin(root, fullPath) && File.Exists(fullPath) &&
                (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsWithin(string root, string path) => path.StartsWith(
        Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool PathEquals(string left, string right) => string.Equals(
        Path.GetFullPath(left), Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string ComputeHash(SemanticResult result)
    {
        var builder = new StringBuilder();
        if (result.Symbol is { } symbol)
        {
            Append(builder, symbol.Name); Append(builder, symbol.Kind); Append(builder, symbol.DisplayName);
            Append(builder, symbol.Definition);
            foreach (var reference in symbol.References) Append(builder, reference);
        }

        foreach (var diagnostic in result.Diagnostics)
        {
            Append(builder, diagnostic.Id); Append(builder, diagnostic.Severity); Append(builder, diagnostic.Message);
            Append(builder, diagnostic.Location?.ToString() ?? string.Empty);
        }

        return $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))}";
    }

    private static void Append(StringBuilder builder, object value)
    {
        var text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        builder.Append(text.Length).Append(':').Append(text).Append(';');
    }

    private static string Bound(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];

    private static DomainResult<SemanticResult> Failure(string message) =>
        DomainResult.Fail<SemanticResult>(new DomainFailure(FailureCode.ValidationFailure, message));
}
