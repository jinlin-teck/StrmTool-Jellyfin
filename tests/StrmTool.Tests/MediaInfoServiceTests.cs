using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
            Assert.True(item.IsShortcut);
            Assert.Equal("https://example.invalid/new.mp4", item.ShortcutPath);
            Assert.True(item.Saves > 0);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task SavingAudioStreamsDiscoversLocalLyricsAndSetsAudioFlags()
    {
        string directory = Path.Combine(Path.GetTempPath(), "strmtool-audio-lyric-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string strmPath = Path.Combine(directory, "song.strm");
            string lrcPath = Path.Combine(directory, "song.lrc");
            File.WriteAllText(strmPath, "https://example.invalid/music/song.mp3");
            File.WriteAllText(lrcPath, "[00:00.00]Test lyric");

            var audio = new TestAudio { Id = Guid.NewGuid(), Path = strmPath };
            var incoming = new List<MediaStream>
            {
                new() { Index = 0, Type = MediaStreamType.Audio, Codec = "mp3" },
                new() { Index = 1, Type = MediaStreamType.EmbeddedImage, Codec = "mjpeg" }
            };
            List<MediaStream> saved = null;
            var repository = Proxy<IMediaStreamRepository>((method, args) =>
            {
                saved = ((IReadOnlyList<MediaStream>)args[1]).ToList();
                return null;
            });
            var service = new StrmMediaInfoService(null, null, repository, null, NullLogger.Instance);

            await service.SaveMediaStreamsAsync(audio, incoming, CancellationToken.None);

            Assert.Equal(3, saved.Count);
            var lyricStream = Assert.Single(saved, s => s.Type == MediaStreamType.Lyric);
            Assert.Equal(lrcPath, lyricStream.Path);
            Assert.Equal(2, lyricStream.Index);
            Assert.True(audio.HasLyrics);
            Assert.Equal(new[] { lrcPath }, audio.LyricFiles);

            // 删除本地歌词文件后再次保存：陈旧本地歌词流自愈剔除，并同步清空 LyricFiles
            audio.Streams = saved;
            File.Delete(lrcPath);
            await service.SaveMediaStreamsAsync(audio, incoming, CancellationToken.None);
            Assert.DoesNotContain(saved, s => s.Type == MediaStreamType.Lyric);
            Assert.False(audio.HasLyrics);
            Assert.Empty(audio.LyricFiles);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void CacheRestoreRecoversAudioLyricFlagsAndShortcutPath()
    {
        string directory = Path.Combine(Path.GetTempPath(), "strmtool-audio-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string strmPath = Path.Combine(directory, "song.strm");
            string lrcPath = Path.Combine(directory, "song.lrc");
            string remoteUrl = "https://example.invalid/music/song.mp3";
            File.WriteAllText(strmPath, remoteUrl);
            File.WriteAllText(lrcPath, "[00:00.00]Lyric");

            var audio = new TestAudio
            {
                Id = Guid.NewGuid(),
                Path = strmPath,
                Size = 5000000,
                RunTimeTicks = 100000000,
                Container = "mp3",
                TotalBitrate = 192000,
                HasLyrics = false,
                LyricFiles = Array.Empty<string>(),
                IsShortcut = false,
                ShortcutPath = null
            };

            var cacheData = new MediaInfoCacheData
            {
                IsValid = true,
                Size = 5000000,
                RunTimeTicks = 100000000,
                Container = "mp3",
                TotalBitrate = 192000,
                MediaStreams = new List<MediaStream>
                {
                    new() { Index = 0, Type = MediaStreamType.Audio, Codec = "mp3" },
                    new() { Index = 1, Type = MediaStreamType.Lyric, Path = lrcPath }
                }
            };

            Assert.True(StrmMediaInfoService.NeedsRestore(audio, cacheData));
            Assert.True(StrmMediaInfoService.TryRestoreMetadataFromCache(audio, cacheData));
            Assert.True(audio.HasLyrics);
            Assert.Equal(new[] { lrcPath }, audio.LyricFiles);
            Assert.True(audio.IsShortcut);
            Assert.Equal(remoteUrl, audio.ShortcutPath);
            Assert.False(StrmMediaInfoService.NeedsRestore(audio, cacheData));
            Assert.False(StrmMediaInfoService.TryRestoreMetadataFromCache(audio, cacheData));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task MediaSourceDecoratorRewritesStrmAudioAndSkipsRedundantPlaybackProbe()
    {
        string directory = Path.Combine(Path.GetTempPath(), "strmtool-audio-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string strmPath = Path.Combine(directory, "track.strm");
            string shortcutUrl = "https://example.invalid/music/shortcut.mp3";
            string fallbackFileUrl = "https://example.invalid/music/%E8%88%9E%E5%BF%B5.mp3?sign=abc";
            File.WriteAllText(strmPath, fallbackFileUrl);

            var audio = new TestAudio
            {
                Id = Guid.NewGuid(),
                Path = strmPath,
                Container = "strm",
                IsShortcut = true,
                ShortcutPath = shortcutUrl,
                Streams = new List<MediaStream>
                {
                    new() { Index = 0, Type = MediaStreamType.Audio, Codec = "mp3" }
                }
            };

            bool? capturedAllowProbe = null;
            var inner = Proxy<IMediaSourceManager>((method, args) =>
            {
                switch (method.Name)
                {
                    case nameof(IMediaSourceManager.GetMediaStreams):
                        return (IReadOnlyList<MediaStream>)audio.Streams;
                    case nameof(IMediaSourceManager.GetPathProtocol):
                        return ((string)args[0]).StartsWith("http", StringComparison.OrdinalIgnoreCase)
                            ? MediaProtocol.Http
                            : MediaProtocol.File;
                    case nameof(IMediaSourceManager.SupportsDirectStream):
                        return (MediaProtocol)args[1] == MediaProtocol.Http;
                    case nameof(IMediaSourceManager.GetStaticMediaSources):
                        return (IReadOnlyList<MediaSourceInfo>)new List<MediaSourceInfo>
                        {
                            new()
                            {
                                Id = audio.Id.ToString("N"),
                                Path = strmPath,
                                Protocol = MediaProtocol.File,
                                IsRemote = false,
                                Container = "strm",
                                SupportsDirectStream = false,
                                MediaStreams = audio.Streams
                            }
                        };
                    case nameof(IMediaSourceManager.GetPlaybackMediaSources):
                        capturedAllowProbe = (bool)args[2];
                        return Task.FromResult<IReadOnlyList<MediaSourceInfo>>(new List<MediaSourceInfo>
                        {
                            new()
                            {
                                Id = audio.Id.ToString("N"),
                                Path = strmPath,
                                Protocol = MediaProtocol.File,
                                IsRemote = false,
                                Container = "strm",
                                SupportsDirectStream = false,
                                MediaStreams = audio.Streams
                            }
                        });
                    default:
                        throw new NotSupportedException(method.Name);
                }
            });

            var emptyServices = new ServiceCollection();
            new PluginServiceRegistrator().RegisterServices(emptyServices, null);
            Assert.False(PluginServiceRegistrator.IsRegistered);

            var services = new ServiceCollection();
            services.AddSingleton<ILogger<StrmMediaSourceManagerDecorator>>(NullLogger<StrmMediaSourceManagerDecorator>.Instance);
            services.AddSingleton(inner);
            new PluginServiceRegistrator().RegisterServices(services, null);
            Assert.True(PluginServiceRegistrator.IsRegistered);
            using var provider = services.BuildServiceProvider();
            var manager = provider.GetRequiredService<IMediaSourceManager>();

            // 热路径优先使用内存中的 ShortcutPath，不读盘；且 SupportsDirectStream 可从 false 升级为 true
            var staticSource = Assert.Single(manager.GetStaticMediaSources(audio, enablePathSubstitution: true));
            Assert.Equal(shortcutUrl, staticSource.Path);
            Assert.Equal(MediaProtocol.Http, staticSource.Protocol);
            Assert.True(staticSource.IsRemote);
            Assert.True(staticSource.SupportsDirectStream);
            Assert.Equal("mp3", staticSource.Container);
            Assert.Equal(0, staticSource.DefaultAudioStreamIndex);

            // ShortcutPath 缺失时回退读取 .strm 文件并写回内存 ShortcutPath
            audio.ShortcutPath = null;
            audio.IsShortcut = false;
            var playbackSource = Assert.Single(await manager.GetPlaybackMediaSources(
                audio,
                user: null,
                allowMediaProbe: true,
                enablePathSubstitution: true,
                CancellationToken.None));
            Assert.False(capturedAllowProbe);
            Assert.Equal(fallbackFileUrl, playbackSource.Path);
            Assert.Equal(fallbackFileUrl, audio.ShortcutPath);
            Assert.True(audio.IsShortcut);
            Assert.Equal(MediaProtocol.Http, playbackSource.Protocol);
            Assert.True(playbackSource.IsRemote);
            Assert.True(playbackSource.SupportsDirectStream);

            // 尚无音频流时保留 allowMediaProbe=true 兜底
            audio.Streams.Clear();
            await manager.GetPlaybackMediaSources(
                audio,
                user: null,
                allowMediaProbe: true,
                enablePathSubstitution: true,
                CancellationToken.None);
            Assert.True(capturedAllowProbe);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
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

    private sealed class TestAudio : Audio, IHasMediaSources
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
