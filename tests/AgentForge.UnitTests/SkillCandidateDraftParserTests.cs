using System.Text.Json;
using AgentForge.Domain.Learning;

namespace AgentForge.UnitTests;

public sealed class SkillCandidateDraftParserTests
{
    [Fact]
    public void Strict_generated_markdown_is_accepted()
    {
        var parsed = SkillCandidateDraftParser.Parse(JsonSerializer.Serialize(new
        {
            markdown = ValidMarkdown(),
        }));

        Assert.True(parsed.IsSuccess, parsed.Failure?.Message);
        Assert.Contains("## Verification", parsed.Value.Markdown, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("```json\n{\"markdown\":\"value\"}\n```")]
    [InlineData("{\"markdown\":\"first\",\"markdown\":\"second\"}")]
    [InlineData("{\"markdown\":\"short\"}")]
    [InlineData("{\"markdown\":123}")]
    [InlineData("[]")]
    public void Non_exact_model_responses_are_rejected(string response)
    {
        var parsed = SkillCandidateDraftParser.Parse(response);

        Assert.False(parsed.IsSuccess);
    }

    [Fact]
    public void Generated_markers_and_active_script_content_are_rejected()
    {
        var marker = SkillCandidateDraftParser.Parse(JsonSerializer.Serialize(new
        {
            markdown = ValidMarkdown() + "\n" + SkillCandidateDraftParser.GeneratedStartMarker,
        }));
        var script = SkillCandidateDraftParser.Parse(JsonSerializer.Serialize(new
        {
            markdown = ValidMarkdown() + "\n<script>alert('unsafe')</script>",
        }));

        Assert.False(marker.IsSuccess);
        Assert.False(script.IsSuccess);
    }

    private static string ValidMarkdown() => """
## Purpose

Describe one bounded procedure from redacted evidence while preserving the authority boundary.

## Inputs

- A specific operator goal.
- Bounded non-sensitive input values.

## Procedure

1. Validate the supplied inputs.
2. Follow only the declared read-only operation.
3. Produce observable output for a separate verifier.

## Verification

Compare the result with the stated outcome and retain only non-sensitive evidence hashes.

## Failure conditions

Stop on missing input, ambiguous evidence, unavailable authority, or an unverifiable result.

## Permission boundary

No tool, network, credential, messaging, device, write, or approval authority is granted by this procedure.
""";
}
