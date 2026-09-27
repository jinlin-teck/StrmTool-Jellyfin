using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace StrmTool
{
    /// <summary>
    /// STRM 媒体流相关共享服务：扫描、读取媒体流、探测远程媒体并写入数据库。
    /// 音频标签与歌词的具体实现分别位于 <see cref="AudioMetadataHelper"/> 与 <see cref="LocalLyricLocator"/>，
    /// 路径工具位于 <see cref="StrmPathHelper"/>；本类对外保留稳定的公共静态外观。
    /// </summary>
    public class StrmMediaInfoService
    {
        // STRM 文件扩展名常量
        public const string StrmFileExtension = ".strm";

        /// <summary>
        /// 检查路径是否是 strm 文件
        /// </summary>
        public static bool IsStrmFile(string path)
        {
            return !string.IsNullOrWhiteSpace(path) &&
                   path.EndsWith(StrmFileExtension, StringComparison.OrdinalIgnoreCase);
        }

        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IMediaEncoder _mediaEncoder;
        private readonly IMediaStreamRepository _mediaStreamRepository;

        public StrmMediaInfoService(
            ILibraryManager libraryManager,
            IMediaEncoder mediaEncoder,
            IMediaStreamRepository mediaStreamRepository,
            // 保留该形参以兼容现有调用方与测试签名；当前实现不再使用
            IItemRepository itemRepository,
            ILogger logger)
        {
            _libraryManager = libraryManager;
            _mediaEncoder = mediaEncoder;
            _mediaStreamRepository = mediaStreamRepository;
            _logger = logger;
        }

        /// <summary>
        /// 获取库中所有的 strm 文件（去重）
        /// </summary>
        /// <param name="cancellationToken">取消令牌（库查询本身不支持取消，在查询前后检查）</param>
        /// <returns>所有 strm 文件对应的库条目列表，永不为 null</returns>
        public List<BaseItem> GetAllStrmItems(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // 通过库查询直接取候选条目（strm 入库后即 Movie/Episode/Video/Audio），再按扩展名内存过滤；
            // 相比递归磁盘枚举，避免了全目录遍历、权限目录处理以及逐文件 FindByPath 的数据库往返
            var query = new InternalItemsQuery
            {
                Recursive = true,
                IsVirtualItem = false,
                IncludeItemTypes = new[]
                {
                    BaseItemKind.Movie,
                    BaseItemKind.Episode,
                    BaseItemKind.Video,
                    BaseItemKind.MusicVideo,
                    BaseItemKind.Audio
                }
            };

            try
            {
                var items = _libraryManager.GetItemList(query);
                cancellationToken.ThrowIfCancellationRequested();
                return items
                    .Where(i => i != null && !string.IsNullOrWhiteSpace(i.Path) && IsStrmFile(i.Path))
                    .GroupBy(i => i.Path, StrmPathHelper.PathComparer)
                    .Select(g => g.First())
                    .ToList();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error querying library for strm items");
                return new List<BaseItem>();
            }
        }

        /// <summary>
        /// 获取库条目的媒体流信息
        /// </summary>
        /// <param name="item">库条目对象</param>
        /// <returns>媒体流列表，若无法获取则返回空列表</returns>
        public List<MediaStream> GetItemMediaStreams(BaseItem item)
        {
            try
            {
                // 使用 IHasMediaSources 接口获取媒体流，避免反射
                if (item is IHasMediaSources hasMediaSources)
                {
                    var streams = hasMediaSources.GetMediaStreams();
                    if (streams != null)
                    {
                        return streams.ToList();
                    }
                }

                return new List<MediaStream>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting media streams for {ItemType}", item.GetType().Name);
                return new List<MediaStream>();
            }
        }

        /// <summary>
        /// 保存库条目的媒体流信息
        /// </summary>
        /// <param name="item">库条目</param>
        /// <param name="mediaStreams">要保存的媒体流列表</param>
        /// <param name="cancellationToken">取消令牌</param>
        public async Task<List<MediaStream>> SaveMediaStreamsAsync(
            BaseItem item, List<MediaStream> mediaStreams, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var streams = MergeExternalStreams(mediaStreams, GetItemMediaStreams(item));
            if (item is Audio audio)
            {
                LocalLyricLocator.ApplyAudioLyrics(audio, streams, _logger);
            }

            _mediaStreamRepository.SaveMediaStreams(item.Id, streams, cancellationToken);
            if (item is Video video)
            {
                video.DefaultVideoStreamIndex = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video)?.Index;
                video.HasSubtitles = streams.Any(s => s.Type == MediaStreamType.Subtitle);
            }

            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataImport, cancellationToken).ConfigureAwait(false);
            if (item is Audio savedAudio)
            {
                await SyncAudioRelationshipsAsync(savedAudio, cancellationToken).ConfigureAwait(false);
            }

            return streams;
        }

        private static bool IsExternalOrLyricStream(MediaStream stream)
        {
            return stream.IsExternal ||
                   (stream.Type == MediaStreamType.Lyric && !string.IsNullOrWhiteSpace(stream.Path));
        }

        // Repository writes replace the entire stream set. Keep discovered external streams,
        // preferring the current library entry over an older cache entry for the same path.
        // Clone first: assigning external indices must not mutate shared library/cache objects.
        private static List<MediaStream> MergeExternalStreams(
            IEnumerable<MediaStream> incoming, IEnumerable<MediaStream> existing)
        {
            var incomingList = incoming.ToList();
            var external = existing.Where(IsExternalOrLyricStream)
                .Concat(incomingList.Where(IsExternalOrLyricStream));
            var selected = incomingList.Where(s => !IsExternalOrLyricStream(s)).ToList();
            var seenPaths = new HashSet<(MediaStreamType, string)>();
            foreach (var stream in external)
            {
                // Pathless streams cannot be safely deduplicated by filename.
                var path = stream.Path;
                if (path != null && OperatingSystem.IsWindows() && !path.Contains("://"))
                    path = path.ToUpperInvariant();
                if (path == null || seenPaths.Add((stream.Type, path)))
                    selected.Add(stream);
            }

            var result = JsonSerializer.Deserialize<List<MediaStream>>(JsonSerializer.Serialize(selected));
            int nextIndex = result.Where(s => !IsExternalOrLyricStream(s)).Select(s => s.Index).DefaultIfEmpty(-1).Max() + 1;
            foreach (var stream in result.Where(IsExternalOrLyricStream))
                stream.Index = nextIndex++;
            return result;
        }

        private static void ApplyProbeMetadata(BaseItem item, MediaProbeResult result)
        {
            item.Size = result.Size > 0 ? result.Size : null;
            item.RunTimeTicks = result.RunTimeTicks;
            item.Container = result.Container;
            item.Width = result.Width;
            item.Height = result.Height;
            item.TotalBitrate = result.TotalBitrate > 0 ? result.TotalBitrate : null;
            if (!string.IsNullOrWhiteSpace(result.TargetPath) && StrmPathHelper.GetProtocolFromPath(result.TargetPath) != MediaProtocol.File)
            {
                item.IsShortcut = true;
                item.ShortcutPath = result.TargetPath;
            }

            if (item is Audio audio)
            {
                AudioMetadataHelper.ApplyProbeMetadata(audio, result);
            }
        }

        /// <summary>
        /// 探测媒体流并返回完整结果（包含元数据）
        /// </summary>
        public async Task<MediaProbeResult> ProbeMediaStreamsAsync(BaseItem item, CancellationToken cancellationToken)
        {
            var fileName = Path.GetFileNameWithoutExtension(item.Path);
            var result = new MediaProbeResult();

            try
            {
                var strmContentHash = MediaInfoCache.GetStrmContentHash(item.Path);
                if (strmContentHash == null)
                {
                    _logger.LogWarning("STRM file unreadable or empty: {Path}; skipping probe", item.Path);
                    return result;
                }

                var strmContent = ReadStrmTargetPath(item.Path, _logger);
                if (!string.Equals(strmContentHash, MediaInfoCache.GetStrmContentHash(item.Path), StringComparison.Ordinal))
                {
                    _logger.LogWarning("STRM content changed while reading {Path}; skipping probe", item.Path);
                    return result;
                }

                if (string.IsNullOrWhiteSpace(strmContent))
                {
                    _logger.LogWarning("STRM file is empty: {Path}", item.Path);
                    return result;
                }

                var isAudio = item.MediaType == MediaType.Audio;
                var mediaInfo = await _mediaEncoder.GetMediaInfo(
                    new MediaInfoRequest
                    {
                        MediaSource = new MediaSourceInfo
                        {
                            Path = strmContent,
                            Protocol = StrmPathHelper.GetProtocolFromPath(strmContent),
                        },
                        MediaType = isAudio ? DlnaProfileType.Audio : DlnaProfileType.Video,
                        ExtractChapters = false,
                    },
                    cancellationToken).ConfigureAwait(false);

                if (mediaInfo?.MediaStreams != null && mediaInfo.MediaStreams.Count > 0)
                {
                    if (!string.Equals(strmContentHash, MediaInfoCache.GetStrmContentHash(item.Path), StringComparison.Ordinal))
                    {
                        _logger.LogWarning("STRM content changed during probing {Path}; discarding result", item.Path);
                        return result;
                    }

                    // 填充返回结果（包含需要的元数据，供缓存使用）
                    result.MediaStreams = mediaInfo.MediaStreams.ToList();
                    result.Size = mediaInfo.Size.GetValueOrDefault();
                    result.RunTimeTicks = mediaInfo.RunTimeTicks;
                    result.Container = mediaInfo.Container;
                    result.StrmContentHash = strmContentHash;
                    result.TargetPath = strmContent;
                    result.TotalBitrate = (int)Math.Min(int.MaxValue, mediaInfo.Bitrate.GetValueOrDefault());

                    var highestVideoStream = GetHighestResolutionVideoStream(result.MediaStreams);
                    if (highestVideoStream != null)
                    {
                        result.Width = highestVideoStream.Width.GetValueOrDefault();
                        result.Height = highestVideoStream.Height.GetValueOrDefault();
                    }

                    if (item is Audio audioItem)
                    {
                        AudioMetadataHelper.PopulateProbeResult(audioItem, mediaInfo, result, _libraryManager, _logger);
                    }

                    // A fresh probe is authoritative even when cache reads/writes are disabled.
                    ApplyProbeMetadata(item, result);
                    result.MediaStreams = await SaveMediaStreamsAsync(item, result.MediaStreams, cancellationToken).ConfigureAwait(false);

                    _logger.LogDebug("Successfully saved {Count} media streams and metadata for {Name}",
                        mediaInfo.MediaStreams.Count, fileName);
                    return result;
                }

                _logger.LogDebug("No media streams found for {Name}", fileName);
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 取消不属于错误：向上传播，由调用方统一处理
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error probing STRM content for {Name}", fileName);
                return new MediaProbeResult();
            }
        }

        /// <summary>
        /// 从媒体流列表中获取最高分辨率的视频流
        /// </summary>
        public static MediaStream GetHighestResolutionVideoStream(IEnumerable<MediaStream> streams)
        {
            if (streams == null)
            {
                return null;
            }

            return streams
                .Where(s => s.Type == MediaStreamType.Video && s.Width.HasValue && s.Height.HasValue)
                .OrderByDescending(s => (long)s.Width.Value * s.Height.Value)
                .FirstOrDefault();
        }

        /// <summary>
        /// 判断缓存元数据是否需要恢复（尺寸被重置，或时长/容器/分辨率/码率/歌词标志/Shortcut/音频标签缺失）
        /// </summary>
        public static bool NeedsRestore(BaseItem item, MediaInfoCacheData cacheData, ILibraryManager libraryManager = null)
        {
            return IsSizeReset(item, cacheData)
                || (cacheData.RunTimeTicks.HasValue && !item.RunTimeTicks.HasValue)
                || (!string.IsNullOrEmpty(cacheData.Container) && string.IsNullOrEmpty(item.Container))
                || (cacheData.Width > 0 && item.Width <= 0)
                || (cacheData.Height > 0 && item.Height <= 0)
                || (cacheData.TotalBitrate > 0 && item.TotalBitrate.GetValueOrDefault() <= 0)
                || LocalLyricLocator.NeedsLyricMetadataRestore(item, cacheData)
                || AudioMetadataHelper.NeedsAudioMetadataRestore(item, cacheData, libraryManager)
                || NeedsShortcutRestore(item);
        }

        /// <summary>
        /// 判断条目尺寸是否被重置为 strm 文件本身大小（不足缓存值的 1/10）
        /// </summary>
        public static bool IsSizeReset(BaseItem item, MediaInfoCacheData cacheData)
        {
            return cacheData.Size > 0 && item.Size.GetValueOrDefault() < cacheData.Size / 10;
        }

        private static bool NeedsShortcutRestore(BaseItem item)
        {
            if (item == null || !IsStrmFile(item.Path))
            {
                return false;
            }

            if (item.IsShortcut && !string.IsNullOrWhiteSpace(item.ShortcutPath))
            {
                return false;
            }

            var targetPath = ReadStrmTargetPath(item.Path);
            return !string.IsNullOrWhiteSpace(targetPath) &&
                   StrmPathHelper.GetProtocolFromPath(targetPath) != MediaProtocol.File;
        }

        /// <summary>
        /// 从缓存恢复条目的元数据字段（仅恢复仍缺失的字段），返回是否有修改。
        /// 不执行写库，由调用方决定持久化方式。
        /// </summary>
        public static bool TryRestoreMetadataFromCache(BaseItem item, MediaInfoCacheData cacheData, ILibraryManager libraryManager = null)
        {
            bool changed = false;

            if (IsSizeReset(item, cacheData))
            {
                item.Size = cacheData.Size;
                changed = true;
            }

            if (cacheData.RunTimeTicks.HasValue && !item.RunTimeTicks.HasValue)
            {
                item.RunTimeTicks = cacheData.RunTimeTicks;
                changed = true;
            }

            if (!string.IsNullOrEmpty(cacheData.Container) && string.IsNullOrEmpty(item.Container))
            {
                item.Container = cacheData.Container;
                changed = true;
            }

            if (cacheData.Width > 0 && item.Width <= 0)
            {
                item.Width = cacheData.Width;
                changed = true;
            }

            if (cacheData.Height > 0 && item.Height <= 0)
            {
                item.Height = cacheData.Height;
                changed = true;
            }

            if (cacheData.TotalBitrate > 0 && item.TotalBitrate.GetValueOrDefault() <= 0)
            {
                item.TotalBitrate = cacheData.TotalBitrate;
                changed = true;
            }

            if (LocalLyricLocator.TryRestoreLyricMetadataFromCache(item, cacheData))
            {
                changed = true;
            }

            if (AudioMetadataHelper.TryRestoreAudioMetadataFromCache(item, cacheData, libraryManager))
            {
                changed = true;
            }

            if (item != null && IsStrmFile(item.Path) && (!item.IsShortcut || string.IsNullOrWhiteSpace(item.ShortcutPath)))
            {
                var targetPath = ReadStrmTargetPath(item.Path);
                if (!string.IsNullOrWhiteSpace(targetPath) && StrmPathHelper.GetProtocolFromPath(targetPath) != MediaProtocol.File)
                {
                    item.IsShortcut = true;
                    item.ShortcutPath = targetPath;
                    changed = true;
                }
            }

            return changed;
        }

        public static bool HasMissingAudioMetadata(
            BaseItem item, ILibraryManager libraryManager = null, bool checkParentAlbum = true)
        {
            return AudioMetadataHelper.HasMissingAudioMetadata(item, libraryManager, checkParentAlbum);
        }

        public Task<bool> SyncAudioRelationshipsAsync(BaseItem item, CancellationToken cancellationToken)
        {
            return SyncAudioRelationshipsStaticAsync(item, _libraryManager, cancellationToken, _logger);
        }

        public static Task<bool> SyncAudioRelationshipsStaticAsync(
            BaseItem item, ILibraryManager libraryManager, CancellationToken cancellationToken, ILogger logger = null)
        {
            if (item is Audio audio)
            {
                return AudioMetadataHelper.SyncAudioRelationshipsAsync(audio, libraryManager, cancellationToken, logger);
            }

            return Task.FromResult(false);
        }

        /// <summary>
        /// 读取 strm 文件的目标地址（薄外观，实际实现见 <see cref="StrmPathHelper"/>）。
        /// </summary>
        internal static string ReadStrmTargetPath(string strmFilePath, ILogger logger = null)
        {
            return StrmPathHelper.ReadStrmTargetPath(strmFilePath, logger);
        }

        public static bool HasMissingLocalLyrics(BaseItem item, IReadOnlyList<MediaStream> currentStreams)
        {
            return LocalLyricLocator.HasMissingLocalLyrics(item, currentStreams);
        }
    }
}
