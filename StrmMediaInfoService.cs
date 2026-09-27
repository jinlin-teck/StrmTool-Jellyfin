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
        /// 判断缓存元数据是否需要恢复（尺寸被重置，或时长/容器/分辨率/码率/歌词标志/Shortcut缺失）
        /// </summary>
        public static bool NeedsRestore(BaseItem item, MediaInfoCacheData cacheData)
        {
            return IsSizeReset(item, cacheData)
                || (cacheData.RunTimeTicks.HasValue && !item.RunTimeTicks.HasValue)
                || (!string.IsNullOrEmpty(cacheData.Container) && string.IsNullOrEmpty(item.Container))
                || (cacheData.Width > 0 && item.Width <= 0)
                || (cacheData.Height > 0 && item.Height <= 0)
                || (cacheData.TotalBitrate > 0 && item.TotalBitrate.GetValueOrDefault() <= 0)
                || LocalLyricLocator.NeedsLyricMetadataRestore(item, cacheData)
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
        public static bool TryRestoreMetadataFromCache(BaseItem item, MediaInfoCacheData cacheData)
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
}
