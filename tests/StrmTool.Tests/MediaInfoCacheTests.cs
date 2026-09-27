using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace StrmTool.Tests
{
    public sealed class MediaInfoCacheTests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "strmtool-tests-" + Guid.NewGuid().ToString("N"));
        private readonly MediaInfoCache _cache = new MediaInfoCache(NullLogger.Instance);
        private string StrmPath => Path.Combine(_directory, "movie.strm");
        private string CachePath => Path.Combine(_directory, "movie.strmtool.json");

        public MediaInfoCacheTests()
        {
            Directory.CreateDirectory(_directory);
        }

        [Theory]
        [InlineData("/media/../secret.mp4", true)]
        [InlineData(@"C:\media\..\secret.mp4", true)]
        [InlineData(@"\\server\share\..\secret.mp4", true)]
        [InlineData("../movie.mp4", true)]
        [InlineData("/media/..", true)]
        [InlineData("..", true)]
        [InlineData("/media/./movie.mp4", false)]
        [InlineData("/media/movie..mp4", false)]
        [InlineData("/media/..movie.mp4", false)]
        [InlineData("/media/movie..", false)]
        [InlineData("/media/movie\0.mp4", true)]
        [InlineData("https://example.invalid/media/../secret.mp4", true)]
        [InlineData("https://example.invalid/media/%2e%2e/secret.mp4", true)]
        [InlineData("https://example.invalid/media%2f..%2fsecret.mp4", true)]
        [InlineData("https://example.invalid/media/%00movie.mp4", true)]
        [InlineData("https://example.invalid/movie.mp4?token=../value", false)]
        [InlineData("https://example.invalid?token=/../value", false)]
        [InlineData("https://example.invalid/movie.mp4#../fragment", false)]
        [InlineData("/media/%2e%2e/movie.mp4", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void TraversalValidationChecksPathSegmentsInsteadOfFilenameOrUrlParameters(string path, bool expected)
        {
            Assert.Equal(expected, MediaInfoCache.ContainsPathTraversal(path));
        }

        [Fact]
        public void MissingConfigurationFieldDefaultsToStrictValidation()
        {
            Assert.True(new PluginConfiguration().VerifyStrmContentHash);
            var serializer = new XmlSerializer(typeof(PluginConfiguration));
            using var reader = new StringReader("<PluginConfiguration />");
            var config = (PluginConfiguration)serializer.Deserialize(reader);
            Assert.True(config.VerifyStrmContentHash);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task MatchingCacheCanBeReadWithoutChangingTheFile(bool streamsOnly)
        {
            await SaveOriginalAsync();
            string originalJson = File.ReadAllText(CachePath);

            Assert.True(ReadCache(streamsOnly, true));
            Assert.Equal(originalJson, File.ReadAllText(CachePath));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ChangedSourceIsRejectedByDefaultAndExplicitStrictValidation(bool streamsOnly)
        {
            await SaveOriginalAsync();
            File.WriteAllText(StrmPath, "https://example.invalid/media-b.mkv");

            Assert.False(ReadCache(streamsOnly, null));
            Assert.False(ReadCache(streamsOnly, true));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task DisabledReadsPreserveOriginalFingerprintAndRejectCacheWhenReenabled(bool streamsOnly)
        {
            var original = await SaveOriginalAsync();
            string originalJson = File.ReadAllText(CachePath);
            File.WriteAllText(StrmPath, "https://example.invalid/media-b.mkv");

            Assert.True(ReadCache(streamsOnly, false));
            Assert.True(_cache.TryGetFullCache(StrmPath, out var reused, verifyContentHash: false));
            Assert.Equal(original.StrmContentHash, reused.StrmContentHash);
            Assert.Equal(100, reused.Size);
            Assert.Equal("h264", Assert.Single(reused.MediaStreams).Codec);
            Assert.Equal(originalJson, File.ReadAllText(CachePath));
            Assert.False(ReadCache(streamsOnly, true));
        }

        [Fact]
        public async Task LegacyCacheWithoutFingerprintIsNotSilentlyMigrated()
        {
            var original = await SaveOriginalAsync();
            original.StrmContentHash = null;
            string legacyJson = JsonSerializer.Serialize(original);
            File.WriteAllText(CachePath, legacyJson);

            Assert.True(_cache.TryGetFullCache(StrmPath, out var reused, verifyContentHash: false));
            Assert.Null(reused.StrmContentHash);
            Assert.True(_cache.TryGetCachedMediaStreams(StrmPath, out _, verifyContentHash: false));
            Assert.False(_cache.TryGetFullCache(StrmPath, out _, verifyContentHash: true));
            Assert.False(_cache.TryGetCachedMediaStreams(StrmPath, out _, verifyContentHash: true));
            Assert.Equal(legacyJson, File.ReadAllText(CachePath));
        }

        [Theory]
        [InlineData("null")]
        [InlineData("{}")]
        [InlineData("{\"isValid\":false,\"mediaStreams\":[]}")]
        [InlineData("{invalid-json")]
        public void DisablingFingerprintValidationDoesNotAcceptInvalidCache(string json)
        {
            File.WriteAllText(StrmPath, "https://example.invalid/media-a.mkv");
            File.WriteAllText(CachePath, json);

            Assert.False(_cache.TryGetFullCache(StrmPath, out _, verifyContentHash: false));
            Assert.False(_cache.TryGetCachedMediaStreams(StrmPath, out _, verifyContentHash: false));

            // 坏缓存被隔离为 .bak，原文件不再保留，后续探测会重新生成
            Assert.False(File.Exists(CachePath));
            Assert.True(File.Exists(CachePath + ".bak"));
        }

        [Fact]
        public async Task QuarantinedCacheCanBeRegeneratedByProbeSave()
        {
            File.WriteAllText(StrmPath, "https://example.invalid/media-a.mkv");
            File.WriteAllText(CachePath, "{invalid-json");

            Assert.False(_cache.TryGetFullCache(StrmPath, out _, verifyContentHash: false));
            Assert.True(File.Exists(CachePath + ".bak"));

            // 模拟重新探测后的保存：缓存文件重新生成且可通过指纹校验
            await _cache.SaveFullCacheAsync(StrmPath, Streams("h264"), CacheMeta(100, 1000, "mkv"));
            Assert.True(File.Exists(CachePath));
            Assert.True(_cache.TryGetFullCache(StrmPath, out var regenerated, verifyContentHash: true));
            Assert.Equal(100, regenerated.Size);
        }

        [Fact]
        public void StreamsReaderStillRequiresMediaStreamsWhenValidationIsDisabled()
        {
            File.WriteAllText(StrmPath, "https://example.invalid/media-a.mkv");
            const string json = "{\"isValid\":true,\"size\":100}";
            File.WriteAllText(CachePath, json);

            Assert.True(_cache.TryGetFullCache(StrmPath, out _, verifyContentHash: false));
            Assert.False(_cache.TryGetCachedMediaStreams(StrmPath, out _, verifyContentHash: false));

            // 缺少媒体流的坏缓存被隔离为 .bak
            Assert.False(File.Exists(CachePath));
            Assert.True(File.Exists(CachePath + ".bak"));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task RepeatedReadsDoNotPersistReturnedSnapshots(bool streamsOnly)
        {
            await SaveOriginalAsync();
            string originalJson = File.ReadAllText(CachePath);
            File.WriteAllText(StrmPath, "https://example.invalid/media-b.mkv");

            for (int i = 0; i < 128; i++)
            {
                if (streamsOnly)
                {
                    Assert.True(_cache.TryGetCachedMediaStreams(StrmPath, out var streams, verifyContentHash: false));
                    Assert.Single(streams).Codec = "modified-by-caller";
                }
                else
                {
                    Assert.True(_cache.TryGetFullCache(StrmPath, out var snapshot, verifyContentHash: false));
                    snapshot.Size = 999;
                    Assert.Single(snapshot.MediaStreams).Codec = "modified-by-caller";
                }
            }

            Assert.Equal(originalJson, File.ReadAllText(CachePath));
            Assert.False(ReadCache(streamsOnly, true));
            Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
        }

        [Fact]
        public async Task ConcurrentReadsDoNotOverwriteNewProbeData()
        {
            await SaveOriginalAsync();
            File.WriteAllText(StrmPath, "https://example.invalid/media-b.mkv");
            var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var readers = Enumerable.Range(0, 4).Select(readerIndex => Task.Run(async () =>
            {
                await start.Task;
                for (int i = 0; i < 128; i++)
                {
                    Assert.True(_cache.TryGetFullCache(StrmPath, out _, verifyContentHash: false));
                    Assert.True(_cache.TryGetCachedMediaStreams(StrmPath, out _, verifyContentHash: false));
                }
            })).ToArray();
            var writer = Task.Run(async () =>
            {
                await start.Task;
                for (int i = 0; i < 8; i++)
                {
                    await _cache.SaveFullCacheAsync(StrmPath, Streams("hevc"), CacheMeta(200, 2000, "mp4"));
                }
            });

            start.SetResult(true);
            await Task.WhenAll(readers.Append(writer));

            Assert.True(_cache.TryGetFullCache(StrmPath, out var current, verifyContentHash: true));
            Assert.Equal(200, current.Size);
            Assert.Equal(2000L, current.RunTimeTicks);
            Assert.Equal("mp4", current.Container);
            Assert.Equal("hevc", Assert.Single(current.MediaStreams).Codec);
            Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
        }

        [Fact]
        public async Task BypassingReadsDoesNotDisableProbeSaveConsistencyCheck()
        {
            var original = await SaveOriginalAsync();
            string originalJson = File.ReadAllText(CachePath);
            File.WriteAllText(StrmPath, "https://example.invalid/media-b.mkv");

            Assert.True(_cache.TryGetFullCache(StrmPath, out _, verifyContentHash: false));
            Assert.False(await _cache.SaveFullCacheAsync(StrmPath, Streams("hevc"), CacheMeta(200, 2000, "mp4"),
                expectedStrmContentHash: original.StrmContentHash));

            Assert.Equal(originalJson, File.ReadAllText(CachePath));
        }

        [Fact]
        public async Task ExportCannotRebindOldStreamsToChangedSource()
        {
            await SaveOriginalAsync();
            string originalJson = File.ReadAllText(CachePath);
            File.WriteAllText(StrmPath, "https://example.invalid/media-b.mkv");

            Assert.False(await _cache.SaveFullCacheAsync(StrmPath, Streams("h264"), CacheMeta(100, 1000, "mkv"),
                onlyIfMissing: true));

            Assert.Equal(originalJson, File.ReadAllText(CachePath));
            Assert.False(_cache.TryGetFullCache(StrmPath, out _, verifyContentHash: true));
        }

        [Theory]
        [InlineData("{invalid-json")]
        [InlineData("{\"isValid\":true,\"mediaStreams\":[]}")]
        public async Task ExportDoesNotOverwriteCorruptOrLegacyCache(string json)
        {
            File.WriteAllText(StrmPath, "https://example.invalid/media-a.mkv");
            File.WriteAllText(CachePath, json);
            Assert.False(await _cache.SaveFullCacheAsync(StrmPath, Streams("h264"), CacheMeta(100, 1000, "mkv"),
                onlyIfMissing: true));
            Assert.Equal(json, File.ReadAllText(CachePath));
        }

        [Fact]
        public async Task ConcurrentExportsAcrossInstancesOnlyCreateOneCache()
        {
            File.WriteAllText(StrmPath, "https://example.invalid/media-a.mkv");
            var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
                new MediaInfoCache(NullLogger.Instance).SaveFullCacheAsync(
                    StrmPath, Streams("h264"), CacheMeta(100, 1000, "mkv"), onlyIfMissing: true)));
            Assert.Single(results, saved => saved);
            Assert.True(_cache.TryGetFullCache(StrmPath, out _, verifyContentHash: true));
        }

        [Fact]
        public async Task SubtitleSnapshotDoesNotMutateInputAndRestoresAbsolutePaths()
        {
            File.WriteAllText(StrmPath, "https://example.invalid/media-a.mkv");
            string subtitlePath = Path.Combine(_directory, "movie.zh.srt");
            string remotePath = "https://example.invalid/movie.en.srt";
            var streams = Streams("h264");
            streams.Add(new MediaStream { Index = 1, Type = MediaStreamType.Subtitle, IsExternal = true, Path = subtitlePath });
            streams.Add(new MediaStream { Index = 2, Type = MediaStreamType.Subtitle, IsExternal = true, IsExternalUrl = true, Path = remotePath });
            Assert.True(await _cache.SaveFullCacheAsync(StrmPath, streams, CacheMeta(100, 1000, "mkv")));

            Assert.Equal(subtitlePath, streams[1].Path);
            var serialized = JsonSerializer.Deserialize<MediaInfoCacheData>(File.ReadAllText(CachePath));
            Assert.Equal("movie.zh.srt", serialized.MediaStreams[1].Path);
            Assert.Equal(remotePath, serialized.MediaStreams[2].Path);
            Assert.True(_cache.TryGetFullCache(StrmPath, out var full));
            Assert.Equal(subtitlePath, full.MediaStreams[1].Path);
            Assert.True(_cache.TryGetCachedMediaStreams(StrmPath, out var restored));
            Assert.Equal(subtitlePath, restored[1].Path);
            Assert.Equal(remotePath, restored[2].Path);
        }

        [Fact]
        public async Task SubtitlesOutsideStrmDirectoryAreNotMappedToWrongFiles()
        {
            File.WriteAllText(StrmPath, "https://example.invalid/media-a.mkv");
            var streams = Streams("h264");
            string outside = Path.Combine(Path.GetTempPath(), "elsewhere", "movie.srt");
            streams.Add(new MediaStream { Type = MediaStreamType.Subtitle, IsExternal = true, Path = outside });
            Assert.True(await _cache.SaveFullCacheAsync(StrmPath, streams, CacheMeta(100, 1000, "mkv")));
            Assert.Equal(outside, streams[1].Path);
            Assert.True(_cache.TryGetCachedMediaStreams(StrmPath, out var restored));
            Assert.Single(restored);
            Assert.DoesNotContain(outside, File.ReadAllText(CachePath));
        }

        [Fact]
        public async Task WriteFailureReturnsFalseWithoutLeavingTemporaryFiles()
        {
            File.WriteAllText(StrmPath, "https://example.invalid/media-a.mkv");
            // 使用目录占据目标路径，跨平台确定性触发 rename 失败（不依赖用户权限）。
            Directory.CreateDirectory(CachePath);
            Assert.False(await _cache.SaveFullCacheAsync(StrmPath, Streams("h264"), CacheMeta(100, 1000, "mkv")));
            Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
        }

        [Fact]
        public async Task TransientReadFailureDoesNotQuarantineValidCache()
        {
            await SaveOriginalAsync();
            using (var exclusive = new FileStream(CachePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.False(_cache.TryGetFullCache(StrmPath, out _));
                Assert.False(_cache.TryGetCachedMediaStreams(StrmPath, out _));
                Assert.True(File.Exists(CachePath));
                Assert.False(File.Exists(CachePath + ".bak"));
            }
            Assert.True(_cache.TryGetFullCache(StrmPath, out _));
        }

        [Fact]
        public async Task QuarantineAndWriterShareLockAcrossCacheInstances()
        {
            File.WriteAllText(StrmPath, "https://example.invalid/media-a.mkv");
            File.WriteAllText(CachePath, "{invalid-json");
            using var logger = new BlockingQuarantineLogger();
            var reader = Task.Run(() => new MediaInfoCache(logger).TryGetFullCache(StrmPath, out _));
            Task<bool> writer = null;
            bool snapshotRequested = false;
            IEnumerable<MediaStream> TrackedStreams()
            {
                snapshotRequested = true;
                yield return new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = "hevc" };
            }
            try
            {
                await logger.Quarantined.Task.WaitAsync(TimeSpan.FromSeconds(10));
                writer = _cache.SaveFullCacheAsync(Path.Combine(_directory, ".", "movie.strm"), TrackedStreams(), CacheMeta(200, 2000, "mp4"));
                // 别名路径必须归一到同一锁；写入不能越过锁去序列化快照或创建临时文件。
                Assert.False(snapshotRequested);
                Assert.False(writer.IsCompleted);
                Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
            }
            finally
            {
                logger.Release.Set();
                await reader.WaitAsync(TimeSpan.FromSeconds(10));
                if (writer != null)
                    await writer.WaitAsync(TimeSpan.FromSeconds(10));
            }
            Assert.True(await writer);
            Assert.True(_cache.TryGetFullCache(StrmPath, out var current));
            Assert.Equal("hevc", Assert.Single(current.MediaStreams).Codec);
            Assert.Equal("{invalid-json", File.ReadAllText(CachePath + ".bak"));
        }

        [Fact]
        public async Task CancelledSaveDoesNotCreateCache()
        {
            File.WriteAllText(StrmPath, "https://example.invalid/media-a.mkv");
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _cache.SaveFullCacheAsync(
                StrmPath, Streams("h264"), CacheMeta(100, 1000, "mkv"), cancellationToken: cts.Token));
            Assert.False(File.Exists(CachePath));
            Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
        }

        private sealed class BlockingQuarantineLogger : ILogger, IDisposable
        {
            public TaskCompletionSource<bool> Quarantined { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public ManualResetEventSlim Release { get; } = new(false);
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
                Func<TState, Exception, string> formatter)
            {
                if (formatter(state, exception).Contains("moved to", StringComparison.Ordinal))
                {
                    Quarantined.TrySetResult(true);
                    if (!Release.Wait(TimeSpan.FromSeconds(10)))
                        throw new TimeoutException("Quarantine test was not released");
                }
            }
            public void Dispose() => Release.Dispose();
        }

        private async Task<MediaInfoCacheData> SaveOriginalAsync()
        {
            File.WriteAllText(StrmPath, "https://example.invalid/media-a.mkv");
            await _cache.SaveFullCacheAsync(StrmPath, Streams("h264"), CacheMeta(100, 1000, "mkv"));
            Assert.True(_cache.TryGetFullCache(StrmPath, out var original, verifyContentHash: true));
            return original;
        }

        private bool ReadCache(bool streamsOnly, bool? verifyContentHash)
        {
            return streamsOnly
                ? _cache.TryGetCachedMediaStreams(StrmPath, out _, verifyContentHash)
                : _cache.TryGetFullCache(StrmPath, out _, verifyContentHash);
        }

        private static MediaInfoCacheData CacheMeta(long size, long? runTimeTicks, string container, int totalBitrate = 0)
        {
            return new MediaInfoCacheData
            {
                Size = size,
                RunTimeTicks = runTimeTicks,
                Container = container,
                TotalBitrate = totalBitrate
            };
        }

        private static List<MediaStream> Streams(string codec)
        {
            return new List<MediaStream>
            {
                new MediaStream { Index = 0, Codec = codec, Type = MediaStreamType.Video }
            };
        }

        public void Dispose()
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
