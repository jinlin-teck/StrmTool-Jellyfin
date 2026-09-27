using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Tasks;
using MediaBrowser.Model.Entities;

namespace StrmTool
{
    public class ExtractTask : IScheduledTask, IDisposable
    {
        protected volatile bool _disposed = false;
        protected readonly ILogger _logger;
        protected readonly StrmMediaInfoService _mediaInfoService;
        protected readonly MediaInfoCache _mediaCache;
        protected volatile PluginConfiguration _config;
        protected readonly SemaphoreSlim _semaphore;

        private readonly ILibraryManager _libraryManager;
        private LibraryScanListener _scanListener;
        private ItemUpdateListener _updateListener;
        private readonly AutoExtractQueue _autoExtractQueue;
        private readonly object _eventLock = new object();

        public ExtractTask(
            ILibraryManager libraryManager,
            IMediaEncoder mediaEncoder,
            IMediaStreamRepository mediaStreamRepository,
            IItemRepository itemRepository,
            ILogger<ExtractTask> logger)
        {
            _logger = logger;
            _mediaInfoService = new StrmMediaInfoService(libraryManager, mediaEncoder, mediaStreamRepository, itemRepository, logger);
            _mediaCache = new MediaInfoCache(logger);

            if (Plugin.Instance == null)
            {
                _logger.LogWarning("Plugin instance not found, using default configuration");
            }

            _config = PluginConfigurationProvider.GetCurrent();

            _semaphore = new SemaphoreSlim(_config.MaxConcurrentExtract);

            _libraryManager = libraryManager;
            try
            {
                _scanListener = new LibraryScanListener(_libraryManager, _logger, _config);
                _scanListener.StrmFileDetected += OnStrmFileDetected;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize library scan listener");
            }

            try
            {
                _updateListener = new ItemUpdateListener(_libraryManager, _logger, _config, _mediaCache);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize item update listener");
            }

            // 自动提取队列：事件线程仅入队，worker 数量固定为 MaxConcurrentExtract（修改需重启生效）
            _autoExtractQueue = new AutoExtractQueue(_config.MaxConcurrentExtract, HandleAutoExtractItemAsync, _logger);
        }

        public string Category => "StrmTool";
        public string Key => "StrmToolTask";
        public string Description => Plugin.Instance?.GetLocalizedString("StrmTool.TaskDescription") ?? "Extract media technical information (codec, resolution, subtitles) from strm files";
        public string Name => Plugin.Instance?.GetLocalizedString("StrmTool.TaskName") ?? "Extract Strm Media Info";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        protected void RefreshConfig()
        {
            // 注意：MaxConcurrentExtract 需要 Jellyfin 重启后生效
            // 避免在运行时替换 semaphore 导致并发控制失效
            _config = PluginConfigurationProvider.GetCurrent(_config);
        }

        /// <summary>
        /// 处理 strm 文件项的通用方法（委托给 <see cref="StrmLibraryTaskBase.RunItemsAsync"/> 统一骨架；
        /// 使用共享 <see cref="_semaphore"/>，与自动提取后台队列共用并发预算）。
        /// </summary>
        protected Task<int> ProcessStrmItemsAsync(
            List<BaseItem> items,
            Func<BaseItem, CancellationToken, Task<bool>> processItemAsync,
            IProgress<double> progress,
            CancellationToken cancellationToken)
        {
            return StrmLibraryTaskBase.RunItemsAsync(
                items, _semaphore, _config.MaxConcurrentExtract, processItemAsync, progress, _logger, cancellationToken);
        }

        /// <summary>
        /// 尝试从缓存加载媒体流，如果成功则直接保存
        /// </summary>
        /// <param name="item">库条目</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>是否成功从缓存加载</returns>
        private async Task<bool> TryLoadFromCacheAsync(BaseItem item, CancellationToken cancellationToken)
        {
            if (!_config.EnableMediaInfoCache || _config.ForceRefreshIgnoreCache)
            {
                return false;
            }

            if (!_mediaCache.TryGetFullCache(item.Path, out var cacheData) ||
                cacheData.MediaStreams == null ||
                cacheData.MediaStreams.Count == 0)
            {
                return false;
            }

            // 旧版缓存尚未探测音频内嵌标签：若当前音频仍缺少专辑/艺术家信息，走一次探测以补齐标签并升级缓存
            if (cacheData.AudioTagsProbed != true &&
                StrmMediaInfoService.HasMissingAudioMetadata(item, _libraryManager))
            {
                return false;
            }

            try
            {
                StrmMediaInfoService.TryRestoreMetadataFromCache(item, cacheData, _libraryManager);
                await _mediaInfoService.SaveMediaStreamsAsync(item, cacheData.MediaStreams, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("{Name}: Used cached media info ({Count} streams)",
                    item.Name, cacheData.MediaStreams.Count);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{Name}: Failed to save cached media streams", item.Name);
                return false;
            }
        }

        private bool NeedsAudioMetadataRefresh(BaseItem item, bool checkParentAlbum = false)
        {
            if (!StrmMediaInfoService.HasMissingAudioMetadata(item, _libraryManager, checkParentAlbum))
            {
                return false;
            }

            if (!_config.EnableMediaInfoCache || _config.ForceRefreshIgnoreCache)
            {
                return true;
            }

            if (!_mediaCache.TryGetFullCache(item.Path, out var cacheData))
            {
                return true;
            }

            return cacheData.AudioTagsProbed != true ||
                   StrmMediaInfoService.NeedsRestore(item, cacheData, checkParentAlbum ? _libraryManager : null);
        }

        private bool HasInvalidCache(BaseItem item)
        {
            return _config.EnableMediaInfoCache &&
                   !_config.ForceRefreshIgnoreCache &&
                   _mediaCache.HasCacheFile(item.Path) &&
                   !_mediaCache.TryGetCachedMediaStreams(item.Path, out _);
        }

        /// <summary>
        /// 探测媒体流并保存到缓存
        /// </summary>
        /// <param name="item">库条目</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>探测结果</returns>
        private async Task<MediaProbeResult> ProbeAndCacheAsync(BaseItem item, CancellationToken cancellationToken)
        {
            var probeResult = await _mediaInfoService.ProbeMediaStreamsAsync(item, cancellationToken).ConfigureAwait(false);

            if (_config.EnableMediaInfoCache && probeResult.Success)
            {
                await _mediaCache.SaveFullCacheAsync(
                    item.Path,
                    probeResult.MediaStreams,
                    MediaInfoCacheData.FromProbeResult(probeResult),
                    expectedStrmContentHash: probeResult.StrmContentHash,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            return probeResult;
        }

        private void OnStrmFileDetected(object sender, BaseItem item)
        {
            if (_disposed)
                return;

            RefreshConfig();

            var fileName = Path.GetFileNameWithoutExtension(item.Path);

            if (!_config.EnableAutoExtract)
            {
                _logger.LogDebug("Auto-extract is disabled, skipping {Name}", fileName);
                return;
            }

            // 不在事件线程读取媒体流/缓存；强制刷新和缓存失效交由后台统一判断。
            _autoExtractQueue.TryEnqueue(item.Id, fileName);
        }

        /// <summary>
        /// 自动提取队列的单条处理：按配置延迟后执行提取（含超时保护）。
        /// </summary>
        private async Task HandleAutoExtractItemAsync(Guid itemId, CancellationToken cancellationToken)
        {
            RefreshConfig();
            if (!_config.EnableAutoExtract)
                return;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMinutes(_config.MetadataRestoreTimeoutMinutes));
            await Task.Delay(_config.RefreshDelayMs, cts.Token).ConfigureAwait(false);

            if (!_disposed)
            {
                var item = _libraryManager.GetItemById(itemId);
                if (item == null)
                {
                    _logger.LogWarning("Auto-extract item {ItemId} no longer exists", itemId);
                    return;
                }

                await ExtractSingleItemAsync(item, cts.Token).ConfigureAwait(false);
            }
        }

        public void CleanupListener()
        {
            lock (_eventLock)
            {
                if (_scanListener != null)
                {
                    _scanListener.StrmFileDetected -= OnStrmFileDetected;
                    _scanListener.Dispose();
                    _scanListener = null;
                }

                if (_updateListener != null)
                {
                    _updateListener.Dispose();
                    _updateListener = null;
                }
            }
        }

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            RefreshConfig();
            _logger.LogInformation("Starting strm file scan...");

            try
            {
                var allStrmItems = _mediaInfoService.GetAllStrmItems(cancellationToken);
                int totalFound = allStrmItems.Count;

                // 过滤阶段含缓存校验（磁盘 I/O），并行执行以缩短大库扫描的等待时间
                var strmItems = StrmLibraryTaskBase.FilterItemsInParallel(allStrmItems, item =>
                {
                    try
                    {
                        var mediaStreams = _mediaInfoService.GetItemMediaStreams(item);
                        bool hasVideo = mediaStreams.Any(s => s.Type == MediaStreamType.Video);
                        bool hasAudio = mediaStreams.Any(s => s.Type == MediaStreamType.Audio);

                        return _config.ForceRefreshIgnoreExisting || !(hasVideo || hasAudio) || HasInvalidCache(item) || StrmMediaInfoService.HasMissingLocalLyrics(item, mediaStreams) || NeedsAudioMetadataRefresh(item);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error checking media streams for {Name}", item.Name);
                        return true;
                    }
                }, cancellationToken);

                _logger.LogInformation("Found {Count} strm files in library, {NeedRefresh} need media info",
                    totalFound, strmItems.Count);

                if (strmItems.Count == 0)
                {
                    progress.Report(100);
                    _logger.LogInformation("Nothing to process, task complete.");
                    return;
                }

                await ProcessStrmFiles(strmItems, progress, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("STRM file scan cancelled");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fatal error during strm file scan");
                throw;
            }
        }

        private async Task ProcessStrmFiles(List<BaseItem> strmItems, IProgress<double> progress, CancellationToken cancellationToken)
        {
            int succeeded = await ProcessStrmItemsAsync(strmItems, ProcessSingleItemAsync, progress, cancellationToken);

            _logger.LogInformation("Task complete. {Succeeded}/{Total} strm files now have media info.",
                succeeded, strmItems.Count);
        }

        /// <summary>
        /// 核心处理逻辑：从缓存加载或探测媒体流
        /// </summary>
        private async Task<(List<MediaStream> before, List<MediaStream> after, bool probed)> ProcessItemCoreAsync(
            BaseItem item,
            string logPrefix,
            CancellationToken cancellationToken)
        {
            var beforeStreams = _mediaInfoService.GetItemMediaStreams(item);
            _logger.LogDebug("{Prefix} - Before: {Count} streams", logPrefix, beforeStreams.Count);

            // 首先尝试从缓存加载
            bool loadedFromCache = await TryLoadFromCacheAsync(item, cancellationToken).ConfigureAwait(false);

            if (!loadedFromCache)
            {
                // 缓存未命中，执行探测并保存到缓存
                await ProbeAndCacheAsync(item, cancellationToken).ConfigureAwait(false);
            }

            // 新探测结果已直接持久化；仅缓存命中时补回缺失元数据。
            if (loadedFromCache)
                await RestoreMetadataFromCacheAsync(item, logPrefix, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            var afterStreams = _mediaInfoService.GetItemMediaStreams(item);
            return (beforeStreams, afterStreams, !loadedFromCache);
        }

        /// <summary>
        /// 缓存命中后从缓存回写仍缺失的 Size 等元数据并持久化。
        /// 与 ItemUpdateListener 的恢复逻辑一致：仅在字段缺失或被重置时修改，
        /// UpdateToRepositoryAsync 触发的 ItemUpdated 事件会被监听器判定为无需恢复，不会循环。
        /// </summary>
        private async Task RestoreMetadataFromCacheAsync(BaseItem item, string logPrefix, CancellationToken cancellationToken)
        {
            if (!_config.EnableMediaInfoCache || _config.ForceRefreshIgnoreCache)
            {
                return;
            }

            if (!_mediaCache.TryGetFullCache(item.Path, out var cacheData) ||
                !StrmMediaInfoService.NeedsRestore(item, cacheData, _libraryManager))
            {
                return;
            }

            if (StrmMediaInfoService.TryRestoreMetadataFromCache(item, cacheData, _libraryManager))
            {
                await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataImport, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("{Prefix} - Restored metadata for {Name} (Size={Size})", logPrefix, item.Name, cacheData.Size);
            }

            await _mediaInfoService.SyncAudioRelationshipsAsync(item, cancellationToken).ConfigureAwait(false);
        }

        private async Task<bool> ProcessSingleItemAsync(BaseItem item, CancellationToken cancellationToken)
        {
            _logger.LogDebug("Processing {Name}", item.Name);

            var (beforeStreams, afterStreams, probed) = await ProcessItemCoreAsync(
                item,
                "StrmTool",
                cancellationToken).ConfigureAwait(false);

            // 仅在实际发起远程探测后按 RefreshDelayMs 限速（缓存命中不延迟），
            // 避免并发 worker 对远程媒体服务器造成瞬时探测风暴
            if (probed && _config.RefreshDelayMs > 0)
            {
                await Task.Delay(_config.RefreshDelayMs, cancellationToken).ConfigureAwait(false);
            }

            bool hasVideo = afterStreams.Any(s => s.Type == MediaStreamType.Video);
            bool hasAudio = afterStreams.Any(s => s.Type == MediaStreamType.Audio);

            _logger.LogInformation(
                "{Name}: Probe done. Streams {Before}→{After}. Video:{Video}, Audio:{Audio}",
                item.Name,
                beforeStreams.Count,
                afterStreams.Count,
                hasVideo,
                hasAudio
            );

            if (!(hasVideo || hasAudio))
            {
                _logger.LogWarning("{Name} may still lack media stream info", item.Name);
            }

            return hasVideo || hasAudio;
        }

        public async Task ExtractSingleItemAsync(BaseItem item, CancellationToken cancellationToken)
        {
            RefreshConfig();
            var fileName = Path.GetFileNameWithoutExtension(item.Path);

            await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _logger.LogDebug("Auto-extracting media info for new strm file: {Name}", fileName);

                var beforeStreams = _mediaInfoService.GetItemMediaStreams(item);
                _logger.LogDebug("Before: {Count} streams", beforeStreams.Count);

                bool hasVideo = beforeStreams.Any(s => s.Type == MediaStreamType.Video);
                bool hasAudio = beforeStreams.Any(s => s.Type == MediaStreamType.Audio);

                if (!_config.ForceRefreshIgnoreExisting && (hasVideo || hasAudio) && !HasInvalidCache(item) && !StrmMediaInfoService.HasMissingLocalLyrics(item, beforeStreams) && !NeedsAudioMetadataRefresh(item, checkParentAlbum: true))
                {
                    _logger.LogInformation("{Name} already has media stream info, skipping", fileName);
                    return;
                }

                // 使用提取的通用方法处理缓存和探测
                var (_, afterStreams, _) = await ProcessItemCoreAsync(
                    item,
                    "Auto-extract",
                    cancellationToken).ConfigureAwait(false);

                _logger.LogInformation(
                    "Auto-extract complete for {Name}. Streams {Before}→{After}",
                    fileName,
                    beforeStreams.Count,
                    afterStreams.Count
                );
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in auto-extract for {Name}", fileName);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            _disposed = true;

            if (disposing)
            {
                // 监听器清理（内部最长等待 30s）与队列 worker 退出并行等待，避免串行阻塞
                var listenerCleanup = Task.Run(CleanupListener);
                _autoExtractQueue.Dispose();

                try
                {
                    listenerCleanup.Wait();
                }
                catch (AggregateException ex)
                {
                    _logger.LogWarning(ex, "Error cleaning up listeners");
                }
            }
        }
    }
}
