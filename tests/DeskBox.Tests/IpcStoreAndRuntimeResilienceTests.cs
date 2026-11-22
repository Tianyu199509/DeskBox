using System.Net;
using System.Text;
using System.Text.Json;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.Services.Ipc;

namespace DeskBox.Tests;

public sealed class IpcStoreAndRuntimeResilienceTests
{
    [Fact]
    public async Task PipeBoundedLineReader_DistinguishesOversizedFromEndOfStream_AndEnforcesIdleTimeout()
    {
        byte[] payload = Encoding.UTF8.GetBytes("ok-1\r\n0123456789ABCDEF\nok-2\n");
        using (var ms = new MemoryStream(payload))
        using (var reader = new PipeBoundedLineReader(ms, streamReaderBufferBytes: 128))
        {
            BoundedLineReadResult first = await reader.ReadBoundedLineAsync(
                maxChars: 8,
                CancellationToken.None,
                idleTimeout: TimeSpan.FromSeconds(2));
            Assert.Equal(BoundedLineReadKind.Line, first.Kind);
            Assert.Equal("ok-1", first.Line);

            BoundedLineReadResult oversized = await reader.ReadBoundedLineAsync(
                maxChars: 8,
                CancellationToken.None,
                idleTimeout: TimeSpan.FromSeconds(2));
            Assert.Equal(BoundedLineReadKind.Oversized, oversized.Kind);
            Assert.True(oversized.ObservedLength > 8);

            BoundedLineReadResult third = await reader.ReadBoundedLineAsync(
                maxChars: 8,
                CancellationToken.None,
                idleTimeout: TimeSpan.FromSeconds(2));
            Assert.Equal(BoundedLineReadKind.Line, third.Kind);
            Assert.Equal("ok-2", third.Line);

            BoundedLineReadResult eof = await reader.ReadBoundedLineAsync(
                maxChars: 8,
                CancellationToken.None,
                idleTimeout: TimeSpan.FromSeconds(2));
            Assert.Equal(BoundedLineReadKind.EndOfStream, eof.Kind);
        }

        await using var stalledStream = new StallingReadStream();
        using var stalledReader = new PipeBoundedLineReader(stalledStream, streamReaderBufferBytes: 128);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            stalledReader.ReadBoundedLineAsync(
                maxChars: 64,
                CancellationToken.None,
                idleTimeout: TimeSpan.FromMilliseconds(80)).AsTask());
    }

    [Fact]
    public async Task PipeEnvelopeSerializer_ConcurrentWritesAcrossThreadPool_ProduceIntactJsonEnvelopes()
    {
        const int writerCount = 48;
        Task<string>[] tasks = Enumerable.Range(0, writerCount)
            .Select(async index =>
            {
                await using var stream = new YieldingWriteMemoryStream();
                string largeText = new((char)('a' + (index % 26)), 3200 + (index * 37));
                await PipeEnvelopeSerializer.WriteEnvelopeAsync(
                    stream,
                    writer =>
                    {
                        writer.WriteStartObject();
                        writer.WriteNumber("id", index);
                        writer.WriteString("payload", largeText);
                        writer.WriteEndObject();
                    },
                    CancellationToken.None);

                string raw = Encoding.UTF8.GetString(stream.ToArray());
                Assert.EndsWith("\n", raw, StringComparison.Ordinal);
                using JsonDocument doc = JsonDocument.Parse(raw.TrimEnd('\n'));
                Assert.Equal(index, doc.RootElement.GetProperty("id").GetInt32());
                Assert.Equal(largeText, doc.RootElement.GetProperty("payload").GetString());
                return raw;
            })
            .ToArray();

        string[] envelopes = await Task.WhenAll(tasks);
        Assert.Equal(writerCount, envelopes.Length);
    }

    [Fact]
    public async Task AiChatTransportClient_RejectsOversizedSseFrames_CleanlyWithoutHanging()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-sse-cap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var credStore = new CustomWidgetAiCredentialStore(tempDir);
            credStore.Upsert(new AiProviderCredentialEntry(
                "deepseek",
                "sk-test-oversized",
                CustomModel: "deepseek-chat",
                ThinkingMode: "disabled"));
            var prefsStore = new CustomWidgetStudioPreferencesStore(Path.Combine(tempDir, "studio-prefs.json"));

            var oversizedHandler = new OversizedSseHttpMessageHandler(AiChatTransportClient.MaxSseLineChars + 8192);
            var aiService = new DeskBoxAiService(credStore, prefsStore, new HttpClient(oversizedHandler));
            var generator = new CustomWidgetAiGeneratorService(
                aiService,
                diagnosticArchive: new CustomWidgetDiagnosticArchive(tempDir));

            StudioTurnResult result = await generator.RunStudioTurnAsync(
                "deepseek",
                "Build a demo status widget",
                uiLanguage: "en-US");

            Assert.False(result.Success);
            Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void CustomWidgetDiagnosticArchive_ScrubsSecrets_AndNeverLeavesUnpairedHighSurrogates()
    {
        const string sample =
            "Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9 " +
            "x-api-key: super-secret-header-value\n" +
            "url=https://api.example.com/v1?api_key=abc123secret&token=tok98765&ok=1 " +
            "'apiKey': 'single-quoted-secret', \"password\": \"my-pass-123\" " +
            "AIzaSyDaGmWKa4JsXZ-fakeGoogleKey12345 ghp_1234567890abcdefghijklmnop " +
            "gsk_1234567890abcdef xai-1234567890abcdef xoxb-1234567890-abcdef";

        string scrubbed = CustomWidgetDiagnosticArchive.ScrubSensitiveText(sample, 4096);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9", scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret-header-value", scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123secret", scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain("tok98765", scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain("single-quoted-secret", scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain("my-pass-123", scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain("SyDaGmWKa4JsXZ", scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain("1234567890abcdefghijklmnop", scrubbed, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", scrubbed, StringComparison.Ordinal);

        // "abc😀def": index 3 is the high surrogate (0xD83D) and index 4 is the low surrogate (0xDE00).
        const string emojiText = "abc\U0001F600def";
        string cutAtSurrogateBoundary = CustomWidgetDiagnosticArchive.ScrubSensitiveText(emojiText, maxChars: 4);
        Assert.Equal("abc", cutAtSurrogateBoundary);
        Assert.False(char.IsHighSurrogate(cutAtSurrogateBoundary[^1]));

        var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        byte[] encoded = strictUtf8.GetBytes(cutAtSurrogateBoundary);
        Assert.Equal("abc", strictUtf8.GetString(encoded));
    }

    [Fact]
    public void WeatherService_MsnConversion_IgnoresZuluTimestampFallback_ButHonorsExplicitCityOffset()
    {
        var zuluOnlyMsn = new MsnWeatherBody
        {
            Current = new MsnWeatherCurrent
            {
                Temp = 22,
                Created = "2026-11-18T06:00:00Z"
            }
        };

        WeatherData zuluConverted = WeatherService.ConvertMsnToWeatherData(zuluOnlyMsn, 31.23, 121.47);
        Assert.Null(zuluConverted.UtcOffsetSeconds);

        var explicitOffsetMsn = new MsnWeatherBody
        {
            Current = new MsnWeatherCurrent
            {
                Temp = 22,
                Created = "2026-11-18T14:00:00+08:00"
            }
        };

        WeatherData explicitConverted = WeatherService.ConvertMsnToWeatherData(explicitOffsetMsn, 31.23, 121.47);
        Assert.Equal(8 * 3600, explicitConverted.UtcOffsetSeconds);
    }

    [Fact]
    public void CustomWidgetCalendarAndAudio_HonorCanonicalTokensAndBoundedSynthesisKeys()
    {
        var sampleDate = new DateTimeOffset(2026, 2, 17, 10, 0, 0, TimeSpan.FromHours(8));
        string lunar = CustomWidgetCalendarEngine.ResolveToken("lunar", sampleDate, cultureName: "zh-CN");
        string lunarDateAlias = CustomWidgetCalendarEngine.ResolveToken("lunarDate", sampleDate, cultureName: "zh-CN");
        string festival = CustomWidgetCalendarEngine.ResolveToken("festival", sampleDate, cultureName: "zh-CN");
        string holidayAlias = CustomWidgetCalendarEngine.ResolveToken("holidayName", sampleDate, cultureName: "zh-CN");

        Assert.False(string.IsNullOrWhiteSpace(lunar));
        Assert.Equal(lunar, lunarDateAlias);
        Assert.Equal(festival, holidayAlias);

        Assert.Equal("tick", CustomWidgetAudioService.ResolveCanonicalCueKey("click"));
        Assert.Equal("soft", CustomWidgetAudioService.ResolveCanonicalCueKey("gentle"));
        Assert.Equal("alert", CustomWidgetAudioService.ResolveCanonicalCueKey("error"));
        Assert.Equal("complete", CustomWidgetAudioService.ResolveCanonicalCueKey("arbitrary_dynamic_99"));

        Assert.Equal("rain", CustomWidgetAudioService.ResolveCanonicalNoiseKind("rain"));
        Assert.Equal("brown", CustomWidgetAudioService.ResolveCanonicalNoiseKind("brown"));
        Assert.Equal("white", CustomWidgetAudioService.ResolveCanonicalNoiseKind("custom_storm_42"));
    }

    private sealed class StallingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class YieldingWriteMemoryStream : Stream
    {
        private readonly MemoryStream _inner = new();

        public byte[] ToArray() => _inner.ToArray();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await Task.Yield();
            await _inner.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            int split = buffer.Length / 2;
            await _inner.WriteAsync(buffer[..split], cancellationToken).ConfigureAwait(false);
            await Task.Yield();
            await _inner.WriteAsync(buffer[split..], cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class OversizedSseHttpMessageHandler(int lineBytesWithoutNewline) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new RepeatedAsciiByteStream((byte)'x', lineBytesWithoutNewline))
            };
            response.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(response);
        }
    }

    private sealed class RepeatedAsciiByteStream(byte fillByte, int totalBytes) : Stream
    {
        private int _remaining = totalBytes;
        private bool _sentPrefix;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadSpan(buffer.AsSpan(offset, count));

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ReadSpan(buffer.Span));

        private int ReadSpan(Span<byte> destination)
        {
            if (destination.IsEmpty || _remaining <= 0)
            {
                return 0;
            }

            if (!_sentPrefix && destination.Length >= 6)
            {
                _sentPrefix = true;
                "data: "u8.CopyTo(destination);
                int consumed = Math.Min(6, _remaining);
                _remaining -= consumed;
                return consumed;
            }

            int toWrite = Math.Min(destination.Length, Math.Min(16384, _remaining));
            destination[..toWrite].Fill(fillByte);
            _remaining -= toWrite;
            return toWrite;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void CustomWidgetScriptEngine_ComputesMinimalReferencedEnvFlags_AndReusesThreadBuffer()
    {
        CustomWidgetScriptCompileResult pureCounter = CustomWidgetScriptEngine.Compile(
            "export function onTick(ctx) { ctx.state.count = String((Number(ctx.state.count || '0') + 1)); }");
        Assert.True(pureCounter.IsSuccess);
        Assert.NotNull(pureCounter.Compiled);
        Assert.Equal(CustomWidgetScriptEnvFlags.None, pureCounter.Compiled!.ReferencedEnvFlags);

        CustomWidgetScriptCompileResult weatherAndMusic = CustomWidgetScriptEngine.Compile(
            "export function onTick(ctx) { ctx.state.summary = `${ctx.env.weather?.temp ?? ''} | ${ctx.env.music?.title ?? ''}`; }");
        Assert.True(weatherAndMusic.IsSuccess);
        Assert.Equal(
            CustomWidgetScriptEnvFlags.Weather | CustomWidgetScriptEnvFlags.Music,
            weatherAndMusic.Compiled!.ReferencedEnvFlags);

        CustomWidgetScriptCompileResult reflective = CustomWidgetScriptEngine.Compile(
            "export function onTick(ctx) { ctx.state.dump = JSON.stringify(ctx.env); }");
        Assert.True(reflective.IsSuccess);
        Assert.Equal(CustomWidgetScriptEnvFlags.All, reflective.Compiled!.ReferencedEnvFlags);

        if (CustomWidgetScriptEngine.IsNativeEngineAvailable(out _))
        {
            CustomWidgetScriptExecutionResult exec = CustomWidgetScriptEngine.Execute(
                pureCounter.Compiled,
                "onTick",
                new CustomWidgetScriptExecutionInput
                {
                    State = new Dictionary<string, string>(StringComparer.Ordinal) { ["count"] = "41" },
                    CallKind = CustomWidgetScriptCallKind.Tick
                });
            Assert.True(exec.IsSuccess);
            Assert.Equal("42", exec.StateSet["count"]);
        }
    }

    [Fact]
    public void CustomWidgetManifest_I18nDictionaryCache_IsThreadSafeAcrossConcurrentCultures()
    {
        var manifest = new CustomWidgetManifest
        {
            I18n = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["en-US"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["title"] = "Hello" },
                ["zh-CN"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["title"] = "你好" },
                ["ja-JP"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["title"] = "こんにちは" }
            }
        };

        string[] cultures = ["en-US", "zh-CN", "ja-JP"];
        Parallel.For(0, 240, index =>
        {
            string culture = cultures[index % cultures.Length];
            IReadOnlyDictionary<string, string> dict = manifest.ResolveI18nDictionary(culture);
            string expected = culture switch
            {
                "zh-CN" => "你好",
                "ja-JP" => "こんにちは",
                _ => "Hello"
            };
            Assert.Equal(expected, dict["title"]);
        });
    }

    [Fact]
    public void NativeShortcutBackend_LongTargetPathOver260Chars_RoundTripsWithoutTruncation()
    {
        ShortcutNativeLoadResult load = ShortcutNativeModule.Default;
        Assert.True(load.Success, load.Detail);
        Assert.NotNull(load.Module);

        string tempRoot = Path.Combine(Path.GetTempPath(), $"dbx-longlnk-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            string deepSubdir = Path.Combine(
                tempRoot,
                new string('a', 85),
                new string('b', 85),
                new string('c', 85));
            Directory.CreateDirectory(@"\\?\" + deepSubdir);
            string longTargetFile = Path.Combine(deepSubdir, "target-payload.txt");
            File.WriteAllText(@"\\?\" + longTargetFile, "ok");
            Assert.True(longTargetFile.Length > 270);

            string shortcutPath = Path.Combine(tempRoot, "long-target.lnk");
            ShortcutNativeWriteCallResult writeResult = load.Module.WriteShortcut(
                shortcutPath,
                new DeskBox.Helpers.ShortcutInfo(
                    longTargetFile,
                    "Long target test",
                    "--flag",
                    tempRoot,
                    string.Empty,
                    0));
            Assert.True(writeResult.Success, writeResult.Detail);

            ShortcutNativeCallResult rawRead = load.Module.ReadStoredRaw(shortcutPath);
            Assert.True(rawRead.Success, rawRead.Detail);
            Assert.Equal(longTargetFile, rawRead.Metadata!.TargetPath);
            Assert.Equal(0u, rawRead.SourceTruncatedFields);
        }
        finally
        {
            try
            {
                Directory.Delete(@"\\?\" + tempRoot, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void AppLogIsolation_WritesDateStampedEntriesOnlyToTestLogOverride()
    {
        string tempLogPath = Path.Combine(
            Path.GetTempPath(),
            "deskbox-tests",
            $"isolated-{Guid.NewGuid():N}.log");
        string? previousOverride = DeskBox.App.TestLogFilePathOverride;
        try
        {
            DeskBox.App.TestLogFilePathOverride = tempLogPath;
            string marker = $"log-isolation-{Guid.NewGuid():N}";
            DeskBox.App.Log(marker);
            DeskBox.App.DrainLogQueue();

            Assert.True(File.Exists(tempLogPath));
            string content = File.ReadAllText(tempLogPath);
            Assert.Contains(marker, content, StringComparison.Ordinal);
            Assert.Matches(@"\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\]", content);
        }
        finally
        {
            DeskBox.App.TestLogFilePathOverride = previousOverride;
            try
            {
                if (File.Exists(tempLogPath))
                {
                    File.Delete(tempLogPath);
                }
            }
            catch
            {
            }
        }
    }
}
