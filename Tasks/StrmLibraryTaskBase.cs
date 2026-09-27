using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Tasks;

namespace StrmTool
{
    /// <summary>
    /// StrmTool 本地库计划任务基类：封装公共依赖、并发处理骨架与单调进度报告。
    /// 导出/恢复任务均为纯本地操作（无远程探测），与 ExtractTask 相互独立。
    /// </summary>
    public abstract class StrmLibraryTaskBase : IScheduledTask
    {
        protected readonly ILogger Logger;
        protected readonly ILibraryManager LibraryManager;
        protected readonly IMediaEncoder MediaEncoder;
        protected readonly IMediaStreamRepository MediaStreamRepository;
        protected readonly IItemRepository ItemRepository;

        protected StrmLibraryTaskBase(
            ILibraryManager libraryManager,
            IMediaEncoder mediaEncoder,
            IMediaStreamRepository mediaStreamRepository,
            IItemRepository itemRepository,
            ILogger logger)
        {
            LibraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
            MediaEncoder = mediaEncoder ?? throw new ArgumentNullException(nameof(mediaEncoder));
            MediaStreamRepository = mediaStreamRepository ?? throw new ArgumentNullException(nameof(mediaStreamRepository));
            ItemRepository = itemRepository ?? throw new ArgumentNullException(nameof(itemRepository));
            Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public string Category => "StrmTool";

        public abstract string Key { get; }
        public abstract string Name { get; }
        public abstract string Description { get; }

        public abstract Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken);

        public virtual IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        protected PluginConfiguration GetConfig()
        {
            return PluginConfigurationProvider.GetCurrent();
        }

        /// <summary>
        /// 创建媒体信息服务实例（每次执行新建，避免持有过期引用）
        /// </summary>
        protected StrmMediaInfoService CreateMediaInfoService()
        {
            return new StrmMediaInfoService(LibraryManager, MediaEncoder, MediaStreamRepository, ItemRepository, Logger);
        }

        /// <summary>
        /// 创建缓存管理实例
        /// </summary>
        protected MediaInfoCache CreateMediaCache()
        {
            return new MediaInfoCache(Logger);
        }

        /// <summary>
        /// 并行筛选候选条目（筛选谓词可能含磁盘 I/O，并行以缩短大库等待时间）
        /// </summary>
        internal static List<T> FilterItemsInParallel<T>(IReadOnlyList<T> items, Func<T, bool> predicate, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = new List<T>();
            var resultLock = new object();

            Parallel.ForEach(
                items,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Max(2, Math.Min(Environment.ProcessorCount, 8)),
                    CancellationToken = cancellationToken
                },
                () => new List<T>(),
                (item, _, local) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (predicate(item))
                    {
                        local.Add(item);
                    }

                    return local;
                },
                local =>
                {
                    lock (resultLock)
                    {
                        result.AddRange(local);
                    }
                });

            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }

        /// <summary>
        /// 以受限并发批量处理条目：取消检查、异常隔离、进度单调递增。
        /// </summary>
        /// <returns>action 返回 true 的条目数</returns>
        protected async Task<int> RunItemsAsync(
            IReadOnlyList<BaseItem> items,
            int maxConcurrency,
            Func<BaseItem, CancellationToken, Task<bool>> actionAsync,
            IProgress<double> progress,
            CancellationToken cancellationToken)
        {
            var concurrency = Math.Max(1, maxConcurrency);
            using var gate = new SemaphoreSlim(concurrency, concurrency);
            return await RunItemsAsync(items, gate, concurrency, actionAsync, progress, Logger, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// 统一的 worker 池批量处理骨架（供计划任务与 ExtractTask 共用）：
        /// 固定 worker 数 + 原子索引分配 + 外部并发闸门，取消传播、逐条异常隔离、进度单调递增。
        /// </summary>
        /// <param name="items">待处理条目</param>
        /// <param name="concurrencyGate">并发闸门（ExtractTask 传入共享信号量，与自动提取共用并发预算）</param>
        /// <param name="workerCount">worker 数量（实际取 min(workerCount, items.Count)）</param>
        /// <param name="actionAsync">单条处理逻辑，返回是否成功</param>
        /// <param name="progress">进度报告</param>
        /// <param name="logger">日志</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>action 返回 true 的条目数</returns>
        internal static async Task<int> RunItemsAsync(
            IReadOnlyList<BaseItem> items,
            SemaphoreSlim concurrencyGate,
            int workerCount,
            Func<BaseItem, CancellationToken, Task<bool>> actionAsync,
            IProgress<double> progress,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int total = items.Count;
            if (total == 0)
            {
                progress?.Report(100);
                return 0;
            }

            int processed = 0;
            int succeeded = 0;
            int nextIndex = -1;
            var progressLock = new object();

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
                    await concurrencyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        bool result = await actionAsync(item, cancellationToken).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (result)
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
                        logger?.LogError(ex, "Error processing {Name} ({Path})", item.Name, item.Path);
                    }
                    finally
                    {
                        concurrencyGate.Release();
                    }

                    // 取消时不计入进度：OperationCanceledException 直接向上传播，跳过下方计数与上报，
                    // 避免被取消条目虚增 processed、甚至将进度误推至 100%。
                    // 加锁保证进度单调递增，不会倒退
                    lock (progressLock)
                    {
                        processed++;
                        progress?.Report(Math.Min(processed * 100.0 / total, 100));
                    }
                }
            }

            var workers = Enumerable.Range(0, Math.Max(1, Math.Min(workerCount, total)))
                .Select(_ => Task.Run(WorkerAsync, cancellationToken));
            try
            {
                await Task.WhenAll(workers).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                logger?.LogInformation("Task cancelled. Processed {Processed}/{Total} items.", processed, total);
                throw;
            }

            progress?.Report(100);
            return succeeded;
        }
    }
}
