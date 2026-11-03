using DeskBox.Services.Lyrics;

namespace DeskBox.Tests;

public sealed class LyricTrackMatcherTests
{
    [Theory]
    [InlineData("《晴天》", "晴天")]
    [InlineData("七里香 (Live)", "七里香")]
    [InlineData("夜曲（现场版）", "夜曲")]
    [InlineData("Hotel California - 2013 Remaster", "Hotel California")]
    [InlineData("Stay (feat. Justin Bieber)", "Stay")]
    [InlineData("Closer feat. Halsey", "Closer")]
    public void CleanTitle_RemovesCommonDecorationsWithoutDestroyingCoreTitle(string input, string expected)
    {
        Assert.Equal(expected, LyricTrackMatcher.CleanTitle(input));
    }

    [Theory]
    [InlineData("周杰伦 / 费玉清", "周杰伦")]
    [InlineData("周杰伦、阿信", "周杰伦")]
    [InlineData("The Kid LAROI & Justin Bieber", "The Kid LAROI")]
    [InlineData("Simon&Garfunkel", "Simon&Garfunkel")]
    public void ExtractPrimaryArtist_ExtractsLeadPerformer(string input, string expected)
    {
        Assert.Equal(expected, LyricTrackMatcher.ExtractPrimaryArtist(input));
    }

    [Fact]
    public void SelectBestCandidate_PrefersMatchingDurationAndSyncedLyrics()
    {
        var query = new LyricsQuery("晴天 (Live)", "周杰伦", "叶惠美", TimeSpan.FromSeconds(269));
        LrclibTrackDto[] candidates =
        [
            new()
            {
                Id = 1,
                TrackName = "晴天",
                ArtistName = "其他翻唱歌手",
                Duration = 210,
                SyncedLyrics = "[00:01.00]Wrong cover"
            },
            new()
            {
                Id = 2,
                TrackName = "晴天",
                ArtistName = "周杰伦",
                Duration = 269.4,
                SyncedLyrics = "[00:28.90]故事的小黄花"
            }
        ];

        LrclibTrackDto? best = LyricTrackMatcher.SelectBestCandidate(candidates, query, out double score);

        Assert.NotNull(best);
        Assert.Equal(2, best.Id);
        Assert.True(score > 0.85, $"Expected high confidence score, got {score:F3}");
    }

    [Fact]
    public void SelectBestCandidate_SupportsTimelineLessPlayerWhenTitleAndArtistMatchStrongly()
    {
        // Duration == 0 simulates unpatched NetEase Cloud Music 3.x SMTC metadata
        var query = new LyricsQuery("千里之外", "周杰伦 / 费玉清", string.Empty, TimeSpan.Zero);

        LrclibTrackDto[] candidates =
        [
            new()
            {
                Id = 10,
                TrackName = "千里之外",
                ArtistName = "周杰伦",
                Duration = 256,
                SyncedLyrics = "[00:15.00]屋檐如悬崖"
            },
            new()
            {
                Id = 11,
                TrackName = "完全无关的歌",
                ArtistName = "周杰伦",
                Duration = 256,
                SyncedLyrics = "[00:01.00]Other"
            }
        ];

        LrclibTrackDto? best = LyricTrackMatcher.SelectBestCandidate(candidates, query, out double score);
        Assert.NotNull(best);
        Assert.Equal(10, best.Id);
        Assert.True(score >= LyricTrackMatcher.MinimumAcceptScore);

        // Wrong artist in timeline-less mode must be rejected (< MinimumAcceptScore)
        var wrongArtistCandidates = new LrclibTrackDto[]
        {
            new()
            {
                Id = 99,
                TrackName = "千里之外",
                ArtistName = "陌生人",
                Duration = 256,
                SyncedLyrics = "[00:01.00]Wrong"
            }
        };

        Assert.Null(LyricTrackMatcher.SelectBestCandidate(wrongArtistCandidates, query, out _));
    }
}
