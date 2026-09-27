using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
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
            return Plugin.Instance?.Configuration ?? new PluginConfiguration();
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
        protected static List<T> FilterItemsInParallel<T>(IReadOnlyList<T> items, Func<T, bool> predicate, CancellationToken cancellationToken)
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
            IReadOnlyList<MediaBrowser.Controller.Entities.BaseItem> items,
            int maxConcurrency,
            Func<MediaBrowser.Controller.Entities.BaseItem, CancellationToken, Task<bool>> actionAsync,
            IProgress<double> progress,
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
            var progressLock = new object();
            var concurrency = Math.Max(1, maxConcurrency);
            using var semaphore = new SemaphoreSlim(concurrency, concurrency);

            var tasks = items.Select(async item =>
            {
                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    bool result;
                    try
                    {
                        result = await actionAsync(item, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "Error processing {Name} ({Path})", item.Name, item.Path);
                        result = false;
                    }

                    if (result)
                    {
                        Interlocked.Increment(ref succeeded);
                    }
                }
                finally
                {
                    semaphore.Release();
                }

                // 加锁保证进度单调递增，不会倒退
                lock (progressLock)
                {
                    processed++;
                    progress?.Report(processed * 100.0 / total);
                }
            });

            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Logger.LogInformation("Task cancelled. Processed {Processed}/{Total} items.", processed, total);
                throw;
            }

            return succeeded;
        }
    }
}
