using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
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
        private readonly CancellationTokenSource _backgroundTaskCts = new CancellationTokenSource();
        private readonly Channel<Guid> _autoExtractQueue = Channel.CreateUnbounded<Guid>();
        private readonly ConcurrentDictionary<Guid, byte> _queuedAutoExtractItems = new ConcurrentDictionary<Guid, byte>();
        private readonly Task[] _autoExtractWorkers;
        private readonly object _eventLock = new object();

        // 待处理自动提取上限：防止扫描风暴时无界排队（超出后由计划任务兜底）
        private const int MaxPendingAutoExtractItems = 100;

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
                _config = new PluginConfiguration();
            }
            else
            {
                _config = Plugin.Instance.Configuration;
            }

            _semaphore = new SemaphoreSlim(_config.MaxConcurrentExtract);

            _libraryManager = libraryManager;
            try
            {
                _scanListener = new LibraryScanListener(_libraryManager, _logger, _config, _mediaCache);
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

            _autoExtractWorkers = Enumerable.Range(0, _config.MaxConcurrentExtract)
                .Select(_ => Task.Run(ProcessAutoExtractQueueAsync))
                .ToArray();
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
            if (Plugin.Instance != null)
            {
                // 注意：MaxConcurrentExtract 需要 Jellyfin 重启后生效
                // 避免在运行时替换 semaphore 导致并发控制失效
                _config = Plugin.Instance.Configuration;
            }
        }

        /// <summary>
        /// 处理 strm 文件项的通用方法
        /// </summary>
        protected async Task<int> ProcessStrmItemsAsync(
            List<BaseItem> items,
            Func<BaseItem, CancellationToken, Task<bool>> processItemAsync,
            IProgress<double> progress,
            CancellationToken cancellationToken)
        {
            int processed = 0;
            int succeeded = 0;
            int total = items.Count;
            var progressLock = new object();

            int nextIndex = -1;
            async Task WorkerAsync()
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int index = Interlocked.Increment(ref nextIndex);
                    if (index >= total)
                    {
                        return;
                    }

                    var item = items[index];
                    await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        bool hasMediaInfo = await processItemAsync(item, cancellationToken).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (hasMediaInfo)
                        {
                            Interlocked.Increment(ref succeeded);
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing {Name} ({Path})", item.Name, item.Path);
                    }
                    finally
                    {
                        _semaphore.Release();
                        lock (progressLock)
                        {
                            processed++;
                            progress.Report(Math.Min((double)processed / total * 100, 100));
                        }
                    }
                }
            }

            var tasks = Enumerable.Range(0, Math.Min(total, _config.MaxConcurrentExtract))
                .Select(_ => Task.Run(WorkerAsync, cancellationToken));
            await Task.WhenAll(tasks).ConfigureAwait(false);
            progress.Report(100);
            return succeeded;
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

            if (!_mediaCache.TryGetCachedMediaStreams(item.Path, out var cachedStreams))
            {
                return false;
            }

            try
            {
                await _mediaInfoService.SaveMediaStreamsAsync(item, cachedStreams, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("{Name}: Used cached media info ({Count} streams)",
                    item.Name, cachedStreams.Count);
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
                    probeResult.Size,
                    probeResult.RunTimeTicks,
                    probeResult.Container,
                    expectedStrmContentHash: probeResult.StrmContentHash,
                    width: probeResult.Width,
                    height: probeResult.Height,
                    totalBitrate: probeResult.TotalBitrate,
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
            if (_queuedAutoExtractItems.Count >= MaxPendingAutoExtractItems)
            {
                _logger.LogWarning("Auto-extract queue is full ({Max}), skipping {Name}. It will be processed by the scheduled task.",
                    MaxPendingAutoExtractItems, fileName);
                return;
            }

            if (!_queuedAutoExtractItems.TryAdd(item.Id, 0))
            {
                return;
            }

            if (!_autoExtractQueue.Writer.TryWrite(item.Id))
            {
                _queuedAutoExtractItems.TryRemove(item.Id, out _);
            }
        }

        private async Task ProcessAutoExtractQueueAsync()
        {
            try
            {
                await foreach (var itemId in _autoExtractQueue.Reader.ReadAllAsync(_backgroundTaskCts.Token))
                {
                    try
                    {
                        RefreshConfig();
                        if (!_config.EnableAutoExtract)
                            continue;

                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_backgroundTaskCts.Token);
                        cts.CancelAfter(TimeSpan.FromMinutes(_config.MetadataRestoreTimeoutMinutes));
                        await Task.Delay(_config.RefreshDelayMs, cts.Token).ConfigureAwait(false);

                        if (!_disposed)
                        {
                            var item = _libraryManager.GetItemById(itemId);
                            if (item == null)
                            {
                                _logger.LogWarning("Auto-extract item {ItemId} no longer exists", itemId);
                                continue;
                            }

                            await ExtractSingleItemAsync(item, cts.Token).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogDebug("Extraction cancelled for item {ItemId}", itemId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error in auto-extract background task for item {ItemId}", itemId);
                    }
                    finally
                    {
                        _queuedAutoExtractItems.TryRemove(itemId, out _);
                    }
                }
            }
            catch (OperationCanceledException) when (_backgroundTaskCts.IsCancellationRequested)
            {
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
                var strmItems = new List<BaseItem>();
                var filterLock = new object();
                Parallel.ForEach(
                    allStrmItems,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = Math.Max(2, Math.Min(Environment.ProcessorCount, 8)),
                        CancellationToken = cancellationToken
                    },
                    () => new List<BaseItem>(),
                    (item, _, local) =>
                    {
                        try
                        {
                            var mediaStreams = _mediaInfoService.GetItemMediaStreams(item);
                            bool hasVideo = mediaStreams.Any(s => s.Type == MediaStreamType.Video);
                            bool hasAudio = mediaStreams.Any(s => s.Type == MediaStreamType.Audio);

                            if (_config.ForceRefreshIgnoreExisting || !(hasVideo || hasAudio) || HasInvalidCache(item))
                            {
                                local.Add(item);
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error checking media streams for {Name}", item.Name);
                            local.Add(item);
                        }

                        return local;
                    },
                    local =>
                    {
                        lock (filterLock)
                        {
                            strmItems.AddRange(local);
                        }
                    });

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
                !StrmMediaInfoService.NeedsRestore(item, cacheData))
            {
                return;
            }

            if (StrmMediaInfoService.TryRestoreMetadataFromCache(item, cacheData))
            {
                await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataImport, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("{Prefix} - Restored metadata for {Name} (Size={Size})", logPrefix, item.Name, cacheData.Size);
            }
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

                if (!_config.ForceRefreshIgnoreExisting && (hasVideo || hasAudio) && !HasInvalidCache(item))
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
                try
                {
                    _backgroundTaskCts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }

                _autoExtractQueue.Writer.TryComplete();

                // 监听器清理（内部最长等待 30s）与队列 worker 退出并行等待，避免串行阻塞
                var listenerCleanup = Task.Run(CleanupListener);

                while (_autoExtractQueue.Reader.TryRead(out _))
                {
                }
                _queuedAutoExtractItems.Clear();

                // Wait for queue workers before releasing the cancellation source. The semaphore
                // is managed-only here and may still be in use by a scheduled task during unload.
                var workers = Task.WhenAll(_autoExtractWorkers);
                try
                {
                    if (workers.Wait(TimeSpan.FromSeconds(30)))
                    {
                        _backgroundTaskCts.Dispose();
                    }
                    else
                    {
                        _logger.LogWarning("Timeout waiting for auto-extract workers to stop");
                        _ = workers.ContinueWith(_ => _backgroundTaskCts.Dispose(), TaskScheduler.Default);
                    }
                }
                catch (AggregateException ex)
                {
                    _logger.LogWarning(ex, "Auto-extract worker stopped with an error");
                    _backgroundTaskCts.Dispose();
                }

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
