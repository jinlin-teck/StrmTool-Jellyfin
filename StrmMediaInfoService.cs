using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    /// 媒体探测结果，包含流信息和元数据
    /// </summary>
    public class MediaProbeResult
    {
        public List<MediaStream> MediaStreams { get; set; } = new List<MediaStream>();
        public long Size { get; set; }
        public long? RunTimeTicks { get; set; }
        public string Container { get; set; }
        public string StrmContentHash { get; set; }
        public string TargetPath { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int TotalBitrate { get; set; }
        public bool? AudioTagsProbed { get; set; }
        public string Title { get; set; }
        public string Album { get; set; }
        public List<string> Artists { get; set; }
        public List<string> AlbumArtists { get; set; }
        public int? TrackNumber { get; set; }
        public int? DiscNumber { get; set; }
        public int? ProductionYear { get; set; }
        public List<string> Genres { get; set; }
        public bool Success => MediaStreams != null && MediaStreams.Count > 0;
    }

    /// <summary>
    /// STRM 媒体流相关共享服务：扫描、读取媒体流、探测远程媒体并写入数据库。
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
        private readonly IItemRepository _itemRepository;

        public StrmMediaInfoService(
            ILibraryManager libraryManager,
            IMediaEncoder mediaEncoder,
            IMediaStreamRepository mediaStreamRepository,
            IItemRepository itemRepository,
            ILogger logger)
        {
            _libraryManager = libraryManager;
            _mediaEncoder = mediaEncoder;
            _mediaStreamRepository = mediaStreamRepository;
            _itemRepository = itemRepository;
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
                    .GroupBy(i => i.Path, OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase)
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
            if (!string.IsNullOrWhiteSpace(result.TargetPath) && GetProtocolFromPath(result.TargetPath) != MediaProtocol.File)
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

                var strmContent = ReadStrmSourcePath(item.Path);
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
                            Protocol = GetProtocolFromPath(strmContent),
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
                   GetProtocolFromPath(targetPath) != MediaProtocol.File;
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
                if (!string.IsNullOrWhiteSpace(targetPath) && GetProtocolFromPath(targetPath) != MediaProtocol.File)
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

        private static MediaProtocol GetProtocolFromPath(string path)
        {
            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return MediaProtocol.Http;
            }

            if (path.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase))
            {
                return MediaProtocol.Rtmp;
            }

            if (path.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
            {
                return MediaProtocol.Rtsp;
            }

            if (path.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase))
            {
                return MediaProtocol.Ftp;
            }

            return MediaProtocol.File;
        }

        private string ReadStrmSourcePath(string strmFilePath)
        {
            return ReadStrmTargetPath(strmFilePath, _logger);
        }

        internal static string ReadStrmTargetPath(string strmFilePath, ILogger logger = null)
        {
            if (string.IsNullOrWhiteSpace(strmFilePath) || !File.Exists(strmFilePath))
            {
                return string.Empty;
            }

            try
            {
                foreach (var line in File.ReadLines(strmFilePath))
                {
                    var sourcePath = line.Trim();
                    if (!string.IsNullOrWhiteSpace(sourcePath))
                    {
                        // 安全验证：检查路径遍历攻击
                        if (MediaInfoCache.ContainsPathTraversal(sourcePath))
                        {
                            logger?.LogWarning("Potential path traversal attack detected in strm file: {Path}", strmFilePath);
                            return string.Empty;
                        }
                        return sourcePath;
                    }
                }

                return string.Empty;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                logger?.LogWarning(ex, "Failed to read strm file: {Path}", strmFilePath);
                return string.Empty;
            }
        }

        public static bool HasMissingLocalLyrics(BaseItem item, IReadOnlyList<MediaStream> currentStreams)
        {
            return LocalLyricLocator.HasMissingLocalLyrics(item, currentStreams);
        }
    }

    /// <summary>
    /// 本地同目录外挂歌词定位与 Audio 歌词元数据同步辅助类。
    /// </summary>
    internal static class LocalLyricLocator
    {
        // 对齐 Jellyfin 核心 Emby.Naming.Common.NamingOptions.LyricFileExtensions，按优先级顺序排列。
        private static readonly string[] LyricFileExtensions = new[] { ".lrc", ".elrc", ".txt" };

        public static bool HasMissingLocalLyrics(BaseItem item, IReadOnlyList<MediaStream> currentStreams, ILogger logger = null)
        {
            if (!(item is Audio))
            {
                return false;
            }

            if (currentStreams != null && currentStreams.Any(s => s.Type == MediaStreamType.Lyric))
            {
                return false;
            }

            return FindLocalLyricFiles(item.Path, logger).Count > 0;
        }

        public static void ApplyAudioLyrics(Audio audio, List<MediaStream> streams, ILogger logger = null)
        {
            if (audio == null || streams == null)
            {
                return;
            }

            // 若音频条目在本地磁盘存在，剔除已不存在的陈旧本地歌词流路径（保留远程歌词或无路径内嵌歌词）
            if (!string.IsNullOrWhiteSpace(audio.Path) && File.Exists(audio.Path))
            {
                streams.RemoveAll(s =>
                    s != null &&
                    s.Type == MediaStreamType.Lyric &&
                    s.IsExternalUrl != true &&
                    !string.IsNullOrWhiteSpace(s.Path) &&
                    Path.IsPathRooted(s.Path) &&
                    !File.Exists(s.Path));
            }

            var localLyrics = FindLocalLyricFiles(audio.Path, logger);
            if (!streams.Any(s => s.Type == MediaStreamType.Lyric) && localLyrics.Count > 0)
            {
                int nextIndex = streams.Select(s => s.Index).DefaultIfEmpty(-1).Max() + 1;
                streams.Add(new MediaStream
                {
                    Type = MediaStreamType.Lyric,
                    Path = localLyrics[0],
                    Index = nextIndex
                });
            }

            var lyricPaths = streams
                .Where(s => s.Type == MediaStreamType.Lyric && !string.IsNullOrWhiteSpace(s.Path))
                .Select(s => s.Path)
                .Concat(localLyrics)
                .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                .ToArray();

            bool hasLyricStream = streams.Any(s => s.Type == MediaStreamType.Lyric);
            if (lyricPaths.Length > 0)
            {
                audio.LyricFiles = lyricPaths;
                audio.HasLyrics = true;
            }
            else if (audio.LyricFiles != null && audio.LyricFiles.Count > 0)
            {
                // 原本记录的本地歌词文件已被移除时同步清空
                audio.LyricFiles = Array.Empty<string>();
                audio.HasLyrics = hasLyricStream;
            }
            else if (hasLyricStream)
            {
                audio.HasLyrics = true;
            }
        }

        public static bool NeedsLyricMetadataRestore(BaseItem item, MediaInfoCacheData cacheData)
        {
            if (!(item is Audio audio) || cacheData?.MediaStreams == null)
            {
                return false;
            }

            var cachedLyrics = GetExistingCachedLyricPaths(cacheData.MediaStreams);
            bool hasCachedLyricStream = cachedLyrics.Length > 0 ||
                cacheData.MediaStreams.Any(s => s != null && s.Type == MediaStreamType.Lyric && string.IsNullOrWhiteSpace(s.Path));
            if (!hasCachedLyricStream)
            {
                return false;
            }

            if (audio.HasLyrics != true)
            {
                return true;
            }

            return cachedLyrics.Length > 0 && (audio.LyricFiles == null || audio.LyricFiles.Count == 0);
        }

        public static bool TryRestoreLyricMetadataFromCache(BaseItem item, MediaInfoCacheData cacheData)
        {
            if (!NeedsLyricMetadataRestore(item, cacheData) || !(item is Audio audio))
            {
                return false;
            }

            var cachedLyrics = GetExistingCachedLyricPaths(cacheData.MediaStreams);
            bool changed = false;

            if (audio.HasLyrics != true)
            {
                audio.HasLyrics = true;
                changed = true;
            }

            if (cachedLyrics.Length > 0 && (audio.LyricFiles == null || audio.LyricFiles.Count == 0))
            {
                audio.LyricFiles = cachedLyrics;
                changed = true;
            }

            return changed;
        }

        private static string[] GetExistingCachedLyricPaths(IEnumerable<MediaStream> streams)
        {
            return streams
                .Where(s => s != null &&
                            s.Type == MediaStreamType.Lyric &&
                            !string.IsNullOrWhiteSpace(s.Path) &&
                            (s.IsExternalUrl == true || !Path.IsPathRooted(s.Path) || File.Exists(s.Path)))
                .Select(s => s.Path)
                .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                .ToArray();
        }

        internal static List<string> FindLocalLyricFiles(string strmPath, ILogger logger = null)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(strmPath) || !Path.IsPathRooted(strmPath))
            {
                return result;
            }

            try
            {
                var directory = Path.GetDirectoryName(strmPath);
                var baseName = Path.GetFileNameWithoutExtension(strmPath);
                if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(baseName) || !Directory.Exists(directory))
                {
                    return result;
                }

                foreach (var ext in LyricFileExtensions)
                {
                    var candidate = Path.Combine(directory, baseName + ext);
                    if (File.Exists(candidate))
                    {
                        result.Add(candidate);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                logger?.LogDebug(ex, "Failed to check local lyric files for: {Path}", strmPath);
            }

            return result;
        }
    }

    /// <summary>
    /// STRM 音频标签提取、标准目录结构（专辑艺术家/专辑/歌曲）回退、父级专辑同步与缓存恢复辅助类。
    /// </summary>
    internal static class AudioMetadataHelper
    {
        private static readonly char[] ArtistSplitDelimiters = new[] { '/', ';', '|', '\\', '、' };
        private static readonly Regex LeadingTrackRegex = new(
            @"^(?:(?<disc>\d{1,2})\s*-\s*)?(?<track>\d{1,3})(?:\s*[-._]\s*|\s+)(?<rest>.+)$",
            RegexOptions.Compiled);
        private static readonly Regex MultiDiscFolderRegex = new(
            @"^(?:cd|disc|disk)\s*(?<disc>\d{1,2})$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly SemaphoreSlim[] AlbumLocks = Enumerable.Range(0, 32)
            .Select(_ => new SemaphoreSlim(1, 1)).ToArray();

        private static SemaphoreSlim GetAlbumLock(Guid albumId)
        {
            return AlbumLocks[(int)((uint)albumId.GetHashCode() % (uint)AlbumLocks.Length)];
        }

        private sealed class AudioFallbackMetadata
        {
            public string Title { get; set; }
            public string Album { get; set; }
            public List<string> Artists { get; set; }
            public List<string> AlbumArtists { get; set; }
            public int? TrackNumber { get; set; }
            public int? DiscNumber { get; set; }
            public int? ProductionYear { get; set; }
            public List<string> Genres { get; set; }
        }

        public static bool HasMissingAudioMetadata(
            BaseItem item, ILibraryManager libraryManager = null, bool checkParentAlbum = true, ILogger logger = null)
        {
            if (!(item is Audio audio))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(audio.Album) ||
                audio.Artists == null || audio.Artists.Count == 0 ||
                audio.AlbumArtists == null || audio.AlbumArtists.Count == 0)
            {
                return true;
            }

            if (!checkParentAlbum)
            {
                return false;
            }

            var album = FindParent<MusicAlbum>(audio, libraryManager, logger);
            if (album != null && !album.IsLocked)
            {
                if (album.AlbumArtists == null || album.AlbumArtists.Count == 0 ||
                    album.Artists == null || album.Artists.Count == 0)
                {
                    return true;
                }
            }

            return false;
        }

        public static void PopulateProbeResult(
            Audio audio, MediaInfo mediaInfo, MediaProbeResult result, ILibraryManager libraryManager, ILogger logger = null)
        {
            if (audio == null || result == null)
            {
                return;
            }

            if (mediaInfo != null)
            {
                result.AudioTagsProbed = true;
                result.Title = NormalizeString(mediaInfo.Name);
                result.Album = NormalizeString(mediaInfo.Album);
                result.Artists = NormalizeList(mediaInfo.Artists);
                result.AlbumArtists = NormalizeList(mediaInfo.AlbumArtists);
                result.TrackNumber = mediaInfo.IndexNumber > 0 ? mediaInfo.IndexNumber : null;
                result.DiscNumber = mediaInfo.ParentIndexNumber > 0 ? mediaInfo.ParentIndexNumber : null;
                int? year = mediaInfo.ProductionYear ?? mediaInfo.PremiereDate?.Year;
                result.ProductionYear = year is > 0 and <= 9999 ? year : null;
                result.Genres = NormalizeList(mediaInfo.Genres);
            }

            var fallback = ResolveFallbackMetadata(audio, libraryManager, logger);

            if (string.IsNullOrWhiteSpace(result.Album))
            {
                result.Album = fallback.Album;
            }

            if (result.AlbumArtists == null || result.AlbumArtists.Count == 0)
            {
                result.AlbumArtists = fallback.AlbumArtists is { Count: > 0 }
                    ? fallback.AlbumArtists
                    : result.Artists;
            }

            if (result.Artists == null || result.Artists.Count == 0)
            {
                result.Artists = fallback.Artists is { Count: > 0 }
                    ? fallback.Artists
                    : result.AlbumArtists;
            }

            if (string.IsNullOrWhiteSpace(result.Title))
            {
                result.Title = fallback.Title;
            }

            if (!result.TrackNumber.HasValue)
            {
                result.TrackNumber = fallback.TrackNumber;
            }

            if (!result.DiscNumber.HasValue)
            {
                result.DiscNumber = fallback.DiscNumber;
            }
        }

        public static void ApplyProbeMetadata(Audio audio, MediaProbeResult result)
        {
            if (audio == null || result == null)
            {
                return;
            }

            ApplyEffectiveAudioMetadata(
                audio,
                new AudioFallbackMetadata
                {
                    Title = result.Title,
                    Album = result.Album,
                    Artists = result.Artists,
                    AlbumArtists = result.AlbumArtists,
                    TrackNumber = result.TrackNumber,
                    DiscNumber = result.DiscNumber,
                    ProductionYear = result.ProductionYear,
                    Genres = result.Genres
                });
        }

        public static bool NeedsAudioMetadataRestore(
            BaseItem item, MediaInfoCacheData cacheData, ILibraryManager libraryManager = null)
        {
            if (!(item is Audio audio) || cacheData == null)
            {
                return false;
            }

            var effective = GetEffectiveCacheMetadata(audio, cacheData, libraryManager);
            if (NeedsAudioItemRestore(audio, effective))
            {
                return true;
            }

            return NeedsParentAlbumSync(audio, effective, libraryManager);
        }

        public static bool TryRestoreAudioMetadataFromCache(
            BaseItem item, MediaInfoCacheData cacheData, ILibraryManager libraryManager = null)
        {
            if (!(item is Audio audio) || cacheData == null)
            {
                return false;
            }

            var effective = GetEffectiveCacheMetadata(audio, cacheData, libraryManager);
            return ApplyEffectiveAudioMetadata(audio, effective);
        }

        /// <summary>
        /// 结合缓存数据与目录/父级实体兜底生成有效音频元数据。
        /// 设计约定：无论 AudioTagsProbed 是否为 true，当缓存或远端内嵌标签中缺失专辑/艺术家时，
        /// 始终回退使用标准目录结构（专辑艺术家/专辑/歌曲）或父级实体补齐，保持与探测路径一致。
        /// </summary>
        private static AudioFallbackMetadata GetEffectiveCacheMetadata(
            Audio audio, MediaInfoCacheData cacheData, ILibraryManager libraryManager, ILogger logger = null)
        {
            var fallback = ResolveFallbackMetadata(audio, libraryManager, logger);

            var album = NormalizeString(cacheData.Album) ?? fallback.Album;
            var cachedArtists = NormalizeList(cacheData.Artists);
            var cachedAlbumArtists = NormalizeList(cacheData.AlbumArtists);

            var albumArtists = cachedAlbumArtists is { Count: > 0 }
                ? cachedAlbumArtists
                : (fallback.AlbumArtists is { Count: > 0 } ? fallback.AlbumArtists : cachedArtists);

            var artists = cachedArtists is { Count: > 0 }
                ? cachedArtists
                : (fallback.Artists is { Count: > 0 } ? fallback.Artists : albumArtists);

            return new AudioFallbackMetadata
            {
                Title = NormalizeString(cacheData.Title) ?? fallback.Title,
                Album = album,
                Artists = artists,
                AlbumArtists = albumArtists,
                TrackNumber = cacheData.TrackNumber ?? fallback.TrackNumber,
                DiscNumber = cacheData.DiscNumber ?? fallback.DiscNumber,
                ProductionYear = cacheData.ProductionYear,
                Genres = NormalizeList(cacheData.Genres)
            };
        }

        private static bool NeedsAudioItemRestore(Audio audio, AudioFallbackMetadata effective)
        {
            if (audio == null || effective == null || audio.IsLocked)
            {
                return false;
            }

            if (ShouldUpdateAudioTitle(audio, effective.Title))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(effective.Album) && string.IsNullOrWhiteSpace(audio.Album))
            {
                return true;
            }

            if (effective.Artists is { Count: > 0 } && (audio.Artists == null || audio.Artists.Count == 0))
            {
                return true;
            }

            if (effective.AlbumArtists is { Count: > 0 } && (audio.AlbumArtists == null || audio.AlbumArtists.Count == 0))
            {
                return true;
            }

            if (effective.TrackNumber.HasValue && !audio.IndexNumber.HasValue)
            {
                return true;
            }

            if (effective.DiscNumber.HasValue && !audio.ParentIndexNumber.HasValue)
            {
                return true;
            }

            if (effective.ProductionYear.HasValue && !audio.ProductionYear.HasValue)
            {
                return true;
            }

            if (!IsFieldLocked(audio, MetadataField.Genres) &&
                effective.Genres is { Count: > 0 } &&
                (audio.Genres == null || audio.Genres.Length == 0 || audio.Genres.All(string.IsNullOrWhiteSpace)))
            {
                return true;
            }

            return false;
        }

        private static bool ApplyEffectiveAudioMetadata(Audio audio, AudioFallbackMetadata effective)
        {
            if (audio == null || effective == null || audio.IsLocked)
            {
                return false;
            }

            bool changed = false;

            if (ShouldUpdateAudioTitle(audio, effective.Title))
            {
                audio.Name = effective.Title;
                changed = true;
            }

            if (!string.IsNullOrWhiteSpace(effective.Album) && string.IsNullOrWhiteSpace(audio.Album))
            {
                audio.Album = effective.Album;
                changed = true;
            }

            if (effective.Artists is { Count: > 0 } && (audio.Artists == null || audio.Artists.Count == 0))
            {
                audio.Artists = effective.Artists;
                changed = true;
            }

            if (effective.AlbumArtists is { Count: > 0 } && (audio.AlbumArtists == null || audio.AlbumArtists.Count == 0))
            {
                audio.AlbumArtists = effective.AlbumArtists;
                changed = true;
            }

            if (effective.TrackNumber.HasValue && !audio.IndexNumber.HasValue)
            {
                audio.IndexNumber = effective.TrackNumber;
                changed = true;
            }

            if (effective.DiscNumber.HasValue && !audio.ParentIndexNumber.HasValue)
            {
                audio.ParentIndexNumber = effective.DiscNumber;
                changed = true;
            }

            if (effective.ProductionYear is > 0 and <= 9999)
            {
                int year = effective.ProductionYear.Value;
                if (!audio.ProductionYear.HasValue)
                {
                    audio.ProductionYear = year;
                    changed = true;
                }

                if (!audio.PremiereDate.HasValue)
                {
                    audio.PremiereDate = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                    changed = true;
                }
            }

            if (!IsFieldLocked(audio, MetadataField.Genres) &&
                effective.Genres is { Count: > 0 } &&
                (audio.Genres == null || audio.Genres.Length == 0 || audio.Genres.All(string.IsNullOrWhiteSpace)))
            {
                audio.Genres = effective.Genres.ToArray();
                changed = true;
            }

            return changed;
        }

        private static bool ShouldUpdateAudioTitle(Audio audio, string candidateTitle)
        {
            if (audio == null || string.IsNullOrWhiteSpace(candidateTitle) || IsFieldLocked(audio, MetadataField.Name))
            {
                return false;
            }

            if (string.Equals(audio.Name, candidateTitle, StringComparison.Ordinal))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(audio.Name))
            {
                return true;
            }

            var fileNameWithoutExt = !string.IsNullOrWhiteSpace(audio.Path)
                ? Path.GetFileNameWithoutExtension(audio.Path)
                : null;
            return !string.IsNullOrWhiteSpace(fileNameWithoutExt) &&
                   string.Equals(audio.Name, fileNameWithoutExt, StringComparison.Ordinal);
        }

        private static bool IsFieldLocked(BaseItem item, MetadataField field)
        {
            return item?.LockedFields != null && item.LockedFields.Contains(field);
        }

        private static bool NeedsParentAlbumSync(
            Audio audio, AudioFallbackMetadata effective, ILibraryManager libraryManager)
        {
            if (audio == null || libraryManager == null)
            {
                return false;
            }

            var album = FindParent<MusicAlbum>(audio, libraryManager);
            if (album == null || album.IsLocked)
            {
                return false;
            }

            var desiredAlbumArtists = NormalizeList(audio.AlbumArtists) ?? effective?.AlbumArtists;
            if (desiredAlbumArtists is { Count: > 0 } &&
                (album.AlbumArtists == null || desiredAlbumArtists.Any(a => !album.AlbumArtists.Contains(a, StringComparer.OrdinalIgnoreCase))))
            {
                return true;
            }

            var desiredArtists = NormalizeList(audio.Artists) ?? effective?.Artists;
            if (desiredArtists is { Count: > 0 } &&
                (album.Artists == null || desiredArtists.Any(a => !album.Artists.Contains(a, StringComparer.OrdinalIgnoreCase))))
            {
                return true;
            }

            return false;
        }

        public static async Task<bool> SyncAudioRelationshipsAsync(
            Audio audio, ILibraryManager libraryManager, CancellationToken cancellationToken, ILogger logger = null)
        {
            if (audio == null || libraryManager == null)
            {
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();

            // 1. 同步单曲的 People（保留非 Artist/AlbumArtist 角色，仅在新增时写库）
            bool audioPeopleChanged = UpdateItemPeople(audio, audio.AlbumArtists, audio.Artists, libraryManager, logger);

            // 2. 确保所有关联的艺术家实体在库中已激活
            EnsureArtistEntitiesExist(audio.AlbumArtists, audio.Artists, libraryManager, logger);

            // 3. 同步父级 MusicAlbum 的艺术家、年份、流派与 People
            var album = FindParent<MusicAlbum>(audio, libraryManager, logger);
            if (album == null || album.IsLocked)
            {
                return audioPeopleChanged;
            }

            var gate = GetAlbumLock(album.Id);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var currentAlbum = libraryManager.GetItemById(album.Id) as MusicAlbum ?? album;
                if (currentAlbum.IsLocked)
                {
                    return audioPeopleChanged;
                }

                bool albumChanged = false;

                var parentArtist = FindParent<MusicArtist>(currentAlbum, libraryManager, logger);
                var incomingAlbumArtists = NormalizeList(audio.AlbumArtists);
                if ((incomingAlbumArtists == null || incomingAlbumArtists.Count == 0) &&
                    !string.IsNullOrWhiteSpace(parentArtist?.Name))
                {
                    incomingAlbumArtists = new List<string> { parentArtist.Name.Trim() };
                }

                if (incomingAlbumArtists is { Count: > 0 })
                {
                    var mergedAlbumArtists = (currentAlbum.AlbumArtists ?? Array.Empty<string>())
                        .Concat(incomingAlbumArtists)
                        .Where(a => !string.IsNullOrWhiteSpace(a))
                        .Select(a => a.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    if (currentAlbum.AlbumArtists == null ||
                        !currentAlbum.AlbumArtists.SequenceEqual(mergedAlbumArtists, StringComparer.Ordinal))
                    {
                        currentAlbum.AlbumArtists = mergedAlbumArtists;
                        albumChanged = true;
                    }
                }

                var incomingArtists = NormalizeList(audio.Artists) ?? incomingAlbumArtists;
                if (incomingArtists is { Count: > 0 })
                {
                    var mergedArtists = (currentAlbum.Artists ?? Array.Empty<string>())
                        .Concat(incomingArtists)
                        .Where(a => !string.IsNullOrWhiteSpace(a))
                        .Select(a => a.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    if (currentAlbum.Artists == null ||
                        !currentAlbum.Artists.SequenceEqual(mergedArtists, StringComparer.Ordinal))
                    {
                        currentAlbum.Artists = mergedArtists;
                        albumChanged = true;
                    }
                }

                if (!currentAlbum.ProductionYear.HasValue && audio.ProductionYear.HasValue)
                {
                    currentAlbum.ProductionYear = audio.ProductionYear;
                    albumChanged = true;
                }

                if (!currentAlbum.PremiereDate.HasValue && audio.PremiereDate.HasValue)
                {
                    currentAlbum.PremiereDate = audio.PremiereDate;
                    albumChanged = true;
                }

                if (!IsFieldLocked(currentAlbum, MetadataField.Genres) &&
                    (currentAlbum.Genres == null || currentAlbum.Genres.Length == 0) &&
                    audio.Genres != null && audio.Genres.Length > 0)
                {
                    currentAlbum.Genres = audio.Genres
                        .Where(g => !string.IsNullOrWhiteSpace(g))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    albumChanged = true;
                }

                if (albumChanged)
                {
                    UpdateItemPeople(currentAlbum, currentAlbum.AlbumArtists, currentAlbum.Artists, libraryManager, logger);
                    await currentAlbum.UpdateToRepositoryAsync(ItemUpdateType.MetadataImport, cancellationToken).ConfigureAwait(false);
                    logger?.LogInformation("Synced album artists ({AlbumArtists}) for album {AlbumName}",
                        string.Join(", ", currentAlbum.AlbumArtists ?? Array.Empty<string>()), currentAlbum.Name);
                }

                return audioPeopleChanged || albumChanged;
            }
            finally
            {
                gate.Release();
            }
        }

        private static bool UpdateItemPeople(
            BaseItem item,
            IReadOnlyList<string> albumArtists,
            IReadOnlyList<string> artists,
            ILibraryManager libraryManager,
            ILogger logger)
        {
            if (item == null || libraryManager == null || !item.SupportsPeople || IsFieldLocked(item, MetadataField.Cast))
            {
                return false;
            }

            var incoming = new List<PersonInfo>();
            if (albumArtists != null)
            {
                foreach (var albumArtist in albumArtists)
                {
                    if (!string.IsNullOrWhiteSpace(albumArtist))
                    {
                        PeopleHelper.AddPerson(incoming, new PersonInfo
                        {
                            Name = albumArtist.Trim(),
                            Type = PersonKind.AlbumArtist
                        });
                    }
                }
            }

            if (artists != null)
            {
                foreach (var artist in artists)
                {
                    if (!string.IsNullOrWhiteSpace(artist))
                    {
                        PeopleHelper.AddPerson(incoming, new PersonInfo
                        {
                            Name = artist.Trim(),
                            Type = PersonKind.Artist
                        });
                    }
                }
            }

            if (incoming.Count == 0)
            {
                return false;
            }

            // UpdatePeople 为全量替换语义：先读取并保留已有人员（如 Composer、Lyricist、Performer 等），
            // 且仅在确实缺少目标 Artist/AlbumArtist 时才调用 UpdatePeople。
            IReadOnlyList<PersonInfo> existing = null;
            try
            {
                existing = libraryManager.GetPeople(item);
            }
            catch (Exception ex) when (ex is NotSupportedException || ex is NotImplementedException || ex is NullReferenceException)
            {
                logger?.LogDebug(ex, "GetPeople not available for {Name}; proceeding with incoming people", item.Name);
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "Failed to read existing people for {Name}", item.Name);
            }

            var merged = new List<PersonInfo>();
            if (existing != null)
            {
                foreach (var person in existing)
                {
                    if (person != null && !string.IsNullOrWhiteSpace(person.Name))
                    {
                        PeopleHelper.AddPerson(merged, person);
                    }
                }
            }

            int beforeCount = merged.Count;
            foreach (var person in incoming)
            {
                PeopleHelper.AddPerson(merged, person);
            }

            if (existing != null && merged.Count == beforeCount)
            {
                return false;
            }

            try
            {
                libraryManager.UpdatePeople(item, merged);
                return true;
            }
            catch (Exception ex) when (ex is NotSupportedException || ex is NotImplementedException || ex is NullReferenceException)
            {
                logger?.LogDebug(ex, "Skipping UpdatePeople for {Name} in non-full library environment", item.Name);
                return false;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to update people for {Name}", item.Name);
                return false;
            }
        }

        private static void EnsureArtistEntitiesExist(
            IReadOnlyList<string> albumArtists,
            IReadOnlyList<string> artists,
            ILibraryManager libraryManager,
            ILogger logger)
        {
            if (libraryManager == null)
            {
                return;
            }

            var allNames = (albumArtists ?? Array.Empty<string>())
                .Concat(artists ?? Array.Empty<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var name in allNames)
            {
                try
                {
                    _ = libraryManager.GetArtist(name);
                }
                catch (Exception ex) when (ex is NotSupportedException || ex is NotImplementedException || ex is NullReferenceException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger?.LogDebug(ex, "Failed to resolve artist entity for {ArtistName}", name);
                }
            }
        }

        private static AudioFallbackMetadata ResolveFallbackMetadata(
            Audio audio, ILibraryManager libraryManager, ILogger logger = null)
        {
            var result = new AudioFallbackMetadata();
            if (audio == null)
            {
                return result;
            }

            // 1. 从文件名解析候选音轨号、碟片号、单曲歌手、歌名（如 "03 - 卓依婷 - 婉君.strm" 或 "卓依婷 - 婉君.strm"）
            var fileNameWithoutExt = !string.IsNullOrWhiteSpace(audio.Path)
                ? Path.GetFileNameWithoutExtension(audio.Path)
                : null;
            ParseTrackFileName(fileNameWithoutExt, out var parsedDisc, out var parsedTrack, out var parsedArtists, out var parsedTitle);

            result.DiscNumber = parsedDisc;
            result.TrackNumber = parsedTrack;
            result.Title = parsedTitle;
            result.Artists = parsedArtists;

            // 2. 优先从 Jellyfin 库实体层级（MusicArtist / MusicAlbum）获取标准专辑与专辑艺术家
            var parentAlbum = FindParent<MusicAlbum>(audio, libraryManager, logger);
            var parentArtist = FindParent<MusicArtist>(audio, libraryManager, logger);

            if (parentAlbum != null && !string.IsNullOrWhiteSpace(parentAlbum.Name))
            {
                result.Album = parentAlbum.Name.Trim();
            }

            if (parentArtist != null && !string.IsNullOrWhiteSpace(parentArtist.Name))
            {
                result.AlbumArtists = new List<string> { parentArtist.Name.Trim() };
            }
            else if (parentAlbum?.AlbumArtists is { Count: > 0 })
            {
                result.AlbumArtists = NormalizeList(parentAlbum.AlbumArtists);
            }

            // 3. 若脱离库实体上下文（如离线缓存恢复），且路径与文件名符合 "专辑艺术家/专辑/歌手 - 歌名.strm" 结构时从路径回退
            if ((string.IsNullOrWhiteSpace(result.Album) || result.AlbumArtists == null || result.AlbumArtists.Count == 0) &&
                !string.IsNullOrWhiteSpace(audio.Path) && Path.IsPathRooted(audio.Path))
            {
                ExtractFolderHierarchyFallback(
                    audio.Path,
                    parsedArtists,
                    out var pathAlbumArtist,
                    out var pathAlbum,
                    out var pathDiscNumber,
                    logger);

                if (string.IsNullOrWhiteSpace(result.Album) && !string.IsNullOrWhiteSpace(pathAlbum))
                {
                    result.Album = pathAlbum;
                }

                if ((result.AlbumArtists == null || result.AlbumArtists.Count == 0) && !string.IsNullOrWhiteSpace(pathAlbumArtist))
                {
                    result.AlbumArtists = new List<string> { pathAlbumArtist };
                }

                if (!result.DiscNumber.HasValue && pathDiscNumber.HasValue)
                {
                    result.DiscNumber = pathDiscNumber;
                }
            }

            // 4. Artists 与 AlbumArtists 互为兜底
            if ((result.Artists == null || result.Artists.Count == 0) && result.AlbumArtists is { Count: > 0 })
            {
                result.Artists = new List<string>(result.AlbumArtists);
            }
            else if ((result.AlbumArtists == null || result.AlbumArtists.Count == 0) && result.Artists is { Count: > 0 })
            {
                result.AlbumArtists = new List<string>(result.Artists);
            }

            return result;
        }

        private static void ParseTrackFileName(
            string fileNameWithoutExt,
            out int? discNumber,
            out int? trackNumber,
            out List<string> artists,
            out string title)
        {
            discNumber = null;
            trackNumber = null;
            artists = null;
            title = null;

            if (string.IsNullOrWhiteSpace(fileNameWithoutExt))
            {
                return;
            }

            string working = fileNameWithoutExt.Trim();
            var match = LeadingTrackRegex.Match(working);
            if (match.Success)
            {
                if (match.Groups["disc"].Success &&
                    int.TryParse(match.Groups["disc"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int disc) &&
                    disc > 0)
                {
                    discNumber = disc;
                }

                if (int.TryParse(match.Groups["track"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int track) &&
                    track > 0)
                {
                    trackNumber = track;
                    working = match.Groups["rest"].Value.Trim();
                }
            }

            int separatorIndex = working.IndexOf(" - ", StringComparison.Ordinal);
            if (separatorIndex > 0)
            {
                var artistPart = working.Substring(0, separatorIndex).Trim();
                var titlePart = working.Substring(separatorIndex + 3).Trim();
                if (!string.IsNullOrWhiteSpace(artistPart) && !string.IsNullOrWhiteSpace(titlePart))
                {
                    artists = SplitArtists(artistPart);
                    title = titlePart;
                    return;
                }
            }

            if (trackNumber.HasValue && !string.IsNullOrWhiteSpace(working))
            {
                title = working;
            }
        }

        private static void ExtractFolderHierarchyFallback(
            string strmPath,
            List<string> parsedFileArtists,
            out string albumArtist,
            out string album,
            out int? discNumber,
            ILogger logger = null)
        {
            albumArtist = null;
            album = null;
            discNumber = null;

            try
            {
                var dir = Path.GetDirectoryName(strmPath);
                if (string.IsNullOrWhiteSpace(dir))
                {
                    return;
                }

                var currentDirName = Path.GetFileName(dir);
                var parentDir = Path.GetDirectoryName(dir);

                var discMatch = !string.IsNullOrWhiteSpace(currentDirName)
                    ? MultiDiscFolderRegex.Match(currentDirName.Trim())
                    : Match.Empty;
                if (discMatch.Success && !string.IsNullOrWhiteSpace(parentDir))
                {
                    if (int.TryParse(discMatch.Groups["disc"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int disc) &&
                        disc > 0)
                    {
                        discNumber = disc;
                    }

                    dir = parentDir;
                    currentDirName = Path.GetFileName(dir);
                    parentDir = Path.GetDirectoryName(dir);
                }

                var grandParentDirName = !string.IsNullOrWhiteSpace(parentDir)
                    ? Path.GetFileName(parentDir)
                    : null;

                if (string.IsNullOrWhiteSpace(currentDirName) || string.IsNullOrWhiteSpace(grandParentDirName))
                {
                    return;
                }

                // 当文件名中的歌手与祖父目录名匹配，或祖父目录名出现在文件名歌手列表中时，确认符合 "专辑艺术家/专辑/歌曲" 标准层级
                bool matchesFileArtist = parsedFileArtists != null &&
                    parsedFileArtists.Any(a => string.Equals(a, grandParentDirName.Trim(), StringComparison.OrdinalIgnoreCase));

                if (matchesFileArtist)
                {
                    albumArtist = grandParentDirName.Trim();
                    album = currentDirName.Trim();
                }
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "Failed to extract folder hierarchy fallback from path: {Path}", strmPath);
            }
        }

        internal static T FindParent<T>(BaseItem item, ILibraryManager libraryManager, ILogger logger = null) where T : Folder
        {
            if (item == null)
            {
                return null;
            }

            var manager = libraryManager ?? BaseItem.LibraryManager;
            if (manager == null)
            {
                return null;
            }

            var visited = new HashSet<Guid>();
            var currentId = item.ParentId;
            while (currentId != Guid.Empty && visited.Add(currentId))
            {
                BaseItem parent;
                try
                {
                    parent = manager.GetItemById(currentId);
                }
                catch (Exception ex)
                {
                    logger?.LogDebug(ex, "Failed to resolve parent {ParentId} for item {ItemId}", currentId, item.Id);
                    return null;
                }

                if (parent == null)
                {
                    return null;
                }

                if (parent is T matched)
                {
                    return matched;
                }

                currentId = parent.ParentId;
            }

            return null;
        }

        private static string NormalizeString(string value)
        {
            return MediaInfoCache.NormalizeText(value);
        }

        private static List<string> NormalizeList(IEnumerable<string> values)
        {
            return MediaInfoCache.NormalizeStringList(values, ArtistSplitDelimiters);
        }

        private static List<string> SplitArtists(string raw)
        {
            return string.IsNullOrWhiteSpace(raw)
                ? new List<string>()
                : MediaInfoCache.NormalizeStringList(new[] { raw }, ArtistSplitDelimiters) ?? new List<string>();
        }
    }
}
