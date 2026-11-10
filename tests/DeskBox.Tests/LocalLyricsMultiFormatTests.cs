using System.Text;
using DeskBox.Services.Lyrics;

namespace DeskBox.Tests;

public sealed class LocalLyricsMultiFormatTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "DeskBox_LocalLyricsMultiFormatTests_" + Guid.NewGuid().ToString("N"));

    public LocalLyricsMultiFormatTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public async Task TryResolveLocalLyricsAsync_DecryptsQqMusicQmcQrc_MergesCompanionQmtsAndIgnoresQmRoma()
    {
        string qmDir = Path.Combine(_tempRoot, "QQMusicLyricNew");
        Directory.CreateDirectory(qmDir);

        const string qrcXml =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
            "<QrcInfos>\n" +
            "<QrcHeadInfo SaveTime=\"0\" Version=\"100\"/>\n" +
            "<LyricInfo LyricCount=\"1\">\n" +
            "<Lyric_1 LyricType=\"1\" LyricContent=\"[ti:我不难过]\n" +
            "[ar:孙燕姿]\n" +
            "[al:未完成]\n" +
            "[offset:0]\n" +
            "[0,0]我不难过(0,0) - (0,0)孙燕姿(0,0)\n" +
            "[0,0]词：杨明学(0,0)\n" +
            "[31290,5808]又(31290,248)站(31538,353)在(31891,260)你(32151,297)家(32448,303)的(32751,389)门(33140,865)口(34005,993)\n" +
            "[38120,4200]我(38120,300)们(38420,350)重(38770,400)复(39170,450)沉(39620,500)默(40120,600)\"/>\n" +
            "</LyricInfo>\n" +
            "</QrcInfos>";

        const string qmtsLrc = """
            [ti:我不难过]
            [00:00.00]//
            [00:31.29]Standing at your doorway once again
            [00:38.10]We repeat our silence
            """;

        const string qmRomaQrc = """
            [0,1500]wo bu nan guo
            [31290,5808]you zhan zai ni jia de men kou
            """;

        byte[] encryptedPrimary = LocalLyricsFormatDecoder.EncryptLocalQmcQrcBytesForTesting(Encoding.UTF8.GetBytes(qrcXml));
        byte[] encryptedTrans = LocalLyricsFormatDecoder.EncryptLocalQmcQrcBytesForTesting(Encoding.UTF8.GetBytes(qmtsLrc));
        byte[] encryptedRoma = LocalLyricsFormatDecoder.EncryptLocalQmcQrcBytesForTesting(Encoding.UTF8.GetBytes(qmRomaQrc));

        await File.WriteAllBytesAsync(Path.Combine(qmDir, "孙燕姿 - 我不难过 - 320 - 未完成_qm.qrc"), encryptedPrimary);
        await File.WriteAllBytesAsync(Path.Combine(qmDir, "孙燕姿 - 我不难过 - 320 - 未完成_qmts.qrc"), encryptedTrans);
        await File.WriteAllBytesAsync(Path.Combine(qmDir, "孙燕姿 - 我不难过 - 320 - 未完成_qmRoma.qrc"), encryptedRoma);

        // Pass the parent cache root (_tempRoot) with timeline-less Duration == 0 to verify both
        // QQMusicLyricNew subfolder discovery and filename duration (320s) backfill.
        LrcDocument? resolved = await LocalFileLyricsProvider.TryResolveLocalLyricsAsync(
            new LyricsQuery("我不难过", "孙燕姿", string.Empty, TimeSpan.Zero),
            _tempRoot,
            userOffsetMs: 150);

        Assert.NotNull(resolved);
        Assert.True(resolved.HasSyncedLines);
        Assert.Equal(150, resolved.UserOffsetMs);
        Assert.Equal(TimeSpan.FromSeconds(320), resolved.TrackDuration);
        Assert.Equal(3, resolved.Lines.Count);

        Assert.Equal("我不难过 - 孙燕姿", resolved.Lines[0].PrimaryText);
        Assert.Equal("词：杨明学", resolved.Lines[0].SecondaryText); // [0,0] credits merged without blocking _qmts.qrc!

        Assert.Equal(TimeSpan.FromMilliseconds(31290), resolved.Lines[1].Timestamp);
        Assert.Equal("又站在你家的门口", resolved.Lines[1].PrimaryText);
        Assert.Equal("Standing at your doorway once again", resolved.Lines[1].SecondaryText);

        Assert.Equal(TimeSpan.FromMilliseconds(38120), resolved.Lines[2].Timestamp);
        Assert.Equal("我们重复沉默", resolved.Lines[2].PrimaryText);
        Assert.Equal("We repeat our silence", resolved.Lines[2].SecondaryText);
    }

    [Fact]
    public async Task TryResolveLocalLyricsAsync_DecryptsKugouKrcWithHashFilenameAndEmbeddedLanguageJson()
    {
        string krcDir = Path.Combine(_tempRoot, "KuGouLyric");
        Directory.CreateDirectory(krcDir);

        const string languageJson = """
            {
              "content": [
                {
                  "type": 0,
                  "lyricContent": [
                    ["kaze", " ga", " fuita"],
                    ["ame", " no", " ai"]
                  ]
                },
                {
                  "type": 1,
                  "lyricContent": [
                    ["//"],
                    ["窗外的天气 像是你多变的表情"]
                  ]
                }
              ]
            }
            """;

        string base64Lang = Convert.ToBase64String(Encoding.UTF8.GetBytes(languageJson));
        string plainKrc =
            "[id:$00000000]\r\n" +
            "[ar:杨丞琳]\r\n" +
            "[ti:雨爱]\r\n" +
            "[total:260292]\r\n" +
            $"[language:{base64Lang}]\r\n" +
            "[0,2000]<0,600,0>雨<600,600,0>爱\r\n" +
            "[16440,4200]<0,300,0>Kaze <300,400,0>ga <700,500,0>fuita\r\n";

        byte[] encryptedKrc = LocalLyricsFormatDecoder.EncryptKrcBytesForTesting(Encoding.UTF8.GetBytes(plainKrc));
        string krcPath = Path.Combine(
            krcDir,
            "杨丞琳 - 雨爱-2592ffa57a2d8b1a16f18d991b7098cd-192391075-00000000.krc");
        await File.WriteAllBytesAsync(krcPath, encryptedKrc);

        LrcDocument? doc = await LocalFileLyricsProvider.TryResolveLocalLyricsAsync(
            new LyricsQuery("雨爱", "杨丞琳", string.Empty, TimeSpan.Zero),
            krcDir);

        Assert.NotNull(doc);
        Assert.Equal(TimeSpan.FromMilliseconds(260292), doc.TrackDuration);
        Assert.Equal(2, doc.Lines.Count);
        Assert.Equal("雨爱", doc.Lines[0].PrimaryText);
        Assert.Null(doc.Lines[0].SecondaryText);
        Assert.Equal("Kaze ga fuita", doc.Lines[1].PrimaryText);
        Assert.Equal("窗外的天气 像是你多变的表情", doc.Lines[1].SecondaryText);
    }

    [Fact]
    public async Task TryResolveLocalLyricsAsync_ParsesNetEaseYrcAndNetEaseJsonPayloads()
    {
        string neteaseDir = Path.Combine(_tempRoot, "NetEase");
        Directory.CreateDirectory(neteaseDir);

        const string yrcContent = """
            {"t":0,"c":[{"tx":"作词 : "},{"tx":"方文山"}]}
            [15240,4120](15240,310,0)故(15550,290,0)事(15840,350,0)的(16190,420,0)小(16610,500,0)黄(17110,480,0)花
            [20100,3500](20100,400,0)从(20500,400,0)出(20900,400,0)生(21300,500,0)那(21800,500,0)年(22300,600,0)就(22900,700,0)飘(23200,400,0)着
            """;

        await File.WriteAllTextAsync(Path.Combine(neteaseDir, "周杰伦 - 晴天.yrc"), yrcContent, Encoding.UTF8);
        await File.WriteAllTextAsync(
            Path.Combine(neteaseDir, "周杰伦 - 晴天_ts.lrc"),
            "[00:15.24]Little yellow flowers of the story\n[00:20.10]Drifting since the year we were born",
            Encoding.UTF8);

        LrcDocument? yrcDoc = await LocalFileLyricsProvider.TryResolveLocalLyricsAsync(
            new LyricsQuery("晴天", "周杰伦", "叶惠美", TimeSpan.FromSeconds(269)),
            neteaseDir);

        Assert.NotNull(yrcDoc);
        Assert.Equal(3, yrcDoc.Lines.Count);
        Assert.Equal("作词 : 方文山", yrcDoc.Lines[0].PrimaryText);
        Assert.Equal("故事的小黄花", yrcDoc.Lines[1].PrimaryText);
        Assert.Equal("Little yellow flowers of the story", yrcDoc.Lines[1].SecondaryText);
        Assert.Equal("从出生那年就飘着", yrcDoc.Lines[2].PrimaryText);

        const string neteaseJson = """
            {
              "yrc": {
                "version": 1,
                "lyric": "[12000,3000](12000,500,0)Hello (12500,600,0)world\n"
              },
              "ytlrc": {
                "version": 1,
                "lyric": "[00:12.00]你好世界\n"
              },
              "code": 200
            }
            """;

        await File.WriteAllTextAsync(Path.Combine(neteaseDir, "Adele - Hello.json"), neteaseJson, Encoding.UTF8);
        LrcDocument? jsonDoc = await LocalFileLyricsProvider.TryResolveLocalLyricsAsync(
            new LyricsQuery("Hello", "Adele", string.Empty, TimeSpan.Zero),
            neteaseDir);

        Assert.NotNull(jsonDoc);
        Assert.Single(jsonDoc.Lines);
        Assert.Equal("Hello world", jsonDoc.Lines[0].PrimaryText);
        Assert.Equal("你好世界", jsonDoc.Lines[0].SecondaryText);

        await File.WriteAllTextAsync(
            Path.Combine(neteaseDir, "纯音乐测试.json"),
            """{"nolyric":true,"code":200}""",
            Encoding.UTF8);

        LrcDocument? instDoc = await LocalFileLyricsProvider.TryResolveLocalLyricsAsync(
            new LyricsQuery("纯音乐测试", string.Empty, string.Empty, TimeSpan.FromSeconds(180)),
            neteaseDir);

        Assert.NotNull(instDoc);
        Assert.True(instDoc.IsInstrumental);
    }

    [Fact]
    public async Task TryResolveLocalLyricsAsync_SupportsLrcxTtmlSrtAndDoubleAudioExtensions()
    {
        string openDir = Path.Combine(_tempRoot, "OpenFormats");
        Directory.CreateDirectory(openDir);

        // 1. LRCX with [tt] translation and [romaji] skipped + audio double extension + leading track number
        const string lrcx = """
            [00:10.50]夜に駆ける (2019)
            [00:10.50][romaji]yoru ni kakeru
            [00:10.50][tt]向夜晚奔去
            """;
        await File.WriteAllTextAsync(
            Path.Combine(openDir, "01. YOASOBI - 夜に駆ける.flac.lrcx"),
            lrcx,
            Encoding.UTF8);

        LrcDocument? lrcxDoc = await LocalFileLyricsProvider.TryResolveLocalLyricsAsync(
            new LyricsQuery("夜に駆ける", "YOASOBI", string.Empty, TimeSpan.Zero),
            openDir);

        Assert.NotNull(lrcxDoc);
        Assert.Single(lrcxDoc.Lines);
        Assert.Equal("夜に駆ける (2019)", lrcxDoc.Lines[0].PrimaryText);
        Assert.Equal("向夜晚奔去", lrcxDoc.Lines[0].SecondaryText);

        // 2. Apple Music / AMLL TTML with x-translation and x-roman
        const string ttml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <tt xmlns="http://www.w3.org/ns/ttml" xmlns:ttm="http://www.w3.org/ns/ttml#metadata">
              <body>
                <div>
                  <p begin="00:14.250" end="00:18.000">
                    <span begin="00:14.250" end="00:15.000">First </span>
                    <span begin="00:15.000" end="00:18.000">love</span>
                    <span ttm:role="x-translation">初恋</span>
                    <span ttm:role="x-roman">hatsukoi</span>
                  </p>
                </div>
              </body>
            </tt>
            """;
        await File.WriteAllTextAsync(Path.Combine(openDir, "Utada Hikaru - First Love.ttml"), ttml, Encoding.UTF8);

        LrcDocument? ttmlDoc = await LocalFileLyricsProvider.TryResolveLocalLyricsAsync(
            new LyricsQuery("First Love", "Utada Hikaru", string.Empty, TimeSpan.Zero),
            openDir);

        Assert.NotNull(ttmlDoc);
        Assert.Single(ttmlDoc.Lines);
        Assert.Equal(TimeSpan.FromMilliseconds(14250), ttmlDoc.Lines[0].Timestamp);
        Assert.Equal("First love", ttmlDoc.Lines[0].PrimaryText);
        Assert.Equal("初恋", ttmlDoc.Lines[0].SecondaryText);

        // 3. Bilingual SRT cues
        const string srt = """
            1
            00:00:08,400 --> 00:00:12,100
            <i>Stay with me</i>
            请留在我身边
            """;
        await File.WriteAllTextAsync(Path.Combine(openDir, "Miki Matsubara - Stay With Me.srt"), srt, Encoding.UTF8);

        LrcDocument? srtDoc = await LocalFileLyricsProvider.TryResolveLocalLyricsAsync(
            new LyricsQuery("Stay With Me", "Miki Matsubara", string.Empty, TimeSpan.Zero),
            openDir);

        Assert.NotNull(srtDoc);
        Assert.Single(srtDoc.Lines);
        Assert.Equal(TimeSpan.FromMilliseconds(8400), srtDoc.Lines[0].Timestamp);
        Assert.Equal("Stay with me", srtDoc.Lines[0].PrimaryText);
        Assert.Equal("请留在我身边", srtDoc.Lines[0].SecondaryText);
    }
}
