using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace StrmTool
{
    /// <summary>
    /// 本地同目录外挂歌词定位与 Audio 歌词元数据同步辅助类。
    /// </summary>
    internal static class LocalLyricLocator
    {
        // 对齐 Jellyfin 核心 Emby.Naming.Common.NamingOptions.LyricFileExtensions，按优先级顺序排列。
        private static readonly string[] LyricFileExtensions = new[] { ".lrc", ".elrc", ".txt" };

        public static bool HasMissingLocalLyrics(BaseItem item, IReadOnlyList<MediaStream> currentStreams, ILogger logger = null)
        {
            if (!(item is Audio))
            {
                return false;
            }

            if (currentStreams != null && currentStreams.Any(s => s.Type == MediaStreamType.Lyric))
            {
                return false;
            }

            return FindLocalLyricFiles(item.Path, logger).Count > 0;
        }

        public static void ApplyAudioLyrics(Audio audio, List<MediaStream> streams, ILogger logger = null)
        {
            if (audio == null || streams == null)
            {
                return;
            }

            // 若音频条目在本地磁盘存在，剔除已不存在的陈旧本地歌词流路径（保留远程歌词或无路径内嵌歌词）
            if (!string.IsNullOrWhiteSpace(audio.Path) && File.Exists(audio.Path))
            {
                streams.RemoveAll(s =>
                    s != null &&
                    s.Type == MediaStreamType.Lyric &&
                    s.IsExternalUrl != true &&
                    !string.IsNullOrWhiteSpace(s.Path) &&
                    Path.IsPathRooted(s.Path) &&
                    !File.Exists(s.Path));
            }

            var localLyrics = FindLocalLyricFiles(audio.Path, logger);
            if (!streams.Any(s => s.Type == MediaStreamType.Lyric) && localLyrics.Count > 0)
            {
                int nextIndex = streams.Select(s => s.Index).DefaultIfEmpty(-1).Max() + 1;
                streams.Add(new MediaStream
                {
                    Type = MediaStreamType.Lyric,
                    Path = localLyrics[0],
                    Index = nextIndex
                });
            }

            var lyricPaths = streams
                .Where(s => s.Type == MediaStreamType.Lyric && !string.IsNullOrWhiteSpace(s.Path))
                .Select(s => s.Path)
                .Concat(localLyrics)
                .Distinct(StrmPathHelper.PathComparer)
                .ToArray();

            bool hasLyricStream = streams.Any(s => s.Type == MediaStreamType.Lyric);
            if (lyricPaths.Length > 0)
            {
                audio.LyricFiles = lyricPaths;
                audio.HasLyrics = true;
            }
            else if (audio.LyricFiles != null && audio.LyricFiles.Count > 0)
            {
                // 原本记录的本地歌词文件已被移除时同步清空
                audio.LyricFiles = Array.Empty<string>();
                audio.HasLyrics = hasLyricStream;
            }
            else if (hasLyricStream)
            {
                audio.HasLyrics = true;
            }
        }

        public static bool NeedsLyricMetadataRestore(BaseItem item, MediaInfoCacheData cacheData)
        {
            if (!(item is Audio audio) || cacheData?.MediaStreams == null)
            {
                return false;
            }

            var cachedLyrics = GetExistingCachedLyricPaths(cacheData.MediaStreams);
            bool hasCachedLyricStream = cachedLyrics.Length > 0 ||
                cacheData.MediaStreams.Any(s => s != null && s.Type == MediaStreamType.Lyric && string.IsNullOrWhiteSpace(s.Path));
            if (!hasCachedLyricStream)
            {
                return false;
            }

            if (audio.HasLyrics != true)
            {
                return true;
            }

            return cachedLyrics.Length > 0 && (audio.LyricFiles == null || audio.LyricFiles.Count == 0);
        }

        public static bool TryRestoreLyricMetadataFromCache(BaseItem item, MediaInfoCacheData cacheData)
        {
            if (!NeedsLyricMetadataRestore(item, cacheData) || !(item is Audio audio))
            {
                return false;
            }

            var cachedLyrics = GetExistingCachedLyricPaths(cacheData.MediaStreams);
            bool changed = false;

            if (audio.HasLyrics != true)
            {
                audio.HasLyrics = true;
                changed = true;
            }

            if (cachedLyrics.Length > 0 && (audio.LyricFiles == null || audio.LyricFiles.Count == 0))
            {
                audio.LyricFiles = cachedLyrics;
                changed = true;
            }

            return changed;
        }

        private static string[] GetExistingCachedLyricPaths(IEnumerable<MediaStream> streams)
        {
            return streams
                .Where(s => s != null &&
                            s.Type == MediaStreamType.Lyric &&
                            !string.IsNullOrWhiteSpace(s.Path) &&
                            (s.IsExternalUrl == true || !Path.IsPathRooted(s.Path) || File.Exists(s.Path)))
                .Select(s => s.Path)
                .Distinct(StrmPathHelper.PathComparer)
                .ToArray();
        }

        internal static List<string> FindLocalLyricFiles(string strmPath, ILogger logger = null)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(strmPath) || !Path.IsPathRooted(strmPath))
            {
                return result;
            }

            try
            {
                var directory = Path.GetDirectoryName(strmPath);
                var baseName = Path.GetFileNameWithoutExtension(strmPath);
                if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(baseName) || !Directory.Exists(directory))
                {
                    return result;
                }

                foreach (var ext in LyricFileExtensions)
                {
                    var candidate = Path.Combine(directory, baseName + ext);
                    if (File.Exists(candidate))
                    {
                        result.Add(candidate);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                logger?.LogDebug(ex, "Failed to check local lyric files for: {Path}", strmPath);
            }

            return result;
        }
    }
}
