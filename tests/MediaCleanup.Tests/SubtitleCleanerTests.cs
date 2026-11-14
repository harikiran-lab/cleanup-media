using System.Text;
using MediaCleanup.Core.Subtitles;

namespace MediaCleanup.Tests;

public class SubtitleCleanerTests
{
    private readonly SubtitleCleaner _cleaner = new();

    [Fact]
    public void Removes_advert_cues_from_srt_and_renumbers()
    {
        var srt = "1\r\n00:00:01,000 --> 00:00:04,000\r\nDownloaded from www.site.com\r\n\r\n" +
                  "2\r\n00:00:05,000 --> 00:00:07,000\r\nHello there.\r\n\r\n" +
                  "3\r\n00:00:08,000 --> 00:00:09,000\r\nVisit TamilSubs.net for more\r\n\r\n" +
                  "4\r\n00:00:10,000 --> 00:00:12,000\r\nGoodbye.\r\nSee you.\r\n";
        var (text, removed) = _cleaner.CleanBlocks(srt, renumber: true);
        Assert.Equal(2, removed);
        Assert.Equal("1\r\n00:00:05,000 --> 00:00:07,000\r\nHello there.\r\n\r\n" +
                     "2\r\n00:00:10,000 --> 00:00:12,000\r\nGoodbye.\r\nSee you.\r\n", text);
    }

    [Theory]
    [InlineData("I was there.in the morning")]
    [InlineData("Wait.is that him?")]
    [InlineData("He paid $5.to the man")]
    [InlineData("Email me at bob@example.com")]
    public void Keeps_dialogue_that_only_looks_like_an_address(string line) =>
        Assert.False(_cleaner.IsAdvert(line));

    [Theory]
    [InlineData("Subtitles by www.opensubs.to")]
    [InlineData("http://example.io/subs")]
    [InlineData("<i>Ripped by MovieSite.xyz</i>")]
    public void Detects_adverts(string line) => Assert.True(_cleaner.IsAdvert(line));

    [Fact]
    public void Untouched_text_is_returned_as_is()
    {
        var srt = "1\n00:00:01,000 --> 00:00:04,000\nHello\n\n\n\n2\n00:00:05,000 --> 00:00:06,000\nBye\n";
        var (text, removed) = _cleaner.CleanBlocks(srt, renumber: true);
        Assert.Equal(0, removed);
        Assert.Same(srt, text);
    }

    [Fact]
    public void Keeps_vtt_header()
    {
        var vtt = "WEBVTT\n\n00:01.000 --> 00:04.000\nwww.site.com\n\n00:05.000 --> 00:06.000\nHi\n";
        var (text, removed) = _cleaner.CleanBlocks(vtt, renumber: false);
        Assert.Equal(1, removed);
        Assert.Equal("WEBVTT\n\n00:05.000 --> 00:06.000\nHi\n", text);
    }

    [Fact]
    public void Removes_advert_dialogue_from_ass()
    {
        var ass = "[Script Info]\nTitle: from www.site.com\n\n[Events]\n" +
                  "Dialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,{\\an8}Get more at site.com\n" +
                  "Dialogue: 0,0:00:03.00,0:00:04.00,Default,,0,0,0,,Hello, world\n";
        var (text, removed) = _cleaner.CleanScript(ass);
        Assert.Equal(1, removed);
        Assert.DoesNotContain("Get more", text);
        Assert.Contains("Hello, world", text);
        Assert.Contains("[Script Info]", text);
    }

    [Fact]
    public void File_round_trip_preserves_bom_and_non_utf8_bytes()
    {
        var dir = Directory.CreateTempSubdirectory("mc-sub").FullName;
        try
        {
            // Latin-1 encoded dialogue (0xE9 = é) is not valid UTF-8 and must survive untouched.
            var path = Path.Combine(dir, "a.srt");
            var bytes = Encoding.Latin1.GetBytes("1\n00:00:01,000 --> 00:00:02,000\nwww.site.com\n\n2\n00:00:03,000 --> 00:00:04,000\ncafé\n");
            File.WriteAllBytes(path, bytes);
            Assert.Equal(1, _cleaner.CleanFile(path));
            Assert.Equal(Encoding.Latin1.GetBytes("1\n00:00:03,000 --> 00:00:04,000\ncafé\n"), File.ReadAllBytes(path));

            var bomPath = Path.Combine(dir, "b.srt");
            var withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:02,000\nsite.com\n\n2\n00:00:03,000 --> 00:00:04,000\nతెలుగు\n")).ToArray();
            File.WriteAllBytes(bomPath, withBom);
            Assert.Equal(1, _cleaner.CleanFile(bomPath));
            var result = File.ReadAllBytes(bomPath);
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, result.Take(3).ToArray());
            Assert.Equal("1\n00:00:03,000 --> 00:00:04,000\nతెలుగు\n", Encoding.UTF8.GetString(result, 3, result.Length - 3));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
