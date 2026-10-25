using CustomRecorder.Core.Util;
using Xunit;

public class ReleaseVersionTests
{
    [Theory]
    [InlineData("v0.1.0", "0.1.0")]
    [InlineData("0.2.3", "0.2.3")]
    [InlineData("V1.0", "1.0.0")]
    [InlineData("v0.3.0-beta.2", "0.3.0")]
    [InlineData("v1.2.3+build.5", "1.2.3")]
    public void ParsesTags(string tag, string expected) => Assert.Equal(Version.Parse(expected), ReleaseVersion.Parse(tag));

    [Theory]
    [InlineData("latest")]
    [InlineData("")]
    [InlineData(null)]
    public void RejectsNonVersions(string? tag) => Assert.Null(ReleaseVersion.Parse(tag));

    [Fact]
    public void ComparesNumerically()
    {
        // 0.10.0 is newer than 0.9.0 (not a string comparison).
        Assert.True(ReleaseVersion.Parse("v0.10.0") > ReleaseVersion.Parse("v0.9.0"));
        Assert.True(ReleaseVersion.Normalize(new Version(0, 1, 0, 0)) == ReleaseVersion.Parse("v0.1.0"));
    }

    [Theory]
    [InlineData("ABCdef0123  CustomRecorder-v0.1.0-win-x64.zip\n", "abcdef0123")]
    [InlineData("abcdef0123 *CustomRecorder.zip", "abcdef0123")]
    [InlineData("abcdef0123", "abcdef0123")]
    public void ReadsChecksumFiles(string content, string expected) => Assert.Equal(expected, ReleaseVersion.ChecksumFromFile(content));
}
