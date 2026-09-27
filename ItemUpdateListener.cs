using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;

namespace StrmTool
{
    /// <summary>
    /// 监听Item更新事件，当strm文件的Size等元数据被重置时，从缓存恢复
    /// </summary>
    public class ItemUpdateListener : IDisposable
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly MediaInfoCache _mediaCache;
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<bool>> _runningTasks;
        private readonly CancellationTokenSource _disposeCts = new CancellationTokenSource();
        private volatile PluginConfiguration _config;
        private volatile bool _isDisposed = false;

        public ItemUpdateListener(
            ILibraryManager libraryManager,
            ILogger logger,
            PluginConfiguration config)
            : this(libraryManager, logger, config, null)
        {
        }

        public ItemUpdateListener(
            ILibraryManager libraryManager,
            ILogger logger,
            PluginConfiguration config,
            MediaInfoCache mediaCache)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _mediaCache = mediaCache ?? new MediaInfoCache(logger);
            _runningTasks = new ConcurrentDictionary<Guid, TaskCompletionSource<bool>>();
            _config = config;

            // 订阅Item更新事件
            _libraryManager.ItemUpdated += OnItemUpdated;
            _logger.LogInformation("Item update listener initialized");
        }

        /// <summary>
        /// 刷新配置引用
        /// </summary>
        public void RefreshConfig()
        {
            if (Plugin.Instance != null)
            {
                _config = Plugin.Instance.Configuration;
            }
        }

        private void OnItemUpdated(object sender, ItemChangeEventArgs e)
        {
            if (_isDisposed)
                return;

            try
            {
                RefreshConfig();

                if (_config == null || !_config.EnableMediaInfoCache || _config.ForceRefreshIgnoreCache)
                {
                    return;
                }

                // 检查是否是strm文件
                if (!StrmMediaInfoService.IsStrmFile(e.Item.Path))
                {
                    return;
                }

                var fileName = Path.GetFileNameWithoutExtension(e.Item.Path);

                // 原子性地检查并标记为正在恢复，防止竞态条件
                // 同时作为任务跟踪键，避免同一 item 创建多个任务
                var taskKey = e.Item.Id;

                // 先注册完成信号，再启动真正的异步任务，保证去重和 Dispose 等待覆盖整个恢复过程。
                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!_runningTasks.TryAdd(taskKey, completion))
                {
                    _logger.LogDebug("Item {Name} already being restored, skipping", fileName);
                    return;
                }

                if (_isDisposed)
                {
                    _runningTasks.TryRemove(taskKey, out _);
                    completion.TrySetResult(true);
                    return;
                }

                try
                {
                    // 缓存校验涉及磁盘 I/O，挪到后台执行，避免阻塞 Jellyfin 库事件线程
                    _ = Task.Run(() => EvaluateRestoreAsync(taskKey, fileName, completion));
                }
                catch
                {
                    _runningTasks.TryRemove(taskKey, out _);
                    completion.TrySetResult(true);
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in OnItemUpdated handler");
            }
        }

        /// <summary>
        /// 后台评估是否需要恢复元数据：读取缓存（磁盘 I/O）并校验，命中后才执行恢复。
        /// </summary>
        private async Task EvaluateRestoreAsync(Guid taskKey, string fileName, TaskCompletionSource<bool> completion)
        {
            try
            {
                if (_isDisposed)
                {
                    return;
                }

                RefreshConfig();
                var config = _config;
                if (config == null || !config.EnableMediaInfoCache || config.ForceRefreshIgnoreCache)
                {
                    return;
                }

                // 排队期间 item、路径或缓存都可能变化；重新获取后再校验
                var item = _libraryManager.GetItemById(taskKey);
                if (item == null || !StrmMediaInfoService.IsStrmFile(item.Path))
                {
                    _logger.LogDebug("Item {Name} is no longer available for restore", fileName);
                    return;
                }

                if (!_mediaCache.TryGetFullCache(item.Path, out var cacheData) ||
                    !StrmMediaInfoService.NeedsRestore(item, cacheData, _libraryManager))
                {
                    return;
                }

                await RestoreItemMetadataAsync(taskKey, fileName).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("Restore evaluation cancelled for {Name}", fileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error evaluating restore for {Name}", fileName);
            }
            finally
            {
                // 整个评估/恢复过程仅由这一层释放去重记录，避免误删后续任务。
                _runningTasks.TryRemove(taskKey, out _);
                completion.TrySetResult(true);
            }
        }

        private async Task RestoreItemMetadataAsync(Guid taskKey, string fileName)
        {
            try
            {
                if (_isDisposed)
                {
                    return;
                }

                RefreshConfig();
                var config = _config;
                if (config == null || !config.EnableMediaInfoCache || config.ForceRefreshIgnoreCache)
                {
                    return;
                }

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
                cts.CancelAfter(TimeSpan.FromMinutes(config.MetadataRestoreTimeoutMinutes));

                // 排队期间 item、路径或缓存都可能变化；写入前重新读取并只恢复仍丢失的字段。
                var item = _libraryManager.GetItemById(taskKey);
                if (item == null || !StrmMediaInfoService.IsStrmFile(item.Path))
                {
                    _logger.LogDebug("Item {Name} is no longer available for restore", fileName);
                    return;
                }

                if (!_mediaCache.TryGetFullCache(item.Path, out var cacheData) ||
                    !StrmMediaInfoService.NeedsRestore(item, cacheData, _libraryManager))
                {
                    return;
                }

                cts.Token.ThrowIfCancellationRequested();
                _logger.LogInformation("Restoring metadata for {Name}", fileName);

                // 恢复元数据（只保留前端显示和 Jellyfin 内部需要的字段）
                // 注意：Jellyfin 不会重置媒体流信息，因此不需要恢复 MediaStreams
                if (StrmMediaInfoService.TryRestoreMetadataFromCache(item, cacheData, _libraryManager))
                {
                    // 持久化修改
                    await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataImport, cts.Token).ConfigureAwait(false);

                    _logger.LogInformation("Successfully restored metadata for {Name}", fileName);
                }

                await StrmMediaInfoService.SyncAudioRelationshipsStaticAsync(
                    item, _libraryManager, cts.Token, _logger).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("Restore operation cancelled for {Name}", fileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to restore metadata for {Name}", fileName);
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;

            try
            {
                _libraryManager.ItemUpdated -= OnItemUpdated;
            }
            catch (ObjectDisposedException)
            {
            }

            _disposeCts.Cancel();

            // 等待真正的恢复操作完成（最多等待30秒）
            try
            {
                var allTasks = _runningTasks.Values.Select(source => source.Task).ToArray();
                if (allTasks.Length > 0 && !Task.WaitAll(allTasks, TimeSpan.FromSeconds(30)))
                {
                    _logger.LogWarning("Timeout waiting for {Count} background tasks to complete", allTasks.Length);
                    _ = Task.WhenAll(allTasks).ContinueWith(_ => _disposeCts.Dispose(), TaskScheduler.Default);
                }
                else
                {
                    _disposeCts.Dispose();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error waiting for background tasks to complete");
                _disposeCts.Dispose();
            }

            _logger.LogInformation("Item update listener disposed");
        }
    }
}
