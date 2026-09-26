using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Serialization;
using MediaBrowser.Model.Entities;
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
            Assert.Equal(json, File.ReadAllText(CachePath));
        }

        [Fact]
        public void StreamsReaderStillRequiresMediaStreamsWhenValidationIsDisabled()
        {
            File.WriteAllText(StrmPath, "https://example.invalid/media-a.mkv");
            const string json = "{\"isValid\":true,\"size\":100}";
            File.WriteAllText(CachePath, json);

            Assert.True(_cache.TryGetFullCache(StrmPath, out _, verifyContentHash: false));
            Assert.False(_cache.TryGetCachedMediaStreams(StrmPath, out _, verifyContentHash: false));
            Assert.Equal(json, File.ReadAllText(CachePath));
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
                    await _cache.SaveFullCacheAsync(StrmPath, Streams("hevc"), 200, 2000, "mp4");
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
            await _cache.SaveFullCacheAsync(StrmPath, Streams("hevc"), 200, 2000, "mp4",
                expectedStrmContentHash: original.StrmContentHash);

            Assert.Equal(originalJson, File.ReadAllText(CachePath));
        }

        private async Task<MediaInfoCacheData> SaveOriginalAsync()
        {
            File.WriteAllText(StrmPath, "https://example.invalid/media-a.mkv");
            await _cache.SaveFullCacheAsync(StrmPath, Streams("h264"), 100, 1000, "mkv");
            Assert.True(_cache.TryGetFullCache(StrmPath, out var original, verifyContentHash: true));
            return original;
        }

        private bool ReadCache(bool streamsOnly, bool? verifyContentHash)
        {
            return streamsOnly
                ? _cache.TryGetCachedMediaStreams(StrmPath, out _, verifyContentHash)
                : _cache.TryGetFullCache(StrmPath, out _, verifyContentHash);
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
