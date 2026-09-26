using System;
using MediaBrowser.Model.Plugins;

namespace StrmTool
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        private int _refreshDelayMs = 1000;
        private int _maxConcurrentExtract = 5;
        private int _metadataRestoreTimeoutMinutes = 5;

        /// <summary>
        /// 刷新延迟时间（毫秒）
        /// </summary>
        public int RefreshDelayMs
        {
            get => _refreshDelayMs;
            set => _refreshDelayMs = Math.Max(0, value);
        }

        /// <summary>
        /// 是否启用新入库 strm 文件自动提取媒体信息
        /// </summary>
        public bool EnableAutoExtract { get; set; } = true;

        /// <summary>
        /// 是否启用媒体信息缓存
        /// </summary>
        public bool EnableMediaInfoCache { get; set; } = true;

        /// <summary>
        /// 校验 STRM 内容指纹（SHA256）
        /// 启用时：当 strm 内容或 URL 变动时旧缓存自动失效并重新探测
        /// 关闭时：跳过读取时的指纹校验，仅适用于路径迁移且确认媒体未变的场景
        /// 读取不会改写缓存指纹；重新启用后，指纹缺失或不匹配的缓存仍会失效
        /// </summary>
        public bool VerifyStrmContentHash { get; set; } = true;

        /// <summary>
        /// 提取任务的最大并发数（范围：1-50）
        /// </summary>
        public int MaxConcurrentExtract
        {
            get => _maxConcurrentExtract;
            set => _maxConcurrentExtract = Math.Clamp(value, 1, 50);
        }

        /// <summary>
        /// 是否无视是否已有媒体流，强制刷新
        /// </summary>
        public bool ForceRefreshIgnoreExisting { get; set; } = false;

        /// <summary>
        /// 是否无视缓存文件，强制从远程服务器获取
        /// </summary>
        public bool ForceRefreshIgnoreCache { get; set; } = false;

        /// <summary>
        /// 元数据恢复超时时间（分钟）
        /// </summary>
        public int MetadataRestoreTimeoutMinutes
        {
            get => _metadataRestoreTimeoutMinutes;
            set => _metadataRestoreTimeoutMinutes = Math.Clamp(value, 1, 30);
        }
    }
}
