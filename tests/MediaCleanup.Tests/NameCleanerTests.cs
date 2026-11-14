using MediaCleanup.Core.Naming;

namespace MediaCleanup.Tests;

public class NameCleanerTests
{
    private readonly NameCleaner _cleaner = new();

    [Theory]
    // The examples from the request.
    [InlineData("www.movieRuz.xyz - Mummy 2001 Dual Tel+Eng.mkv", "Mummy 2001 Dual Tel+Eng.mkv")]
    [InlineData("tamilmv.us - Don.2003.Web-DL.mp4", "Don 2003 Web-DL.mp4")]
    [InlineData("Devil.2005.Web-DL.mp4", "Devil 2005 Web-DL.mp4")]
    [InlineData("www.movieRuz.xyz - House S01E01.mkv", "House S01E01.mkv")]
    [InlineData("House S01E02.mkv", "House S01E02.mkv")]
    // Other shapes seen in the wild.
    [InlineData("www.site.com.Movie.Name.2019.1080p.mkv", "Movie Name 2019 1080p.mkv")]
    [InlineData("[www.site.com] Movie Name 2019.mkv", "Movie Name 2019.mkv")]
    [InlineData("Movie.Name.2019.x264-[site.net].mkv", "Movie Name 2019 x264.mkv")]
    [InlineData("Movie Name 2019 - www.site.me.mkv", "Movie Name 2019.mkv")]
    [InlineData("Movie Name 2019 - site.me.mkv", "Movie Name 2019.mkv")]
    [InlineData("www.1TamilMV.fi - Movie (2021) Telugu HDRip.mkv", "Movie (2021) Telugu HDRip.mkv")]
    [InlineData("www.site.co.in - Movie 2019.mkv", "Movie 2019.mkv")]
    [InlineData("Movie.2019.1080p.site.com.mkv", "Movie 2019 1080p.mkv")]
    [InlineData("https://site.com - Movie 2019.mkv", "Movie 2019.mkv")]
    [InlineData("Movie_Name_2019.avi", "Movie Name 2019.avi")]
    public void Cleans_file_names(string input, string expected) =>
        Assert.Equal(expected, _cleaner.CleanFileName(input));

    [Theory]
    // Titles that only look like web addresses must survive.
    [InlineData("Movie.In.Time.2011.mkv", "Movie In Time 2011.mkv")]
    [InlineData("Us.2019.1080p.mkv", "Us 2019 1080p.mkv")]
    [InlineData("The.Best.Of.Me.2014.mkv", "The Best Of Me 2014.mkv")]
    [InlineData("Mr.Robot - S01E01.mkv", "Mr Robot - S01E01.mkv")]
    [InlineData("Spider-Man.Homecoming.2017.mkv", "Spider-Man Homecoming 2017.mkv")]
    [InlineData("www.site.com.In.Time.2011.mkv", "In Time 2011.mkv")]
    public void Keeps_real_titles(string input, string expected) =>
        Assert.Equal(expected, _cleaner.CleanFileName(input));

    [Theory]
    [InlineData("Movie.2019.DD5.1.H.264.mkv", "Movie 2019 DD5.1 H.264.mkv")]
    [InlineData("Movie.2019.AAC.2.0.x264.mkv", "Movie 2019 AAC 2.0 x264.mkv")]
    [InlineData("Movie.2.2019.1080p.mkv", "Movie 2 2019 1080p.mkv")]
    public void Keeps_dots_in_audio_layouts_and_codecs(string input, string expected) =>
        Assert.Equal(expected, _cleaner.CleanFileName(input));

    [Theory]
    [InlineData("www.movieRuz.xyz - House 2020 Dual Tel+Eng", "House 2020 Dual Tel+Eng")]
    [InlineData("House 2020 Dual Tel+Eng", "House 2020 Dual Tel+Eng")]
    [InlineData("tamilmv.us - Show.Name.S01.1080p", "Show Name S01 1080p")]
    public void Cleans_folder_names(string input, string expected) =>
        Assert.Equal(expected, _cleaner.CleanFolderName(input));

    [Theory]
    [InlineData("www.site.com - Movie.2019.en.srt", "Movie 2019.en.srt")]
    [InlineData("Movie.2019.en.forced.srt", "Movie 2019.en.forced.srt")]
    [InlineData("tamilmv.us - Don.2003.Web-DL.srt", "Don 2003 Web-DL.srt")]
    [InlineData("Movie 2019.eng.srt", "Movie 2019.eng.srt")]
    public void Subtitles_keep_language_suffix(string input, string expected) =>
        Assert.Equal(expected, _cleaner.CleanFileName(input));

    [Fact]
    public void Removes_custom_terms()
    {
        var cleaner = new NameCleaner(new[] { "TamilMV", "1337x.zz" });
        Assert.Equal("Movie 2019 HDRip.mkv", cleaner.CleanFileName("TamilMV - Movie 2019 HDRip.mkv"));
        Assert.Equal("Movie 2019.mkv", cleaner.CleanFileName("Movie.2019.1337x.zz.mkv"));
        Assert.Equal("TamilMVP 2019.mkv", cleaner.CleanFileName("TamilMVP 2019.mkv"));
    }

    [Fact]
    public void Never_returns_an_empty_name()
    {
        Assert.Equal("www.site.com.mkv", _cleaner.CleanFileName("www.site.com.mkv"));
        Assert.Equal("www.site.com", _cleaner.CleanFolderName("www.site.com"));
    }

    [Fact]
    public void Cleaning_is_idempotent()
    {
        foreach (var name in new[] { "tamilmv.us - Don.2003.Web-DL", "www.movieRuz.xyz - Mummy 2001 Dual Tel+Eng", "Movie.2019.DD5.1.H.264" })
        {
            var once = _cleaner.CleanStem(name);
            Assert.Equal(once, _cleaner.CleanStem(once));
        }
    }
}
