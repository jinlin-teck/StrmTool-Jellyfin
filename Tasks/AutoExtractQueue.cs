using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace StrmTool
{
    /// <summary>
    /// 自动提取后台队列：Channel + 去重 + 有界pending上限 + 固定 worker 处理。
    /// 库事件线程仅入队（零磁盘 I/O），具体处理逻辑由注入的 handler 完成。
    /// </summary>
    internal sealed class AutoExtractQueue : IDisposable
    {
        // 待处理自动提取上限：防止扫描风暴时无界排队（超出后由计划任务兜底）
        private const int MaxPendingItems = 100;

        private readonly ILogger _logger;
        private readonly Func<Guid, CancellationToken, Task> _handler;
        private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>();
        private readonly ConcurrentDictionary<Guid, byte> _queuedItems = new ConcurrentDictionary<Guid, byte>();
        private readonly CancellationTokenSource _backgroundCts = new CancellationTokenSource();
        private readonly Task[] _workers;
        private volatile bool _disposed = false;

        public AutoExtractQueue(int workerCount, Func<Guid, CancellationToken, Task> handler, ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _workers = Enumerable.Range(0, Math.Max(1, workerCount))
                .Select(_ => Task.Run(ProcessQueueAsync))
                .ToArray();
        }

        /// <summary>
        /// 尝试将条目加入自动提取队列（去重 + 上限保护）。
        /// </summary>
        /// <param name="itemId">条目 Id</param>
        /// <param name="displayName">用于日志的展示名（通常为文件名）</param>
        /// <returns>是否成功入队（重复或超限时返回 false）</returns>
        public bool TryEnqueue(Guid itemId, string displayName = null)
        {
            if (_disposed)
            {
                return false;
            }

            // 先判重：队列满时重复事件不应误刷“队列已满”告警
            if (_queuedItems.ContainsKey(itemId))
            {
                return false;
            }

            if (_queuedItems.Count >= MaxPendingItems)
            {
                _logger.LogWarning("Auto-extract queue is full ({Max}), skipping {Name}. It will be processed by the scheduled task.",
                    MaxPendingItems, displayName ?? itemId.ToString());
                return false;
            }

            if (!_queuedItems.TryAdd(itemId, 0))
            {
                return false;
            }

            if (!_queue.Writer.TryWrite(itemId))
            {
                _queuedItems.TryRemove(itemId, out _);
                return false;
            }

            return true;
        }

        /// <summary>
        /// 判断条目是否已在队列中（供测试与诊断使用）
        /// </summary>
        internal bool IsQueued(Guid itemId)
        {
            return _queuedItems.ContainsKey(itemId);
        }

        private async Task ProcessQueueAsync()
        {
            try
            {
                await foreach (var itemId in _queue.Reader.ReadAllAsync(_backgroundCts.Token))
                {
                    try
                    {
                        await _handler(itemId, _backgroundCts.Token).ConfigureAwait(false);
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
                        _queuedItems.TryRemove(itemId, out _);
                    }
                }
            }
            catch (OperationCanceledException) when (_backgroundCts.IsCancellationRequested)
            {
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                _backgroundCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            _queue.Writer.TryComplete();

            while (_queue.Reader.TryRead(out _))
            {
            }
            _queuedItems.Clear();

            // Wait for queue workers before releasing the cancellation source.
            var workers = Task.WhenAll(_workers);
            try
            {
                if (workers.Wait(TimeSpan.FromSeconds(30)))
                {
                    _backgroundCts.Dispose();
                }
                else
                {
                    _logger.LogWarning("Timeout waiting for auto-extract workers to stop");
                    _ = workers.ContinueWith(_ => _backgroundCts.Dispose(), TaskScheduler.Default);
                }
            }
            catch (AggregateException ex)
            {
                _logger.LogWarning(ex, "Auto-extract worker stopped with an error");
                _backgroundCts.Dispose();
            }
        }
    }
}
