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
                new MediaInfoCacheData { Size = 100000, RunTimeTicks = 72000000000, Container = "mkv" });
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

    [Fact]
    public async Task AudioProbeExtractsTagsSyncsParentAlbumAndUpgradesLegacyCache()
    {
        string rootDir = Path.Combine(Path.GetTempPath(), "strmtool-audio-tags-" + Guid.NewGuid().ToString("N"));
        string artistDir = Path.Combine(rootDir, "卓依婷");
        string albumDir = Path.Combine(artistDir, "皇牌影视金曲1");
        Directory.CreateDirectory(albumDir);
        try
        {
            string strmPath = Path.Combine(albumDir, "卓依婷 - 婉君.strm");
            File.WriteAllText(strmPath, "https://example.invalid/music/wanjun.mp3");

            var artistEntity = new MusicArtist { Id = Guid.NewGuid(), Name = "卓依婷", Path = artistDir };
            var albumEntity = new TestMusicAlbum
            {
                Id = Guid.NewGuid(),
                ParentId = artistEntity.Id,
                Name = "皇牌影视金曲1",
                Path = albumDir
            };
            var audio = new TestAudio
            {
                Id = Guid.NewGuid(),
                ParentId = albumEntity.Id,
                Name = "卓依婷 - 婉君",
                Path = strmPath,
                Streams = new List<MediaStream> { new() { Index = 0, Type = MediaStreamType.Audio, Codec = "mp3" } }
            };

            // 先写入一份旧版缓存（无 audioTagsProbed 和艺术家字段）
            var cache = new MediaInfoCache(NullLogger.Instance);
            await cache.SaveFullCacheAsync(strmPath, audio.Streams, new MediaInfoCacheData { Size = 10312071, RunTimeTicks = 2549812250, Container = "mp3", TotalBitrate = 323539 });

            int probeCalls = 0;
            var mediaInfo = new MediaInfo
            {
                Name = "婉君",
                Album = "皇牌影视金曲1",
                Artists = new[] { "卓依婷" },
                AlbumArtists = new[] { "卓依婷" },
                IndexNumber = 3,
                ProductionYear = 2010,
                Genres = new[] { "Blues" },
                Size = 10312071,
                RunTimeTicks = 2549812250,
                Container = "mp3",
                Bitrate = 323539,
                MediaStreams = new List<MediaStream> { new() { Index = 0, Type = MediaStreamType.Audio, Codec = "mp3" } }
            };

            var peopleByItem = new Dictionary<Guid, List<PersonInfo>>();
            var itemsById = new Dictionary<Guid, BaseItem>
            {
                [artistEntity.Id] = artistEntity,
                [albumEntity.Id] = albumEntity,
                [audio.Id] = audio
            };

            var encoder = Proxy<IMediaEncoder>((method, args) =>
            {
                probeCalls++;
                return Task.FromResult(mediaInfo);
            });
            var repository = Proxy<IMediaStreamRepository>((method, args) =>
            {
                audio.Streams = ((IReadOnlyList<MediaStream>)args[1]).ToList();
                return null;
            });
            var library = Proxy<ILibraryManager>((method, args) =>
            {
                switch (method.Name)
                {
                    case nameof(ILibraryManager.GetItemById):
                        return itemsById.TryGetValue((Guid)args[0], out var found) ? found : null;
                    case nameof(ILibraryManager.UpdatePeople):
                        peopleByItem[((BaseItem)args[0]).Id] = ((List<PersonInfo>)args[1]).ToList();
                        return null;
                    case nameof(ILibraryManager.GetArtist):
                        return artistEntity;
                    default:
                        return null;
                }
            });

            // 即使 ForceRefreshIgnoreExisting=false 且已有旧版缓存与音频流，因缺失艺术家且旧缓存未探测过音频标签，应自动探测并升级缓存
            using var task = new ExtractHarness(library, encoder, repository, cacheEnabled: true, ignoreCache: false, ignoreExisting: false);
            await task.ExtractSingleItemAsync(audio, CancellationToken.None);

            Assert.Equal(1, probeCalls);
            Assert.Equal("婉君", audio.Name);
            Assert.Equal("皇牌影视金曲1", audio.Album);
            Assert.Equal(new[] { "卓依婷" }, audio.Artists);
            Assert.Equal(new[] { "卓依婷" }, audio.AlbumArtists);
            Assert.Equal(3, audio.IndexNumber);
            Assert.Equal(2010, audio.ProductionYear);
            Assert.Equal(new[] { "Blues" }, audio.Genres);

            // 父级专辑应自动同步 AlbumArtists / Artists / 年份 / 流派 / People
            Assert.Equal(new[] { "卓依婷" }, albumEntity.AlbumArtists);
            Assert.Equal(new[] { "卓依婷" }, albumEntity.Artists);
            Assert.Equal(2010, albumEntity.ProductionYear);
            Assert.Equal(new[] { "Blues" }, albumEntity.Genres);
            Assert.Equal(1, albumEntity.Saves);
            Assert.True(peopleByItem.ContainsKey(audio.Id));
            Assert.True(peopleByItem.ContainsKey(albumEntity.Id));

            // 缓存已升级包含音频标签，再次调用 ExtractSingleItemAsync 应直接跳过，不重复探测
            Assert.True(cache.TryGetFullCache(strmPath, out var upgradedCache));
            Assert.True(upgradedCache.AudioTagsProbed);
            Assert.Equal("婉君", upgradedCache.Title);
            Assert.Equal("皇牌影视金曲1", upgradedCache.Album);
            Assert.Equal(new[] { "卓依婷" }, upgradedCache.Artists);
            Assert.Equal(new[] { "卓依婷" }, upgradedCache.AlbumArtists);

            await task.ExtractSingleItemAsync(audio, CancellationToken.None);
            Assert.Equal(1, probeCalls);

            // 模拟 Jellyfin 重置音频元数据回默认值，测试从缓存恢复
            audio.Name = "卓依婷 - 婉君";
            audio.Album = null;
            audio.Artists = Array.Empty<string>();
            audio.AlbumArtists = Array.Empty<string>();
            audio.IndexNumber = null;
            audio.ProductionYear = null;
            audio.Genres = Array.Empty<string>();

            Assert.True(StrmMediaInfoService.NeedsRestore(audio, upgradedCache, library));
            Assert.True(StrmMediaInfoService.TryRestoreMetadataFromCache(audio, upgradedCache, library));
            Assert.Equal("婉君", audio.Name);
            Assert.Equal("皇牌影视金曲1", audio.Album);
            Assert.Equal(new[] { "卓依婷" }, audio.Artists);
            Assert.Equal(new[] { "卓依婷" }, audio.AlbumArtists);
            Assert.Equal(3, audio.IndexNumber);
            Assert.Equal(2010, audio.ProductionYear);
            Assert.Equal(new[] { "Blues" }, audio.Genres);
            Assert.False(StrmMediaInfoService.NeedsRestore(audio, upgradedCache, library));
        }
        finally
        {
            Directory.Delete(rootDir, true);
        }
    }

    [Fact]
    public async Task AudioProbeFallsBackToFolderStructureWhenEmbeddedTagsMissing()
    {
        string rootDir = Path.Combine(Path.GetTempPath(), "strmtool-audio-fallback-" + Guid.NewGuid().ToString("N"));
        string artistDir = Path.Combine(rootDir, "卓依婷");
        string albumDir = Path.Combine(artistDir, "好春天");
        Directory.CreateDirectory(albumDir);
        try
        {
            string strmPath = Path.Combine(albumDir, "05 - 卓依婷 - 恭喜发财.strm");
            File.WriteAllText(strmPath, "https://example.invalid/music/gongxi.mp3");

            var audio = new TestAudio
            {
                Id = Guid.NewGuid(),
                Name = "05 - 卓依婷 - 恭喜发财",
                Path = strmPath
            };

            // 远程探测仅返回流信息，无任何内嵌标签
            var mediaInfo = new MediaInfo
            {
                Size = 8000000,
                RunTimeTicks = 2000000000,
                Container = "mp3",
                Bitrate = 320000,
                MediaStreams = new List<MediaStream> { new() { Index = 0, Type = MediaStreamType.Audio, Codec = "mp3" } }
            };

            var encoder = Proxy<IMediaEncoder>((method, args) => Task.FromResult(mediaInfo));
            var repository = Proxy<IMediaStreamRepository>((method, args) =>
            {
                audio.Streams = ((IReadOnlyList<MediaStream>)args[1]).ToList();
                return null;
            });
            var service = new StrmMediaInfoService(null, encoder, repository, null, NullLogger.Instance);

            var result = await service.ProbeMediaStreamsAsync(audio, CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal("恭喜发财", audio.Name);
            Assert.Equal("好春天", audio.Album);
            Assert.Equal(new[] { "卓依婷" }, audio.Artists);
            Assert.Equal(new[] { "卓依婷" }, audio.AlbumArtists);
            Assert.Equal(5, audio.IndexNumber);
        }
        finally
        {
            Directory.Delete(rootDir, true);
        }
    }

    [Fact]
    public async Task SyncAudioRelationshipsPreservesExistingNonArtistPeopleAndReportsChangesIdempotently()
    {
        var audio = new TestAudio
        {
            Id = Guid.NewGuid(),
            ParentId = Guid.NewGuid(),
            Name = "婉君",
            Album = "皇牌影视金曲1",
            Artists = new[] { "卓依婷" },
            AlbumArtists = new[] { "卓依婷" }
        };

        var storedPeople = new List<PersonInfo>
        {
            new() { Name = "左宏元", Type = PersonKind.Composer },
            new() { Name = "琼瑶", Type = PersonKind.Lyricist }
        };

        int updatePeopleCalls = 0;
        int getItemByIdCalls = 0;
        var library = Proxy<ILibraryManager>((method, args) =>
        {
            switch (method.Name)
            {
                case nameof(ILibraryManager.GetPeople):
                    return (IReadOnlyList<PersonInfo>)storedPeople.ToList();
                case nameof(ILibraryManager.UpdatePeople):
                    updatePeopleCalls++;
                    storedPeople = ((List<PersonInfo>)args[1]).ToList();
                    return null;
                case nameof(ILibraryManager.GetItemById):
                    getItemByIdCalls++;
                    return null;
                default:
                    return null;
            }
        });

        // 轻量筛选 checkParentAlbum=false 时，若单曲自身 Album/Artists/AlbumArtists 已完整，不查询父链
        Assert.False(StrmMediaInfoService.HasMissingAudioMetadata(audio, library, checkParentAlbum: false));
        Assert.Equal(0, getItemByIdCalls);

        // 首次同步：即使无父级专辑，单曲 People 新增了 Artist/AlbumArtist 时也应返回 true，并保留原有的 Composer/Lyricist
        Assert.True(await StrmMediaInfoService.SyncAudioRelationshipsStaticAsync(audio, library, CancellationToken.None));
        Assert.Equal(1, updatePeopleCalls);
        Assert.Contains(storedPeople, p => p.Name == "左宏元" && p.Type == PersonKind.Composer);
        Assert.Contains(storedPeople, p => p.Name == "琼瑶" && p.Type == PersonKind.Lyricist);
        Assert.Contains(storedPeople, p => p.Name == "卓依婷" && p.Type == PersonKind.AlbumArtist);
        Assert.Contains(storedPeople, p => p.Name == "卓依婷" && p.Type == PersonKind.Artist);

        // 二次同步：人员已完整，不应重复调用 UpdatePeople 且返回 false
        Assert.False(await StrmMediaInfoService.SyncAudioRelationshipsStaticAsync(audio, library, CancellationToken.None));
        Assert.Equal(1, updatePeopleCalls);

        // PopulateProbeResult 在 mediaInfo == null 时不应将 AudioTagsProbed 置为 true
        var probeResult = new MediaProbeResult();
        var helperType = typeof(StrmMediaInfoService).Assembly.GetType("StrmTool.AudioMetadataHelper", throwOnError: true);
        var populateMethod = helperType.GetMethod("PopulateProbeResult", BindingFlags.Public | BindingFlags.Static);
        populateMethod.Invoke(null, new object[] { audio, null, probeResult, null, null });
        Assert.Null(probeResult.AudioTagsProbed);
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

    private sealed class TestMusicAlbum : MusicAlbum
    {
        public int Saves { get; private set; }
        public override Task UpdateToRepositoryAsync(ItemUpdateType reason, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Saves++;
            return Task.CompletedTask;
        }
    }

    private sealed class ExtractHarness : ExtractTask
    {
        public ExtractHarness(
            ILibraryManager library,
            IMediaEncoder encoder,
            IMediaStreamRepository repository,
            bool cacheEnabled,
            bool ignoreCache,
            bool ignoreExisting = true)
            : base(library, encoder, repository, null, NullLogger<ExtractTask>.Instance)
        {
            _config = new PluginConfiguration
            {
                EnableAutoExtract = false,
                EnableMediaInfoCache = cacheEnabled,
                ForceRefreshIgnoreCache = ignoreCache,
                ForceRefreshIgnoreExisting = ignoreExisting,
                RefreshDelayMs = 0
            };
        }
    }
}
