using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Entities;
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
        /// <param name="itemId">库条目ID</param>
        /// <param name="mediaStreams">要保存的媒体流列表</param>
        /// <param name="cancellationToken">取消令牌</param>
        public void SaveMediaStreams(Guid itemId, List<MediaStream> mediaStreams, CancellationToken cancellationToken)
        {
            _mediaStreamRepository.SaveMediaStreams(itemId, mediaStreams, cancellationToken);
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

                    // 保存媒体流信息（不保存 Item 元数据，避免与 Jellyfin 的元数据重置产生竞态条件）
                    // Item 元数据会在 ItemUpdateListener 中从缓存恢复
                    _mediaStreamRepository.SaveMediaStreams(item.Id, mediaInfo.MediaStreams, cancellationToken);

                    // 填充返回结果（包含需要的元数据，供缓存使用）
                    result.MediaStreams = mediaInfo.MediaStreams.ToList();
                    result.Size = mediaInfo.Size.GetValueOrDefault();
                    result.RunTimeTicks = mediaInfo.RunTimeTicks;
                    result.Container = mediaInfo.Container;
                    result.StrmContentHash = strmContentHash;
                    result.TotalBitrate = (int)Math.Min(int.MaxValue, mediaInfo.Bitrate.GetValueOrDefault());

                    var highestVideoStream = GetHighestResolutionVideoStream(result.MediaStreams);
                    if (highestVideoStream != null)
                    {
                        result.Width = highestVideoStream.Width.GetValueOrDefault();
                        result.Height = highestVideoStream.Height.GetValueOrDefault();
                    }

                    _logger.LogDebug("Successfully saved {Count} media streams for {Name} (item metadata will be restored later via cache)",
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
                return result;
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
        /// 判断缓存元数据是否需要恢复（尺寸被重置，或时长/容器/分辨率/码率缺失）
        /// </summary>
        public static bool NeedsRestore(BaseItem item, MediaInfoCacheData cacheData)
        {
            return IsSizeReset(item, cacheData)
                || (cacheData.RunTimeTicks.HasValue && !item.RunTimeTicks.HasValue)
                || (!string.IsNullOrEmpty(cacheData.Container) && string.IsNullOrEmpty(item.Container))
                || (cacheData.Width > 0 && item.Width <= 0)
                || (cacheData.Height > 0 && item.Height <= 0)
                || (cacheData.TotalBitrate > 0 && item.TotalBitrate.GetValueOrDefault() <= 0);
        }

        /// <summary>
        /// 判断条目尺寸是否被重置为 strm 文件本身大小（不足缓存值的 1/10）
        /// </summary>
        public static bool IsSizeReset(BaseItem item, MediaInfoCacheData cacheData)
        {
            return cacheData.Size > 0 && item.Size.GetValueOrDefault() < cacheData.Size / 10;
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
                            _logger.LogWarning("Potential path traversal attack detected in strm file: {Path}", strmFilePath);
                            return string.Empty;
                        }
                        return sourcePath;
                    }
                }

                return string.Empty;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                _logger.LogWarning(ex, "Failed to read strm file: {Path}", strmFilePath);
                return string.Empty;
            }
        }


    }
}
