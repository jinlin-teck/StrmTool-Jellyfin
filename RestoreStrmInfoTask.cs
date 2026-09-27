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
using MediaBrowser.Model.Entities;

namespace StrmTool
{
    /// <summary>
    /// 恢复任务：把存在有效缓存、但媒体流缺失或元数据被重置的 strm 批量恢复。
    /// 纯本地操作，不访问远程媒体；
    /// EnableMediaInfoCache=false 或 ForceRefreshIgnoreCache=true 时无操作（与 ExtractTask 缓存读取语义一致）。
    /// </summary>
    public class RestoreStrmInfoTask : StrmLibraryTaskBase
    {
        public RestoreStrmInfoTask(
            ILibraryManager libraryManager,
            IMediaEncoder mediaEncoder,
            IMediaStreamRepository mediaStreamRepository,
            IItemRepository itemRepository,
            ILogger<RestoreStrmInfoTask> logger)
            : base(libraryManager, mediaEncoder, mediaStreamRepository, itemRepository, logger)
        {
        }

        public override string Key => "StrmToolRestoreTask";
        public override string Name => Plugin.Instance?.GetLocalizedString("StrmTool.RestoreTaskName") ?? "Restore Strm Media Info from Cache";
        public override string Description => Plugin.Instance?.GetLocalizedString("StrmTool.RestoreTaskDescription") ?? "Restores media streams and metadata for strm files from valid .strmtool.json cache files without remote probing";

        public override async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var config = GetConfig();
            if (!config.EnableMediaInfoCache || config.ForceRefreshIgnoreCache)
            {
                Logger.LogInformation("Media info cache is disabled or cache reads are bypassed, restore task has nothing to do.");
                progress.Report(100);
                return;
            }

            var service = CreateMediaInfoService();
            var cache = CreateMediaCache();

            var allItems = service.GetAllStrmItems(cancellationToken);
            Logger.LogInformation("Found {Count} strm files in library", allItems.Count);

            // 候选筛选（含磁盘 I/O，并行）：流缺失需缓存可恢复，或元数据被重置需恢复
            var candidates = FilterItemsInParallel(allItems, item =>
            {
                if (!cache.TryGetFullCache(item.Path, out var cacheData))
                {
                    return false;
                }

                var streams = service.GetItemMediaStreams(item);
                bool hasVideo = streams.Any(s => s.Type == MediaStreamType.Video);
                bool hasAudio = streams.Any(s => s.Type == MediaStreamType.Audio);

                if (hasVideo || hasAudio)
                {
                    return StrmMediaInfoService.NeedsRestore(item, cacheData)
                        || StrmMediaInfoService.HasMissingLocalLyrics(item, streams);
                }

                return cacheData.MediaStreams != null && cacheData.MediaStreams.Count > 0;
            }, cancellationToken);

            Logger.LogInformation("{Count} strm files need restore from cache", candidates.Count);

            if (candidates.Count == 0)
            {
                progress.Report(100);
                Logger.LogInformation("Nothing to restore, task complete.");
                return;
            }

            int restored = await RunItemsAsync(
                candidates,
                config.MaxConcurrentExtract,
                (item, ct) => RestoreSingleItemAsync(service, cache, item, ct),
                progress,
                cancellationToken).ConfigureAwait(false);

            Logger.LogInformation("Restore complete. {Succeeded}/{Total} items restored.", restored, candidates.Count);
        }

        private async Task<bool> RestoreSingleItemAsync(
            StrmMediaInfoService service, MediaInfoCache cache, BaseItem item, CancellationToken cancellationToken)
        {
            // 处理前重新读取缓存，确保仍有效（指纹校验内建）
            if (!cache.TryGetFullCache(item.Path, out var cacheData))
            {
                return false;
            }

            bool changed = false;

            var streams = service.GetItemMediaStreams(item);
            bool hasVideo = streams.Any(s => s.Type == MediaStreamType.Video);
            bool hasAudio = streams.Any(s => s.Type == MediaStreamType.Audio);
            bool missingLyrics = StrmMediaInfoService.HasMissingLocalLyrics(item, streams);

            if ((!hasVideo && !hasAudio || missingLyrics) && cacheData.MediaStreams != null && cacheData.MediaStreams.Count > 0)
            {
                await service.SaveMediaStreamsAsync(item, cacheData.MediaStreams, cancellationToken).ConfigureAwait(false);
                changed = true;
                Logger.LogInformation("{Name}: media streams restored from cache ({Count} streams)",
                    item.Name, cacheData.MediaStreams.Count);
            }

            // 元数据恢复：逐字段判断仍缺失才写入（与 ItemUpdateListener 共用逻辑，并发执行幂等）
            if (StrmMediaInfoService.TryRestoreMetadataFromCache(item, cacheData))
            {
                await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataImport, cancellationToken).ConfigureAwait(false);
                changed = true;
                Logger.LogInformation("{Name}: metadata restored from cache", item.Name);
            }

            return changed;
        }
    }
}
