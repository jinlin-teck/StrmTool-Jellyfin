using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace StrmTool
{
    /// <summary>
    /// 自动提取运行时：装配库扫描监听、元数据恢复监听与后台队列，并负责其生命周期。
    /// 事件线程只入队，具体提取逻辑由注入的 handler 执行。
    /// </summary>
    internal sealed class AutoExtractRuntime : IDisposable
    {
        private readonly ILogger _logger;
        private readonly Func<PluginConfiguration> _configProvider;
        private readonly AutoExtractQueue _queue;
        private readonly EventHandler<BaseItem> _onStrmFileDetected;
        private readonly object _eventLock = new object();

        private LibraryScanListener _scanListener;
        private ItemUpdateListener _updateListener;
        private volatile bool _disposed;

        public AutoExtractRuntime(
            ILibraryManager libraryManager,
            ILogger logger,
            MediaInfoCache mediaCache,
            Func<PluginConfiguration> configProvider,
            Func<Guid, CancellationToken, Task> handler)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
            _onStrmFileDetected = OnStrmFileDetected;

            var config = configProvider() ?? new PluginConfiguration();

            try
            {
                _scanListener = new LibraryScanListener(libraryManager, logger, config);
                _scanListener.StrmFileDetected += _onStrmFileDetected;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize library scan listener");
            }

            try
            {
                _updateListener = new ItemUpdateListener(libraryManager, logger, config, mediaCache);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize item update listener");
            }

            // worker 数量固定为启动时的 MaxConcurrentExtract（修改需重启生效）
            _queue = new AutoExtractQueue(config.MaxConcurrentExtract, handler, logger);
        }

        private void OnStrmFileDetected(object sender, BaseItem item)
        {
            if (_disposed)
            {
                return;
            }

            var config = _configProvider();
            var fileName = Path.GetFileNameWithoutExtension(item.Path);

            if (config?.EnableAutoExtract != true)
            {
                _logger.LogDebug("Auto-extract is disabled, skipping {Name}", fileName);
                return;
            }

            // 不在事件线程读取媒体流/缓存；强制刷新和缓存失效交由后台统一判断。
            _queue.TryEnqueue(item.Id, fileName);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // 监听器清理（内部最长等待 30s）与队列 worker 退出并行等待，避免串行阻塞
            var listenerCleanup = Task.Run(CleanupListeners);
            _queue.Dispose();

            try
            {
                listenerCleanup.Wait();
            }
            catch (AggregateException ex)
            {
                _logger.LogWarning(ex, "Error cleaning up listeners");
            }
        }

        private void CleanupListeners()
        {
            lock (_eventLock)
            {
                if (_scanListener != null)
                {
                    _scanListener.StrmFileDetected -= _onStrmFileDetected;
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
    }
}
