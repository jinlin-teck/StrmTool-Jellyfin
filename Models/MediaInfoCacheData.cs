using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace StrmTool
{
    /// <summary>
    /// 缓存数据结构（.strmtool.json 的序列化模型，同时作为保存缓存时的元数据载荷）。
    /// </summary>
    public class MediaInfoCacheData
    {
        [JsonPropertyName("version")]
        public string Version { get; set; }

        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; }

        [JsonPropertyName("mediaStreams")]
        public List<MediaStream> MediaStreams { get; set; }

        [JsonPropertyName("strmContentHash")]
        public string StrmContentHash { get; set; }

        [JsonPropertyName("isValid")]
        public bool IsValid { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("runTimeTicks")]
        public long? RunTimeTicks { get; set; }

        [JsonPropertyName("container")]
        public string Container { get; set; }

        [JsonPropertyName("width")]
        public int Width { get; set; }

        [JsonPropertyName("height")]
        public int Height { get; set; }

        [JsonPropertyName("totalBitrate")]
        public int TotalBitrate { get; set; }

        [JsonPropertyName("audioTagsProbed")]
        public bool? AudioTagsProbed { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; }

        [JsonPropertyName("album")]
        public string Album { get; set; }

        [JsonPropertyName("artists")]
        public List<string> Artists { get; set; }

        [JsonPropertyName("albumArtists")]
        public List<string> AlbumArtists { get; set; }

        [JsonPropertyName("trackNumber")]
        public int? TrackNumber { get; set; }

        [JsonPropertyName("discNumber")]
        public int? DiscNumber { get; set; }

        [JsonPropertyName("productionYear")]
        public int? ProductionYear { get; set; }

        [JsonPropertyName("genres")]
        public List<string> Genres { get; set; }

        /// <summary>
        /// 从探测结果构建缓存元数据载荷（不含媒体流与指纹，由保存方传入/填充）。
        /// </summary>
        public static MediaInfoCacheData FromProbeResult(MediaProbeResult probe)
        {
            if (probe == null)
            {
                return new MediaInfoCacheData();
            }

            return new MediaInfoCacheData
            {
                Size = probe.Size,
                RunTimeTicks = probe.RunTimeTicks,
                Container = probe.Container,
                Width = probe.Width,
                Height = probe.Height,
                TotalBitrate = probe.TotalBitrate,
                AudioTagsProbed = probe.AudioTagsProbed,
                Title = probe.Title,
                Album = probe.Album,
                Artists = probe.Artists,
                AlbumArtists = probe.AlbumArtists,
                TrackNumber = probe.TrackNumber,
                DiscNumber = probe.DiscNumber,
                ProductionYear = probe.ProductionYear,
                Genres = probe.Genres
            };
        }

        /// <summary>
        /// 从库条目当前状态构建缓存元数据载荷（导出任务用；不含媒体流与指纹）。
        /// 分辨率优先取条目元数据，缺失时退回从视频流计算；音频标签字段仅在信息完整时标记已探测。
        /// </summary>
        public static MediaInfoCacheData FromLibraryItem(
            BaseItem item, IReadOnlyList<MediaStream> streams, ILibraryManager libraryManager = null)
        {
            if (item == null)
            {
                return new MediaInfoCacheData();
            }

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
            bool? audioTagsProbed = audio != null && !StrmMediaInfoService.HasMissingAudioMetadata(audio, libraryManager)
                ? true
                : null;
            string exportedTitle = audio != null &&
                                   !string.IsNullOrWhiteSpace(audio.Name) &&
                                   !string.Equals(audio.Name, Path.GetFileNameWithoutExtension(audio.Path), StringComparison.Ordinal)
                ? audio.Name
                : null;

            return new MediaInfoCacheData
            {
                Size = item.Size.GetValueOrDefault(),
                RunTimeTicks = item.RunTimeTicks,
                Container = item.Container,
                Width = width,
                Height = height,
                TotalBitrate = item.TotalBitrate.GetValueOrDefault(),
                AudioTagsProbed = audioTagsProbed,
                Title = exportedTitle,
                Album = audio?.Album,
                Artists = audio?.Artists?.ToList(),
                AlbumArtists = audio?.AlbumArtists?.ToList(),
                TrackNumber = audio?.IndexNumber,
                DiscNumber = audio?.ParentIndexNumber,
                ProductionYear = audio?.ProductionYear,
                Genres = audio?.Genres?.ToList()
            };
        }
    }
}
