using System.Collections.Generic;
using MediaBrowser.Model.Entities;

namespace StrmTool
{
    /// <summary>
    /// 媒体探测结果，包含流信息和元数据
    /// </summary>
    public class MediaProbeResult
    {
        public List<MediaStream> MediaStreams { get; set; } = new List<MediaStream>();
        public long Size { get; set; }
        public long? RunTimeTicks { get; set; }
        public string Container { get; set; }
        public string StrmContentHash { get; set; }
        public string TargetPath { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int TotalBitrate { get; set; }
        public bool? AudioTagsProbed { get; set; }
        public string Title { get; set; }
        public string Album { get; set; }
        public List<string> Artists { get; set; }
        public List<string> AlbumArtists { get; set; }
        public int? TrackNumber { get; set; }
        public int? DiscNumber { get; set; }
        public int? ProductionYear { get; set; }
        public List<string> Genres { get; set; }
        public bool Success => MediaStreams != null && MediaStreams.Count > 0;
    }
}
