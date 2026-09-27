using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace StrmTool.Tests;

public sealed class MediaInfoServiceTests
{
    [Fact]
    public async Task SavingStreamsPreservesExternalTracksAndUpdatesVideoFields()
    {
        var subtitle = new MediaStream { Index = 0, Type = MediaStreamType.Subtitle, IsExternal = true, Path = "/movie.zh.srt", Language = "zho" };
        var audio = new MediaStream { Index = 3, Type = MediaStreamType.Audio, IsExternal = true, Path = "/movie.en.aac" };
        var item = new TestVideo { Id = Guid.NewGuid(), DefaultVideoStreamIndex = 1, Streams = new List<MediaStream> { subtitle, audio } };
        var incoming = new List<MediaStream>
        {
            new() { Index = 0, Type = MediaStreamType.Video },
            new() { Index = 1, Type = MediaStreamType.Audio },
            new() { Index = 2, Type = MediaStreamType.Subtitle, IsExternal = true, Path = subtitle.Path, Language = "old" }
        };
        List<MediaStream> saved = null;
        var repository = Proxy<IMediaStreamRepository>((method, args) => { saved = ((IReadOnlyList<MediaStream>)args[1]).ToList(); return null; });
        var service = new StrmMediaInfoService(null, null, repository, null, NullLogger.Instance);
        await service.SaveMediaStreamsAsync(item, incoming, CancellationToken.None);
        Assert.Equal(4, saved.Count);
        Assert.Equal(4, saved.Select(s => s.Index).Distinct().Count());
        Assert.Equal("zho", saved.Single(s => s.Type == MediaStreamType.Subtitle).Language);
        Assert.Contains(saved, s => s.Path == audio.Path);
        Assert.Equal(0, item.DefaultVideoStreamIndex);
        Assert.True(item.HasSubtitles);
        Assert.Equal(1, item.Saves);
        Assert.Equal(0, subtitle.Index);
        Assert.Equal(2, incoming[2].Index);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task ExtractionPersistsFreshMetadataRegardlessOfCacheSettings(bool cacheEnabled, bool ignoreCache)
    {
        string directory = Path.Combine(Path.GetTempPath(), "strmtool-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var item = new TestVideo
            {
                Id = Guid.NewGuid(), Path = Path.Combine(directory, "movie.strm"),
                DefaultVideoStreamIndex = 99, HasSubtitles = true,
                Size = 100000, RunTimeTicks = 72000000000, Container = "mkv", Width = 1920, Height = 1080, TotalBitrate = 1000
            };
            File.WriteAllText(item.Path, "https://example.invalid/old.mkv");
            var cache = new MediaInfoCache(NullLogger.Instance);
            await cache.SaveFullCacheAsync(item.Path,
                new List<MediaStream> { new() { Type = MediaStreamType.Video, Index = 0, Width = 1920, Height = 1080 } },
                100000, 72000000000, "mkv");
            File.WriteAllText(item.Path, "https://example.invalid/new.mp4");
            var mediaInfo = new MediaInfo
            {
                Size = 200000, RunTimeTicks = 36000000000, Container = "mp4", Bitrate = 2000,
                MediaStreams = new List<MediaStream> { new() { Index = 0, Type = MediaStreamType.Video, Width = 3840, Height = 2160 } }
            };
            var encoder = Proxy<IMediaEncoder>((method, args) => Task.FromResult(mediaInfo));
            var repository = Proxy<IMediaStreamRepository>((method, args) => { item.Streams = ((IReadOnlyList<MediaStream>)args[1]).ToList(); return null; });
            var library = Proxy<ILibraryManager>((method, args) => null);
            using var task = new ExtractHarness(library, encoder, repository, cacheEnabled, ignoreCache);
            await task.ExtractSingleItemAsync(item, CancellationToken.None);
            Assert.Equal(200000L, item.Size);
            Assert.Equal(36000000000L, item.RunTimeTicks);
            Assert.Equal("mp4", item.Container);
            Assert.Equal(3840, item.Width);
            Assert.Equal(2160, item.Height);
            Assert.Equal(2000, item.TotalBitrate);
            Assert.Equal(0, item.DefaultVideoStreamIndex);
            Assert.False(item.HasSubtitles);
            Assert.True(item.Saves > 0);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void LibraryQueryIncludesMusicVideos()
    {
        var video = new TestVideo { Path = "/music/clip.strm" };
        var library = Proxy<ILibraryManager>((method, args) =>
        {
            var query = (InternalItemsQuery)args[0];
            return query.IncludeItemTypes.Contains(BaseItemKind.MusicVideo) ? new List<BaseItem> { video } : new List<BaseItem>();
        });
        var service = new StrmMediaInfoService(library, null, null, null, NullLogger.Instance);
        Assert.Same(video, Assert.Single(service.GetAllStrmItems(CancellationToken.None)));
    }

    private static T Proxy<T>(Func<MethodInfo, object[], object> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, TestProxy>();
        ((TestProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    public class TestProxy : DispatchProxy
    {
        public Func<MethodInfo, object[], object> Handler { get; set; }
        protected override object Invoke(MethodInfo method, object[] args) => Handler(method, args);
    }

    private sealed class TestVideo : Video, IHasMediaSources
    {
        public List<MediaStream> Streams { get; set; } = new();
        public int Saves { get; private set; }
        IReadOnlyList<MediaStream> IHasMediaSources.GetMediaStreams() => Streams;
        public override Task UpdateToRepositoryAsync(ItemUpdateType reason, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Saves++;
            return Task.CompletedTask;
        }
    }

    private sealed class ExtractHarness : ExtractTask
    {
        public ExtractHarness(ILibraryManager library, IMediaEncoder encoder, IMediaStreamRepository repository, bool cacheEnabled, bool ignoreCache)
            : base(library, encoder, repository, null, NullLogger<ExtractTask>.Instance)
        {
            _config = new PluginConfiguration { EnableAutoExtract = false, EnableMediaInfoCache = cacheEnabled, ForceRefreshIgnoreCache = ignoreCache, ForceRefreshIgnoreExisting = true, RefreshDelayMs = 0 };
        }
    }
}
