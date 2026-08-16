using System.Text.Json;
using AgentForge.Domain.Primitives;

namespace AgentForge.Domain.Learning;

public sealed record SkillCandidateDraft(string Markdown);

public static class SkillCandidateDraftParser
{
    public const int MaximumResponseCharacters = 65_536;
    public const int MaximumMarkdownCharacters = 32_768;
    public const string GeneratedStartMarker = "<!-- agentforge-generated:start -->";
    public const string GeneratedEndMarker = "<!-- agentforge-generated:end -->";
    private static readonly string[] RequiredHeadings =
    [
        "## Purpose",
        "## Inputs",
        "## Procedure",
        "## Verification",
        "## Failure conditions",
        "## Permission boundary",
    ];

    public static DomainResult<SkillCandidateDraft> Parse(string? response)
    {
        if (string.IsNullOrWhiteSpace(response) || response.Length > MaximumResponseCharacters ||
            response.Any(character => character == '\0'))
        {
            return Failure("The model response is empty or outside the candidate-generation bound.");
        }

        try
        {
            using var document = JsonDocument.Parse(response, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
            var root = document.RootElement;
            var properties = root.ValueKind is JsonValueKind.Object
                ? root.EnumerateObject().ToArray()
                : [];
            if (properties.Length != 1 || properties[0].Name != "markdown" ||
                properties[0].Value.ValueKind is not JsonValueKind.String)
            {
                return Failure("The model must return one exact JSON object containing only a markdown string.");
            }

            var markdown = properties[0].Value.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(markdown) || markdown.Length is < 200 or > MaximumMarkdownCharacters ||
                markdown.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')) ||
                markdown.StartsWith("---", StringComparison.Ordinal) ||
                markdown.Contains(GeneratedStartMarker, StringComparison.Ordinal) ||
                markdown.Contains(GeneratedEndMarker, StringComparison.Ordinal) ||
                markdown.Contains("<script", StringComparison.OrdinalIgnoreCase) ||
                RequiredHeadings.Any(heading => !ContainsExactHeading(markdown, heading)))
            {
                return Failure(
                    "Generated Markdown must be bounded, marker-free, and contain every required procedure heading.");
            }

            return DomainResult.Success(new SkillCandidateDraft(
                markdown.Replace("\r\n", "\n", StringComparison.Ordinal)));
        }
        catch (JsonException)
        {
            return Failure("The model response is not strict JSON.");
        }
    }

    private static bool ContainsExactHeading(string markdown, string heading) =>
        markdown.Split('\n').Any(line => string.Equals(line.TrimEnd('\r'), heading, StringComparison.Ordinal));

    private static DomainResult<SkillCandidateDraft> Failure(string message) =>
        DomainResult.Fail<SkillCandidateDraft>(new DomainFailure(FailureCode.ValidationFailure, message));
}
