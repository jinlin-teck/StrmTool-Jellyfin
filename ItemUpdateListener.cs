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

                var item = e.Item;
                var fileName = Path.GetFileNameWithoutExtension(item.Path);

                // 检查是否有缓存
                if (!_mediaCache.TryGetFullCache(item.Path, out var cacheData))
                {
                    return;
                }

                if (!NeedsRestore(item, cacheData))
                {
                    return;
                }

                // 原子性地检查并标记为正在恢复，防止竞态条件
                // 同时作为任务跟踪键，避免同一 item 创建多个任务
                var taskKey = item.Id;

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
                    _ = Task.Run(() => RestoreItemMetadataAsync(taskKey, fileName, completion));
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

        private static bool NeedsRestore(BaseItem item, MediaInfoCacheData cacheData)
        {
            return IsSizeReset(item, cacheData)
                || (cacheData.RunTimeTicks.HasValue && !item.RunTimeTicks.HasValue)
                || (!string.IsNullOrEmpty(cacheData.Container) && string.IsNullOrEmpty(item.Container));
        }

        private static bool IsSizeReset(BaseItem item, MediaInfoCacheData cacheData)
        {
            return cacheData.Size > 0 && item.Size < cacheData.Size / 10;
        }

        private async Task RestoreItemMetadataAsync(Guid taskKey, string fileName, TaskCompletionSource<bool> completion)
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

                if (!_mediaCache.TryGetFullCache(item.Path, out var cacheData) || !NeedsRestore(item, cacheData))
                {
                    return;
                }

                cts.Token.ThrowIfCancellationRequested();
                _logger.LogInformation("Restoring metadata for {Name}", fileName);

                // 恢复元数据（只保留前端显示和 Jellyfin 内部需要的字段）
                // 注意：Jellyfin 不会重置媒体流信息，因此不需要恢复 MediaStreams
                if (IsSizeReset(item, cacheData))
                {
                    item.Size = cacheData.Size;
                }

                if (cacheData.RunTimeTicks.HasValue && !item.RunTimeTicks.HasValue)
                {
                    item.RunTimeTicks = cacheData.RunTimeTicks;
                }

                if (!string.IsNullOrEmpty(cacheData.Container) && string.IsNullOrEmpty(item.Container))
                {
                    item.Container = cacheData.Container;
                }

                // 持久化修改
                await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataImport, cts.Token).ConfigureAwait(false);

                _logger.LogInformation("Successfully restored metadata for {Name}", fileName);
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("Restore operation cancelled for {Name}", fileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to restore metadata for {Name}", fileName);
            }
            finally
            {
                _runningTasks.TryRemove(taskKey, out _);
                completion.TrySetResult(true);
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
