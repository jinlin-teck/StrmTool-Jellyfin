using System;
using System.IO;
using Microsoft.Extensions.Logging;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace StrmTool
{
    /// <summary>
    /// STRM 路径与协议相关的共享工具：路径比较器、协议识别、STRM 目标地址读取。
    /// </summary>
    internal static class StrmPathHelper
    {
        /// <summary>
        /// 路径字符串比较器：Windows 不区分大小写，Linux/macOS 区分大小写。
        /// </summary>
        public static StringComparer PathComparer =>
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        /// <summary>
        /// 路径字符串比较方式：Windows 不区分大小写，Linux/macOS 区分大小写。
        /// </summary>
        public static StringComparison PathComparison =>
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        /// <summary>
        /// 根据路径前缀识别媒体协议（无法识别时按本地文件处理）
        /// </summary>
        public static MediaProtocol GetProtocolFromPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return MediaProtocol.File;
            }

            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return MediaProtocol.Http;
            }

            if (path.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase))
            {
                return MediaProtocol.Rtmp;
            }

            if (path.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
            {
                return MediaProtocol.Rtsp;
            }

            if (path.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase))
            {
                return MediaProtocol.Ftp;
            }

            return MediaProtocol.File;
        }

        /// <summary>
        /// 读取 strm 文件中首个非空白行作为目标地址（含路径遍历安全检查）。
        /// 文件不存在、无有效内容或读取失败时返回空字符串。
        /// </summary>
        internal static string ReadStrmTargetPath(string strmFilePath, ILogger logger = null)
        {
            if (string.IsNullOrWhiteSpace(strmFilePath) || !File.Exists(strmFilePath))
            {
                return string.Empty;
            }

            try
            {
                foreach (var line in File.ReadLines(strmFilePath))
                {
                    var sourcePath = line.Trim();
                    if (!string.IsNullOrWhiteSpace(sourcePath))
                    {
                        // 安全验证：检查路径遍历攻击
                        if (MediaInfoCache.ContainsPathTraversal(sourcePath))
                        {
                            logger?.LogWarning("Potential path traversal attack detected in strm file: {Path}", strmFilePath);
                            return string.Empty;
                        }
                        return sourcePath;
                    }
                }

                return string.Empty;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                logger?.LogWarning(ex, "Failed to read strm file: {Path}", strmFilePath);
                return string.Empty;
            }
        }
    }
}
