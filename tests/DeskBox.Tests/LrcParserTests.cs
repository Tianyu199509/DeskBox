using DeskBox.Services.Lyrics;

namespace DeskBox.Tests;

public sealed class LrcParserTests
{
    [Fact]
    public void Parse_SupportsStandardMillisecondColonMultiTagOffsetAndLengthMetadata()
    {
        const string lrc = """
            [ti:晴天]
            [ar:周杰伦]
            [length:04:29]
            [offset:500]
            [00:01.20]故事的小黄花
            [00:05.345]从出生那年就飘着
            [00:10:50]童年的荡秋千
            [00:20.00][01:20.00]吹着前奏望着天空
            """;

        LrcDocument doc = LrcParser.Parse(lrc);

        Assert.True(doc.HasSyncedLines);
        Assert.False(doc.IsInstrumental);
        Assert.Equal(TimeSpan.FromSeconds(269), doc.TrackDuration);
        Assert.Equal(500, doc.EmbeddedOffsetMs);
        Assert.Equal(5, doc.Lines.Count);

        Assert.Equal(TimeSpan.FromMilliseconds(1200), doc.Lines[0].Timestamp);
        Assert.Equal("故事的小黄花", doc.Lines[0].PrimaryText);

        Assert.Equal(TimeSpan.FromMilliseconds(5345), doc.Lines[1].Timestamp);
        Assert.Equal(TimeSpan.FromMilliseconds(10500), doc.Lines[2].Timestamp);

        Assert.Equal(TimeSpan.FromSeconds(20), doc.Lines[3].Timestamp);
        Assert.Equal("吹着前奏望着天空", doc.Lines[3].PrimaryText);
        Assert.Equal(TimeSpan.FromSeconds(80), doc.Lines[4].Timestamp);
        Assert.Equal("吹着前奏望着天空", doc.Lines[4].PrimaryText);
    }

    [Fact]
    public void Parse_StripsInlineKaraokeWordTagsAndMergesBilingualLinesWithin20Ms()
    {
        const string lrc = """
            [00:12.00]<00:12.00>Fly <00:12.40>me <00:12.80>to <00:13.10>the <00:13.50>moon
            [00:12.01]带我飞向月球
            [00:16.50]Let me play among the stars
            [00:16.50]让我漫步星辰之间
            """;

        LrcDocument doc = LrcParser.Parse(lrc);

        Assert.Equal(2, doc.Lines.Count);
        Assert.Equal("Fly me to the moon", doc.Lines[0].PrimaryText);
        Assert.Equal("带我飞向月球", doc.Lines[0].SecondaryText);
        Assert.Equal("Let me play among the stars", doc.Lines[1].PrimaryText);
        Assert.Equal("让我漫步星辰之间", doc.Lines[1].SecondaryText);
    }

    [Theory]
    [InlineData("[00:00.00]纯音乐，请欣赏")]
    [InlineData("[00:05.00]Instrumental")]
    [InlineData("纯音乐 请欣赏")]
    public void Parse_DetectsInstrumentalTracks(string raw)
    {
        LrcDocument doc = LrcParser.Parse(raw);
        Assert.True(doc.IsInstrumental);
        Assert.Empty(doc.Lines);
    }

    [Fact]
    public void FindActiveLineIndex_And_GetNextTransitionDelay_RespectOffsetsAndPlaybackRate()
    {
        const string lrc = """
            [00:10.00]Line Zero
            [00:20.00]Line One
            [00:35.00]Line Two
            """;

        LrcDocument doc = LrcParser.Parse(lrc, userOffsetMs: 1000); // +1.0s delay -> triggers at 11s, 21s, 36s

        Assert.Equal(-1, doc.FindActiveLineIndex(TimeSpan.FromSeconds(10.5)));
        Assert.Equal(0, doc.FindActiveLineIndex(TimeSpan.FromSeconds(11.0)));
        Assert.Equal(1, doc.FindActiveLineIndex(TimeSpan.FromSeconds(25.0)));
        Assert.Equal(2, doc.FindActiveLineIndex(TimeSpan.FromSeconds(50.0)));

        // At 15s (active=0, next triggers at 21s -> 6s remaining; at 2.0x rate -> 3s timer delay)
        TimeSpan? delay = doc.GetNextTransitionDelay(TimeSpan.FromSeconds(15), playbackRate: 2.0);
        Assert.NotNull(delay);
        Assert.Equal(3.0, delay.Value.TotalSeconds, precision: 3);

        // On final line -> null (no further timer wakeup needed)
        Assert.Null(doc.GetNextTransitionDelay(TimeSpan.FromSeconds(40), playbackRate: 1.0));
    }
}
