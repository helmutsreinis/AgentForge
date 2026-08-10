using AgentForge.Domain.Primitives;
using AgentForge.Environment;

namespace AgentForge.UnitTests;

public sealed class OsReleaseParserTests
{
    [Fact]
    public void Parses_quoted_Ubuntu_24_04_metadata()
    {
        const string content = """
            NAME="Ubuntu"
            VERSION="24.04.3 LTS (Noble Numbat)"
            ID=ubuntu
            ID_LIKE=debian
            PRETTY_NAME="Ubuntu 24.04.3 LTS"
            VERSION_ID="24.04"
            VERSION_CODENAME=noble
            """;

        var result = OsReleaseParser.Parse(content);

        Assert.True(result.IsSuccess, result.Failure?.Message);
        Assert.Equal("ubuntu", result.Value.Id);
        Assert.Equal("debian", result.Value.IdLike);
        Assert.Equal("24.04", result.Value.VersionId);
        Assert.Equal("noble", result.Value.VersionCodename);
        Assert.False(result.Value.IsKali);
    }

    [Fact]
    public void Parses_Kali_from_distribution_metadata_without_running_a_command()
    {
        const string content = """
            PRETTY_NAME="Kali GNU/Linux Rolling"
            ID=kali
            ID_LIKE=debian
            VERSION_ID="2026.2"
            """;

        var result = OsReleaseParser.Parse(content);

        Assert.True(result.IsSuccess, result.Failure?.Message);
        Assert.True(result.Value.IsKali);
    }

    [Theory]
    [InlineData("ID=\"ubuntu")]
    [InlineData("ID ubuntu")]
    [InlineData("id=ubuntu")]
    [InlineData("ID=")]
    public void Rejects_malformed_or_missing_distribution_identity(string content)
    {
        var result = OsReleaseParser.Parse(content);

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureCode.ValidationFailure, result.Failure?.Code);
    }

    [Fact]
    public void Rejects_content_over_64_KiB()
    {
        var result = OsReleaseParser.Parse("ID=ubuntu\n" + new string('x', 65_536));

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureCode.ValidationFailure, result.Failure?.Code);
    }
}
