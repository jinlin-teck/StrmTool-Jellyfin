using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;

namespace StrmTool
{
    /// <summary>
    /// 导出任务：把库中已具备完整媒体信息、但尚无缓存的 strm 批量写入本地缓存。
    /// 纯本地操作，不访问远程媒体；仅在 EnableMediaInfoCache 启用时有意义。
    /// </summary>
    public class ExportStrmInfoTask : StrmLibraryTaskBase
    {
        public ExportStrmInfoTask(
            ILibraryManager libraryManager,
            IMediaEncoder mediaEncoder,
            IMediaStreamRepository mediaStreamRepository,
            IItemRepository itemRepository,
            ILogger<ExportStrmInfoTask> logger)
            : base(libraryManager, mediaEncoder, mediaStreamRepository, itemRepository, logger)
        {
        }

        public override string Key => "StrmToolExportTask";
        public override string Name => Plugin.Instance?.GetLocalizedString("StrmTool.ExportTaskName") ?? "Export Strm Media Info Cache";
        public override string Description => Plugin.Instance?.GetLocalizedString("StrmTool.ExportTaskDescription") ?? "Exports existing media info of strm files into local .strmtool.json cache files without remote probing";

        public override async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var config = GetConfig();
            if (!config.EnableMediaInfoCache)
            {
                Logger.LogInformation("Media info cache is disabled, export task has nothing to do.");
                progress.Report(100);
                return;
            }

            var service = CreateMediaInfoService();
            var cache = CreateMediaCache();

            var allItems = service.GetAllStrmItems(cancellationToken);
            Logger.LogInformation("Found {Count} strm files in library", allItems.Count);

            // 只回填缺失缓存。已有缓存即使失效也不覆盖，避免把旧库信息绑定到新媒体源。
            // 无缓存时无法证明库信息的来源，手动导出以库中信息已经更新为前提。
            var candidates = FilterItemsInParallel(allItems, item =>
            {
                var streams = service.GetItemMediaStreams(item);
                bool hasVideo = streams.Any(s => s.Type == MediaStreamType.Video);
                bool hasAudio = streams.Any(s => s.Type == MediaStreamType.Audio);
                return (hasVideo || hasAudio) && !cache.HasCacheFile(item.Path);
            }, cancellationToken);

            Logger.LogInformation("{Count} strm files have media info but no cache, exporting...", candidates.Count);

            if (candidates.Count == 0)
            {
                progress.Report(100);
                Logger.LogInformation("Nothing to export, task complete.");
                return;
            }

            int exported = await RunItemsAsync(
                candidates,
                config.MaxConcurrentExtract,
                (item, ct) => ExportSingleItemAsync(service, cache, item, ct),
                progress,
                cancellationToken).ConfigureAwait(false);

            Logger.LogInformation("Export complete. {Succeeded}/{Total} cache files written.", exported, candidates.Count);
        }

        private async Task<bool> ExportSingleItemAsync(
            StrmMediaInfoService service, MediaInfoCache cache, BaseItem item, CancellationToken cancellationToken)
        {
            var streams = service.GetItemMediaStreams(item);
            if (streams.Count == 0)
            {
                return false;
            }

            var strmContentHash = MediaInfoCache.GetStrmContentHash(item.Path);
            if (strmContentHash == null)
            {
                Logger.LogWarning("{Name}: STRM file unreadable, skipping export", item.Name);
                return false;
            }

            // 分辨率优先取条目元数据，缺失时退回从视频流计算
            int width = item.Width;
            int height = item.Height;
            if (width <= 0 || height <= 0)
            {
                var videoStream = StrmMediaInfoService.GetHighestResolutionVideoStream(streams);
                if (videoStream != null)
                {
                    width = videoStream.Width.GetValueOrDefault();
                    height = videoStream.Height.GetValueOrDefault();
                }
            }

            var audio = item as Audio;
            bool? audioTagsProbed = audio != null && !StrmMediaInfoService.HasMissingAudioMetadata(audio, LibraryManager)
                ? true
                : null;
            string exportedTitle = audio != null &&
                                   !string.IsNullOrWhiteSpace(audio.Name) &&
                                   !string.Equals(audio.Name, Path.GetFileNameWithoutExtension(audio.Path), StringComparison.Ordinal)
                ? audio.Name
                : null;

            bool saved = await cache.SaveFullCacheAsync(
                item.Path,
                streams,
                item.Size.GetValueOrDefault(),
                item.RunTimeTicks,
                item.Container,
                expectedStrmContentHash: strmContentHash,
                width: width,
                height: height,
                totalBitrate: item.TotalBitrate.GetValueOrDefault(),
                cancellationToken: cancellationToken,
                onlyIfMissing: true,
                audioTagsProbed: audioTagsProbed,
                title: exportedTitle,
                album: audio?.Album,
                artists: audio?.Artists,
                albumArtists: audio?.AlbumArtists,
                trackNumber: audio?.IndexNumber,
                discNumber: audio?.ParentIndexNumber,
                productionYear: audio?.ProductionYear,
                genres: audio?.Genres).ConfigureAwait(false);

            if (saved)
                Logger.LogInformation("{Name}: media info exported to cache", item.Name);
            else
                Logger.LogWarning("{Name}: cache export skipped or failed", item.Name);
            return saved;
        }
    }
}
