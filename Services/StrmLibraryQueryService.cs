using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Enums;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Dto;

namespace StrmTool
{
    /// <summary>
    /// 库查询辅助：枚举 .strm 条目并读取条目的媒体流，不涉及探测与写入。
    /// </summary>
    internal sealed class StrmLibraryQueryService
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;

        public StrmLibraryQueryService(ILibraryManager libraryManager, ILogger logger)
        {
            _libraryManager = libraryManager;
            _logger = logger;
        }

        /// <summary>
        /// 获取库中所有的 strm 文件（去重）
        /// </summary>
        /// <param name="cancellationToken">取消令牌（库查询本身不支持取消，在查询前后检查）</param>
        /// <returns>所有 strm 文件对应的库条目列表，永不为 null</returns>
        public List<BaseItem> GetAllStrmItems(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // 通过库查询直接取候选条目（strm 入库后即 Movie/Episode/Video/Audio），再按扩展名内存过滤；
            // 相比递归磁盘枚举，避免了全目录遍历、权限目录处理以及逐文件 FindByPath 的数据库往返
            var query = new InternalItemsQuery
            {
                Recursive = true,
                IsVirtualItem = false,
                IncludeItemTypes = new[]
                {
                    BaseItemKind.Movie,
                    BaseItemKind.Episode,
                    BaseItemKind.Video,
                    BaseItemKind.MusicVideo,
                    BaseItemKind.Audio
                }
            };

            try
            {
                var items = _libraryManager.GetItemList(query);
                cancellationToken.ThrowIfCancellationRequested();
                return items
                    .Where(i => i != null && !string.IsNullOrWhiteSpace(i.Path) && StrmMediaInfoService.IsStrmFile(i.Path))
                    .GroupBy(i => i.Path, StrmPathHelper.PathComparer)
                    .Select(g => g.First())
                    .ToList();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error querying library for strm items");
                return new List<BaseItem>();
            }
        }

        /// <summary>
        /// 获取库条目的媒体流信息
        /// </summary>
        /// <param name="item">库条目对象</param>
        /// <returns>媒体流列表，若无法获取则返回空列表</returns>
        public List<MediaStream> GetItemMediaStreams(BaseItem item)
        {
            try
            {
                // 使用 IHasMediaSources 接口获取媒体流，避免反射
                if (item is IHasMediaSources hasMediaSources)
                {
                    var streams = hasMediaSources.GetMediaStreams();
                    if (streams != null)
                    {
                        return streams.ToList();
                    }
                }

                return new List<MediaStream>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting media streams for {ItemType}", item.GetType().Name);
                return new List<MediaStream>();
            }
        }
    }
}
