using System;
using System.IO;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.MediaInfo;

namespace StrmTool
{
    /// <summary>
    /// 缓存恢复判定与字段回填策略：只决定"是否需要恢复"以及"恢复哪些字段"，
    /// 不负责持久化与音频关系同步（由 <see cref="StrmMediaInfoService"/> 编排）。
    /// </summary>
    internal static class StrmRestorePolicy
    {
        /// <summary>
        /// 判断缓存元数据是否需要恢复（尺寸被重置，或时长/容器/分辨率/码率/歌词标志/Shortcut/音频标签缺失）
        /// </summary>
        public static bool NeedsRestore(BaseItem item, MediaInfoCacheData cacheData, ILibraryManager libraryManager = null)
        {
            if (item == null || cacheData == null)
            {
                return false;
            }

            return IsSizeReset(item, cacheData)
                || (cacheData.RunTimeTicks.HasValue && !item.RunTimeTicks.HasValue)
                || (!string.IsNullOrEmpty(cacheData.Container) && string.IsNullOrEmpty(item.Container))
                || (cacheData.Width > 0 && item.Width <= 0)
                || (cacheData.Height > 0 && item.Height <= 0)
                || (cacheData.TotalBitrate > 0 && item.TotalBitrate.GetValueOrDefault() <= 0)
                || LocalLyricLocator.NeedsLyricMetadataRestore(item, cacheData)
                || AudioMetadataHelper.NeedsAudioMetadataRestore(item, cacheData, libraryManager)
                || NeedsShortcutRestore(item);
        }

        /// <summary>
        /// 判断条目尺寸是否被重置为 strm 文件本身大小（不足缓存值的 1/10）
        /// </summary>
        public static bool IsSizeReset(BaseItem item, MediaInfoCacheData cacheData)
        {
            return item != null && cacheData != null &&
                   cacheData.Size > 0 && item.Size.GetValueOrDefault() < cacheData.Size / 10;
        }

        private static bool NeedsShortcutRestore(BaseItem item)
        {
            if (!StrmMediaInfoService.IsStrmFile(item.Path))
            {
                return false;
            }

            var targetPath = StrmPathHelper.ReadStrmTargetPath(item.Path);
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                // 读取失败与空文件都返回空串；仅当文件确实为空时才判定旧快捷路径失效
                return HasStoredShortcut(item) && IsEmptyStrmFile(item.Path);
            }

            if (StrmPathHelper.GetProtocolFromPath(targetPath) == MediaProtocol.File)
            {
                return HasStoredShortcut(item);
            }

            return !item.IsShortcut ||
                   !string.Equals(item.ShortcutPath, targetPath, StrmPathHelper.PathComparison);
        }

        private static bool HasStoredShortcut(BaseItem item)
        {
            return item.IsShortcut || !string.IsNullOrWhiteSpace(item.ShortcutPath);
        }

        private static bool IsEmptyStrmFile(string path)
        {
            try
            {
                return new FileInfo(path).Length == 0;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                return false;
            }
        }

        /// <summary>
        /// 依据 STRM 当前目标同步 ShortcutPath：远程目标写入，本地目标清除旧值。
        /// 目标为空（不可读/空文件）时保持原值，避免瞬时读取失败破坏已持久化的快捷路径。
        /// </summary>
        public static bool TryApplyTargetShortcut(BaseItem item, string targetPath)
        {
            if (item == null || string.IsNullOrWhiteSpace(targetPath))
            {
                return false;
            }

            if (StrmPathHelper.GetProtocolFromPath(targetPath) != MediaProtocol.File)
            {
                if (item.IsShortcut &&
                    string.Equals(item.ShortcutPath, targetPath, StrmPathHelper.PathComparison))
                {
                    return false;
                }

                item.IsShortcut = true;
                item.ShortcutPath = targetPath;
                return true;
            }

            // 目标已改为本地文件：旧的远程 ShortcutPath 会让播放继续指向旧地址
            if (!item.IsShortcut && string.IsNullOrWhiteSpace(item.ShortcutPath))
            {
                return false;
            }

            item.IsShortcut = false;
            item.ShortcutPath = null;
            return true;
        }

        /// <summary>
        /// 从缓存恢复条目的元数据字段（仅恢复仍缺失的字段），返回是否有修改。
        /// 不执行写库，由调用方决定持久化方式。
        /// </summary>
        public static bool TryRestoreMetadataFromCache(BaseItem item, MediaInfoCacheData cacheData, ILibraryManager libraryManager = null)
        {
            if (item == null || cacheData == null)
            {
                return false;
            }

            bool changed = false;

            if (IsSizeReset(item, cacheData))
            {
                item.Size = cacheData.Size;
                changed = true;
            }

            if (cacheData.RunTimeTicks.HasValue && !item.RunTimeTicks.HasValue)
            {
                item.RunTimeTicks = cacheData.RunTimeTicks;
                changed = true;
            }

            if (!string.IsNullOrEmpty(cacheData.Container) && string.IsNullOrEmpty(item.Container))
            {
                item.Container = cacheData.Container;
                changed = true;
            }

            if (cacheData.Width > 0 && item.Width <= 0)
            {
                item.Width = cacheData.Width;
                changed = true;
            }

            if (cacheData.Height > 0 && item.Height <= 0)
            {
                item.Height = cacheData.Height;
                changed = true;
            }

            if (cacheData.TotalBitrate > 0 && item.TotalBitrate.GetValueOrDefault() <= 0)
            {
                item.TotalBitrate = cacheData.TotalBitrate;
                changed = true;
            }

            if (LocalLyricLocator.TryRestoreLyricMetadataFromCache(item, cacheData))
            {
                changed = true;
            }

            if (AudioMetadataHelper.TryRestoreAudioMetadataFromCache(item, cacheData, libraryManager))
            {
                changed = true;
            }

            if (StrmMediaInfoService.IsStrmFile(item.Path))
            {
                var targetPath = StrmPathHelper.ReadStrmTargetPath(item.Path);
                if (string.IsNullOrWhiteSpace(targetPath) && IsEmptyStrmFile(item.Path) && HasStoredShortcut(item))
                {
                    item.IsShortcut = false;
                    item.ShortcutPath = null;
                    changed = true;
                }
                else if (TryApplyTargetShortcut(item, targetPath))
                {
                    changed = true;
                }
            }

            return changed;
        }
    }
}
