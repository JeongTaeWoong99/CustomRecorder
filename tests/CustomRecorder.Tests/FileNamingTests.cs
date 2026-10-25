using CustomRecorder.Core.Util;
using Xunit;

public class FileNamingTests
{
    private static readonly DateTime Time = new(2026, 10, 24, 21, 5, 9);

    [Fact]
    public void DefaultPattern()
        => Assert.Equal("녹화_2026-10-24_21-05-09", FileNaming.Format(null, "녹화", "모니터1", Time));

    [Fact]
    public void AllTokens()
        => Assert.Equal("[모니터1] 캡처 2026-10-24 21-05-09", FileNaming.Format("[{대상}] {종류} {날짜} {시간}", "캡처", "모니터1", Time));

    [Fact]
    public void InvalidCharactersAreReplaced()
        => Assert.Equal("a_b_c_d_", FileNaming.Format("a:b/c?d{대상}", "녹화", "*", Time));

    [Fact]
    public void TrailingDotsAndSpacesAreRemoved()
        => Assert.Equal("녹화", FileNaming.Format("{종류}. . ", "녹화", "", Time));

    [Fact]
    public void BlankResultFallsBackToKindAndTime()
        => Assert.Equal("녹화_2026-10-24_21-05-09", FileNaming.Format("{대상}", "녹화", "", Time));

    [Fact]
    public void LongNamesAreCut()
        => Assert.Equal(FileNaming.MaxLength, FileNaming.Format(new string('가', 300), "녹화", "", Time).Length);

    [Fact]
    public void UniqueAddsNumber()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var path = Path.Combine(dir, "a.mp4");
            Assert.Equal(path, FileNaming.Unique(path));
            File.WriteAllText(path, "");
            Assert.Equal(Path.Combine(dir, "a_2.mp4"), FileNaming.Unique(path));
            File.WriteAllText(Path.Combine(dir, "a_2.mp4"), "");
            Assert.Equal(Path.Combine(dir, "a_3.mp4"), FileNaming.Unique(path));
        }
        finally { Directory.Delete(dir, true); }
    }
}
