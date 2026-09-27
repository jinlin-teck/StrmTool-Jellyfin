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

        // 有界分片锁：所有实例共用，覆盖读取、隔离和写入的整个事务。
        private static readonly SemaphoreSlim[] CacheLocks = Enumerable.Range(0, 64)
            .Select(_ => new SemaphoreSlim(1, 1)).ToArray();

        private static SemaphoreSlim GetCacheLock(string strmPath)
        {
            var path = GetCachePath(strmPath);
            var key = path == null ? string.Empty : Path.GetFullPath(path);
            return CacheLocks[(int)((uint)StrmPathHelper.PathComparer.GetHashCode(key) % (uint)CacheLocks.Length)];
        }

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

            // URL 只检查原始路径，避免查询参数误报；不能使用 Uri.AbsolutePath，
            // 因为 Uri 会先消除 ../，使检查丢失原始路径段。
            var schemeEnd = path.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd > 0 && Uri.CheckSchemeName(path.Substring(0, schemeEnd)))
            {
                var suffixStart = path.IndexOfAny(new[] { '?', '#' }, schemeEnd + 3);
                if (suffixStart >= 0)
                    path = path.Substring(0, suffixStart);

                var pathStart = path.IndexOfAny(new[] { '/', '\\' }, schemeEnd + 3);
                path = pathStart >= 0 ? Uri.UnescapeDataString(path.Substring(pathStart)) : string.Empty;
            }

            return path.Contains('\0') || path.Split(new[] { '/', '\\' }).Any(segment => segment == "..");
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
        /// 验证缓存数据结构有效性（不含指纹校验）。
        /// 结构无效的文件视为坏缓存，隔离为 .bak 以便后续重新探测覆写。
        /// </summary>
        private bool ValidateCacheStructure(MediaInfoCacheData cache, string cachePath, bool requireMediaStreams)
        {
            if (cache?.IsValid != true)
            {
                QuarantineInvalidCacheFile(cachePath, "cache data is invalid or missing");
                return false;
            }

            if (requireMediaStreams && cache.MediaStreams == null)
            {
                QuarantineInvalidCacheFile(cachePath, "media streams missing");
                return false;
            }

            return true;
        }

        /// <summary>
        /// 验证缓存数据是否有效
        /// </summary>
        private bool ValidateCacheData(MediaInfoCacheData cache, string strmPath, bool requireMediaStreams = true, bool? verifyContentHash = null)
        {
            if (!ValidateCacheStructure(cache, GetCachePath(strmPath), requireMediaStreams))
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
        /// 将无效缓存文件重命名为 .bak（带序号防冲突），使后续探测重新生成缓存
        /// </summary>
        private void QuarantineInvalidCacheFile(string cachePath, string reason)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(cachePath) || !File.Exists(cachePath))
                {
                    return;
                }

                var backupPath = cachePath + ".bak";
                var suffix = 1;
                while (File.Exists(backupPath))
                {
                    backupPath = cachePath + "." + suffix++ + ".bak";
                }

                File.Move(cachePath, backupPath);
                _logger.LogWarning("Invalid media info cache ({Reason}): {Path} moved to {Backup}", reason, cachePath, backupPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not move invalid cache file {Path} to .bak", cachePath);
            }
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
            bool found = TryReadCache(strmPath, out var cache, requireMediaStreams: true, verifyContentHash);
            mediaStreams = found ? cache.MediaStreams : null;
            return found;
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

                cancellationToken.ThrowIfCancellationRequested();
                // 临时文件与目标位于同目录；调用方持有路径锁，隔离不会移走新版本。
                File.Move(tempPath, filePath, overwrite: true);
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
        /// 创建独立快照。同目录本地字幕保存文件名，读取时还原；其他目录的字幕交由 Jellyfin 重新发现。
        /// </summary>
        private static List<MediaStream> CreateStreamsForCache(IEnumerable<MediaStream> streams, string strmPath)
        {
            var snapshot = JsonSerializer.Deserialize<List<MediaStream>>(
                JsonSerializer.Serialize(streams ?? Enumerable.Empty<MediaStream>(), JsonOptions), JsonOptions);
            var directory = Path.GetDirectoryName(Path.GetFullPath(strmPath));
            snapshot.RemoveAll(stream =>
            {
                if (stream == null)
                    return true;
                if (!IsLocalSubtitle(stream))
                    return false;
                if (string.IsNullOrWhiteSpace(stream.Path) || ContainsPathTraversal(stream.Path))
                    return true;

                var fullPath = Path.GetFullPath(stream.Path, directory);
                if (!string.Equals(Path.GetDirectoryName(fullPath), directory, StrmPathHelper.PathComparison))
                    return true;

                stream.Path = Path.GetFileName(fullPath);
                return false;
            });
            return snapshot;
        }

        private static bool IsLocalSubtitle(MediaStream stream) =>
            ((stream.IsExternal && stream.Type == MediaStreamType.Subtitle) ||
             (stream.Type == MediaStreamType.Lyric && !string.IsNullOrWhiteSpace(stream.Path)))
            && stream.IsExternalUrl != true;

        private static void RestoreSubtitlePaths(List<MediaStream> streams, string strmPath)
        {
            if (streams == null)
                return;

            var directory = Path.GetDirectoryName(Path.GetFullPath(strmPath));
            streams.RemoveAll(stream =>
            {
                if (stream == null)
                    return true;
                if (!IsLocalSubtitle(stream))
                    return false;
                if (string.IsNullOrWhiteSpace(stream.Path) || ContainsPathTraversal(stream.Path))
                    return true;
                // 兼容旧缓存的绝对路径；新格式只接受单个文件名，不解析任意相对目录。
                if (!Path.IsPathRooted(stream.Path))
                {
                    if (stream.Path.IndexOfAny(new[] { '/', '\\' }) >= 0 || stream.Path == "." || stream.Path == "..")
                        return true;
                    stream.Path = Path.Combine(directory, stream.Path);
                }
                return false;
            });
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
        /// 保存完整媒体信息缓存（媒体流 + 元数据载荷）。
        /// 媒体流保持惰性枚举语义：快照序列化在分片锁内执行，调用方可传入延迟枚举而不影响并发正确性。
        /// </summary>
        /// <param name="strmPath">strm 文件路径</param>
        /// <param name="mediaStreams">要缓存的媒体流（快照脱敏在锁内进行，不修改调用方对象）</param>
        /// <param name="metadata">元数据载荷（Size/时长/容器/分辨率/码率/音频标签等），可由 <see cref="MediaInfoCacheData.FromProbeResult"/> 或 <see cref="MediaInfoCacheData.FromLibraryItem"/> 构建</param>
        /// <param name="expectedStrmContentHash">期望的 STRM 内容指纹；不匹配时放弃保存（TOCTOU 防护）</param>
        /// <param name="onlyIfMissing">仅当缓存文件不存在时才写入（导出任务用，不覆盖已有缓存）</param>
        /// <param name="cancellationToken">取消令牌</param>
        public async Task<bool> SaveFullCacheAsync(
            string strmPath,
            IEnumerable<MediaStream> mediaStreams,
            MediaInfoCacheData metadata,
            string expectedStrmContentHash = null,
            bool onlyIfMissing = false,
            CancellationToken cancellationToken = default)
        {
            var gate = GetCacheLock(strmPath);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var (valid, cachePath) = ValidateSaveCache(strmPath);
                if (!valid || (onlyIfMissing && File.Exists(cachePath)))
                    return false;

                var strmContentHash = GetStrmContentHash(strmPath);
                if (strmContentHash == null)
                {
                    _logger.LogWarning("STRM file unreadable; skipping cache save for {Path}", strmPath);
                    return false;
                }

                if (expectedStrmContentHash != null &&
                    !string.Equals(expectedStrmContentHash, strmContentHash, StringComparison.Ordinal))
                {
                    _logger.LogWarning("STRM content changed during cache save; skipping cache save for {Path}", strmPath);
                    return false;
                }

                var streamsToCache = CreateStreamsForCache(mediaStreams, strmPath);

                var cache = new MediaInfoCacheData
                {
                    Version = "1.0",
                    Timestamp = DateTime.UtcNow,
                    MediaStreams = streamsToCache,
                    StrmContentHash = strmContentHash,
                    IsValid = true,
                    Size = metadata?.Size ?? 0,
                    RunTimeTicks = metadata?.RunTimeTicks,
                    Container = metadata?.Container,
                    Width = metadata?.Width ?? 0,
                    Height = metadata?.Height ?? 0,
                    TotalBitrate = metadata?.TotalBitrate ?? 0,
                    AudioTagsProbed = metadata?.AudioTagsProbed,
                    Title = NormalizeText(metadata?.Title),
                    Album = NormalizeText(metadata?.Album),
                    Artists = NormalizeStringList(metadata?.Artists),
                    AlbumArtists = NormalizeStringList(metadata?.AlbumArtists),
                    TrackNumber = metadata?.TrackNumber,
                    DiscNumber = metadata?.DiscNumber,
                    ProductionYear = metadata?.ProductionYear,
                    Genres = NormalizeStringList(metadata?.Genres)
                };

                var json = JsonSerializer.Serialize(cache, JsonOptions);
                await WriteFileAtomicallyAsync(cachePath, json, cancellationToken).ConfigureAwait(false);

                _logger.LogDebug("Saved full cache (Size={Size}) to {Path}", cache.Size, cachePath);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 取消时中止写缓存：临时文件已在 WriteFileAtomicallyAsync 内清理，向上传播取消
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving full cache to {Path}", strmPath);
                return false;
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>
        /// 尝试读取完整缓存数据（包含元数据）
        /// </summary>
        public bool TryGetFullCache(string strmPath, out MediaInfoCacheData cacheData, bool? verifyContentHash = null)
        {
            return TryReadCache(strmPath, out cacheData, requireMediaStreams: false, verifyContentHash);
        }

        private bool TryReadCache(string strmPath, out MediaInfoCacheData cacheData, bool requireMediaStreams, bool? verifyContentHash)
        {
            cacheData = null;
            var gate = GetCacheLock(strmPath);
            gate.Wait();
            string cachePath = null;
            try
            {
                var validated = ValidateCachePath(strmPath);
                cachePath = validated.cachePath;
                if (!validated.valid)
                    return false;

                var json = File.ReadAllText(cachePath);
                var cache = JsonSerializer.Deserialize<MediaInfoCacheData>(json, JsonOptions);

                if (!ValidateCacheData(cache, strmPath, requireMediaStreams, verifyContentHash))
                    return false;

                RestoreSubtitlePaths(cache.MediaStreams, strmPath);
                cacheData = cache;
                _logger.LogDebug("Loaded full cache (Size={Size}) from {Path}", cache.Size, cachePath);
                return true;
            }
            catch (JsonException ex)
            {
                QuarantineInvalidCacheFile(cachePath, "JSON cannot be deserialized");
                _logger.LogWarning(ex, "Invalid JSON cache for {Path}", strmPath);
                return false;
            }
            catch (Exception ex)
            {
                // 读取失败不等于格式损坏；权限/共享冲突等暂态错误不能触发隔离。
                _logger.LogWarning(ex, "Error reading full cache from {Path}", strmPath);
                return false;
            }
            finally
            {
                gate.Release();
            }
        }

        internal static string NormalizeText(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        internal static List<string> NormalizeStringList(IEnumerable<string> values, char[] splitDelimiters = null)
        {
            if (values == null)
            {
                return null;
            }

            IEnumerable<string> tokens = splitDelimiters is { Length: > 0 }
                ? values
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .SelectMany(v => v.Split(splitDelimiters, StringSplitOptions.RemoveEmptyEntries))
                : values.Where(v => !string.IsNullOrWhiteSpace(v));

            var list = tokens
                .Select(v => v.Trim())
                .Where(v => v.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return list.Count > 0 ? list : null;
        }
    }
}
