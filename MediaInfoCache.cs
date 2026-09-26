using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MediaBrowser.Model.Entities;

namespace StrmTool
{
    public class MediaInfoCache
    {
        private readonly ILogger _logger;
        private const string CacheFileSuffix = ".strmtool.json";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public MediaInfoCache(ILogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// 获取缓存文件路径
        /// </summary>
        private static string GetCachePath(string strmPath)
        {
            if (string.IsNullOrWhiteSpace(strmPath))
                return null;

            // 确保路径是绝对路径（Jellyfin 的 item.Path 应该总是绝对路径）
            if (!Path.IsPathRooted(strmPath))
                return null;

            var directory = Path.GetDirectoryName(strmPath);
            if (string.IsNullOrWhiteSpace(directory))
                return null;

            var fileName = Path.GetFileNameWithoutExtension(strmPath) + CacheFileSuffix;
            var cachePath = Path.Combine(directory, fileName);

            return cachePath;
        }

        /// <summary>
        /// 检查路径是否包含路径遍历攻击模式
        /// </summary>
        /// <summary>
        /// 检查路径是否包含路径遍历攻击模式
        /// 用于验证 strm 文件内容中的路径
        /// </summary>
        public static bool ContainsPathTraversal(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;

            // 检查空字符（注入攻击）
            if (path.Contains('\0'))
            {
                return true;
            }

            // 检查基本的路径遍历模式
            var normalized = path.Replace('/', '\\');
            if (normalized.Contains(@"..\\") || normalized.Contains(@"\\..") || 
                normalized.StartsWith(@"..") || normalized.EndsWith(@".."))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// 检查缓存文件是否存在
        /// </summary>
        public bool HasCacheFile(string strmPath)
        {
            if (string.IsNullOrWhiteSpace(strmPath))
            {
                return false;
            }

            var cachePath = GetCachePath(strmPath);
            return !string.IsNullOrWhiteSpace(cachePath) && File.Exists(cachePath);
        }

        /// <summary>
        /// 验证缓存路径并返回（通用验证方法）
        /// </summary>
        private (bool valid, string cachePath) ValidateCachePath(string strmPath)
        {
            if (string.IsNullOrWhiteSpace(strmPath))
            {
                _logger.LogDebug("Invalid strm path for cache lookup");
                return (false, null);
            }

            var cachePath = GetCachePath(strmPath);
            if (string.IsNullOrWhiteSpace(cachePath))
            {
                _logger.LogDebug("Failed to get cache path for: {Path}", strmPath);
                return (false, null);
            }

            if (!File.Exists(cachePath))
            {
                return (false, null);
            }

            return (true, cachePath);
        }

        /// <summary>
        /// 验证缓存数据是否有效
        /// </summary>
        private bool ValidateCacheData(MediaInfoCacheData cache, string strmPath, bool requireMediaStreams = true, bool? verifyContentHash = null)
        {
            if (cache?.IsValid != true)
                return false;
            
            if (requireMediaStreams && cache.MediaStreams == null)
                return false;

            bool shouldVerify = verifyContentHash ?? (Plugin.Instance?.Configuration?.VerifyStrmContentHash ?? true);
            if (!shouldVerify)
            {
                // 仅跳过读取时的校验，不迁移或回写指纹；重新启用后仍使用原指纹判断有效性。
                return true;
            }

            // 旧缓存没有内容指纹，无法判断 STRM 是否已更换媒体源。
            // STRM 文件暂不可读（哈希为 null）时同样判为无效，触发重探测而非使用过期缓存。
            return !string.IsNullOrEmpty(cache.StrmContentHash) &&
                   string.Equals(cache.StrmContentHash, GetStrmContentHash(strmPath), StringComparison.Ordinal);
        }

        /// <summary>
        /// 计算 STRM 有效内容的 SHA256 指纹。
        /// 与 ReadStrmSourcePath 的读取语义一致：取首个非空白行并 trim，
        /// 避免纯空白/换行差异导致缓存失效与不必要的重探测。
        /// 文件不可读或无有效内容时返回 null，由调用方按"缓存无效/跳过探测"降级处理。
        /// </summary>
        internal static string GetStrmContentHash(string strmPath)
        {
            try
            {
                foreach (var line in File.ReadLines(strmPath))
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length > 0)
                    {
                        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(trimmed)));
                    }
                }

                return null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                return null;
            }
        }

        /// <summary>
        /// 检查并读取缓存（同步版本，保持兼容性）
        /// </summary>
        public bool TryGetCachedMediaStreams(string strmPath, out List<MediaStream> mediaStreams, bool? verifyContentHash = null)
        {
            mediaStreams = null;

            var (valid, cachePath) = ValidateCachePath(strmPath);
            if (!valid)
                return false;

            try
            {
                var json = File.ReadAllText(cachePath);
                var cache = JsonSerializer.Deserialize<MediaInfoCacheData>(json, JsonOptions);

                if (!ValidateCacheData(cache, strmPath, verifyContentHash: verifyContentHash))
                    return false;

                mediaStreams = cache.MediaStreams;
                _logger.LogDebug("Loaded cached media streams from {Path}", cachePath);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error reading cache from {Path}", strmPath);
                return false;
            }
        }

        /// <summary>
        /// 保存完整媒体信息缓存（包含Size等元数据）
        /// </summary>
        private async Task WriteFileAtomicallyAsync(string filePath, string content, CancellationToken cancellationToken)
        {
            string tempPath = null;
            try
            {
                tempPath = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                await File.WriteAllTextAsync(tempPath, content, cancellationToken).ConfigureAwait(false);

                if (File.Exists(filePath))
                {
                    File.Replace(tempPath, filePath, null);
                }
                else
                {
                    File.Move(tempPath, filePath);
                }
            }
            catch
            {
                CleanupTempFile(tempPath);
                throw;
            }
        }

        /// <summary>
        /// 清理临时文件
        /// </summary>
        private void CleanupTempFile(string tempPath)
        {
            if (!string.IsNullOrWhiteSpace(tempPath) && File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception cleanupEx)
                {
                    _logger.LogWarning(cleanupEx, "Failed to cleanup temp cache file {Path}", tempPath);
                }
            }
        }

        /// <summary>
        /// 验证保存缓存的前置条件
        /// </summary>
        private (bool valid, string cachePath) ValidateSaveCache(string strmPath)
        {
            if (string.IsNullOrWhiteSpace(strmPath))
            {
                _logger.LogDebug("Invalid strm path for cache save");
                return (false, null);
            }

            var cachePath = GetCachePath(strmPath);
            if (string.IsNullOrWhiteSpace(cachePath))
            {
                _logger.LogDebug("Failed to get cache path for: {Path}", strmPath);
                return (false, null);
            }

            return (true, cachePath);
        }

        /// <summary>
        /// 保存完整媒体信息缓存（包含Size等元数据）
        /// </summary>
        public async Task SaveFullCacheAsync(
            string strmPath,
            IEnumerable<MediaStream> mediaStreams,
            long size,
            long? runTimeTicks,
            string container,
            string expectedStrmContentHash = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var (valid, cachePath) = ValidateSaveCache(strmPath);
                if (!valid)
                    return;

                var strmContentHash = GetStrmContentHash(strmPath);
                if (strmContentHash == null)
                {
                    _logger.LogWarning("STRM file unreadable; skipping cache save for {Path}", strmPath);
                    return;
                }

                if (expectedStrmContentHash != null &&
                    !string.Equals(expectedStrmContentHash, strmContentHash, StringComparison.Ordinal))
                {
                    _logger.LogWarning("STRM content changed during probing; skipping cache save for {Path}", strmPath);
                    return;
                }

                var cache = new MediaInfoCacheData
                {
                    Version = "1.0",
                    Timestamp = DateTime.UtcNow,
                    MediaStreams = mediaStreams?.ToList() ?? new List<MediaStream>(),
                    StrmContentHash = strmContentHash,
                    IsValid = true,
                    Size = size,
                    RunTimeTicks = runTimeTicks,
                    Container = container
                };

                var json = JsonSerializer.Serialize(cache, JsonOptions);
                await WriteFileAtomicallyAsync(cachePath, json, cancellationToken).ConfigureAwait(false);

                _logger.LogDebug("Saved full cache (Size={Size}) to {Path}", size, cachePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving full cache to {Path}", strmPath);
            }
        }

        /// <summary>
        /// 尝试读取完整缓存数据（包含元数据）
        /// </summary>
        public bool TryGetFullCache(string strmPath, out MediaInfoCacheData cacheData, bool? verifyContentHash = null)
        {
            cacheData = null;

            var (valid, cachePath) = ValidateCachePath(strmPath);
            if (!valid)
                return false;

            try
            {
                var json = File.ReadAllText(cachePath);
                var cache = JsonSerializer.Deserialize<MediaInfoCacheData>(json, JsonOptions);

                if (!ValidateCacheData(cache, strmPath, requireMediaStreams: false, verifyContentHash: verifyContentHash))
                    return false;

                cacheData = cache;
                _logger.LogDebug("Loaded full cache (Size={Size}) from {Path}", cache.Size, cachePath);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error reading full cache from {Path}", strmPath);
                return false;
            }
        }

    }

    /// <summary>
    /// 缓存数据结构
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
    }
}
