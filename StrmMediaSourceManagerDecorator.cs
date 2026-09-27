using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace StrmTool
{
    /// <summary>
    /// 注册 IMediaSourceManager 装饰器，修复 Jellyfin 仅对 Video 处理 .strm ShortcutPath 而遗漏 Audio 的问题。
    /// </summary>
    public class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        public static bool IsRegistered { get; private set; }

        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            var existingDescriptor = serviceCollection.LastOrDefault(d => d.ServiceType == typeof(IMediaSourceManager));
            if (existingDescriptor == null)
            {
                IsRegistered = false;
                Console.Error.WriteLine(
                    "[StrmTool] Warning: IMediaSourceManager service descriptor not found during plugin service registration; STRM audio playback support is inactive.");
                return;
            }

            serviceCollection.Remove(existingDescriptor);
            serviceCollection.Add(new ServiceDescriptor(
                typeof(IMediaSourceManager),
                sp =>
                {
                    var inner = ResolveDescriptor(sp, existingDescriptor);
                    var logger = sp.GetRequiredService<ILogger<StrmMediaSourceManagerDecorator>>();
                    return new StrmMediaSourceManagerDecorator(inner, logger);
                },
                existingDescriptor.Lifetime));
            IsRegistered = true;
        }

        private static IMediaSourceManager ResolveDescriptor(IServiceProvider serviceProvider, ServiceDescriptor descriptor)
        {
            if (descriptor.ImplementationInstance is IMediaSourceManager instance)
            {
                return instance;
            }

            if (descriptor.ImplementationFactory != null)
            {
                return (IMediaSourceManager)descriptor.ImplementationFactory(serviceProvider);
            }

            if (descriptor.ImplementationType != null)
            {
                return (IMediaSourceManager)ActivatorUtilities.CreateInstance(serviceProvider, descriptor.ImplementationType);
            }

            throw new InvalidOperationException("Unable to resolve inner IMediaSourceManager service descriptor.");
        }
    }

    /// <summary>
    /// IMediaSourceManager 装饰器：
    /// 1. 为 .strm 封装的 Audio（及其他未被替换路径的 .strm 条目）将 MediaSourceInfo.Path/Protocol/IsRemote 替换为真实目标地址；
    /// 2. 当 .strm 音频已具备提取好的音频流与有效目标地址时，跳过起播阶段重复触发的 FullRefresh 远程探测。
    /// </summary>
    public class StrmMediaSourceManagerDecorator : IMediaSourceManager, IDisposable
    {
        private readonly IMediaSourceManager _inner;
        private readonly ILogger _logger;

        public StrmMediaSourceManagerDecorator(IMediaSourceManager inner, ILogger logger)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _logger = logger;
        }

        public void AddParts(IEnumerable<IMediaSourceProvider> providers)
        {
            _inner.AddParts(providers);
        }

        public IReadOnlyList<MediaStream> GetMediaStreams(Guid itemId)
        {
            return _inner.GetMediaStreams(itemId);
        }

        public IReadOnlyList<MediaStream> GetMediaStreams(MediaStreamQuery query)
        {
            return _inner.GetMediaStreams(query);
        }

        public IReadOnlyList<MediaAttachment> GetMediaAttachments(Guid itemId)
        {
            return _inner.GetMediaAttachments(itemId);
        }

        public IReadOnlyList<MediaAttachment> GetMediaAttachments(MediaAttachmentQuery query)
        {
            return _inner.GetMediaAttachments(query);
        }

        public async Task<IReadOnlyList<MediaSourceInfo>> GetPlaybackMediaSources(
            BaseItem item,
            User user,
            bool allowMediaProbe,
            bool enablePathSubstitution,
            CancellationToken cancellationToken)
        {
            bool effectiveAllowProbe = ShouldAllowPlaybackProbe(item, allowMediaProbe);
            var sources = await _inner.GetPlaybackMediaSources(
                item,
                user,
                effectiveAllowProbe,
                enablePathSubstitution,
                cancellationToken).ConfigureAwait(false);
            RewriteStrmMediaSources(item, sources);
            return sources;
        }

        public IReadOnlyList<MediaSourceInfo> GetStaticMediaSources(BaseItem item, bool enablePathSubstitution, User user = null)
        {
            var sources = _inner.GetStaticMediaSources(item, enablePathSubstitution, user);
            RewriteStrmMediaSources(item, sources);
            return sources;
        }

        public async Task<MediaSourceInfo> GetMediaSource(
            BaseItem item,
            string mediaSourceId,
            string liveStreamId,
            bool enablePathSubstitution,
            CancellationToken cancellationToken)
        {
            var source = await _inner.GetMediaSource(
                item,
                mediaSourceId,
                liveStreamId,
                enablePathSubstitution,
                cancellationToken).ConfigureAwait(false);
            if (source != null)
            {
                RewriteStrmMediaSource(item, source);
            }

            return source;
        }

        public Task<LiveStreamResponse> OpenLiveStream(LiveStreamRequest request, CancellationToken cancellationToken)
        {
            return _inner.OpenLiveStream(request, cancellationToken);
        }

        public Task<Tuple<LiveStreamResponse, IDirectStreamProvider>> OpenLiveStreamInternal(
            LiveStreamRequest request,
            CancellationToken cancellationToken)
        {
            return _inner.OpenLiveStreamInternal(request, cancellationToken);
        }

        public Task<MediaSourceInfo> GetLiveStream(string id, CancellationToken cancellationToken)
        {
            return _inner.GetLiveStream(id, cancellationToken);
        }

        public Task<Tuple<MediaSourceInfo, IDirectStreamProvider>> GetLiveStreamWithDirectStreamProvider(
            string id,
            CancellationToken cancellationToken)
        {
            return _inner.GetLiveStreamWithDirectStreamProvider(id, cancellationToken);
        }

        public ILiveStream GetLiveStreamInfo(string id)
        {
            return _inner.GetLiveStreamInfo(id);
        }

        public ILiveStream GetLiveStreamInfoByUniqueId(string uniqueId)
        {
            return _inner.GetLiveStreamInfoByUniqueId(uniqueId);
        }

        public Task<IReadOnlyList<MediaSourceInfo>> GetRecordingStreamMediaSources(
            ActiveRecordingInfo info,
            CancellationToken cancellationToken)
        {
            return _inner.GetRecordingStreamMediaSources(info, cancellationToken);
        }

        public Task CloseLiveStream(string id)
        {
            return _inner.CloseLiveStream(id);
        }

        public Task<MediaSourceInfo> GetLiveStreamMediaInfo(string id, CancellationToken cancellationToken)
        {
            return _inner.GetLiveStreamMediaInfo(id, cancellationToken);
        }

        public bool SupportsDirectStream(string path, MediaProtocol protocol)
        {
            return _inner.SupportsDirectStream(path, protocol);
        }

        public MediaProtocol GetPathProtocol(string path)
        {
            return _inner.GetPathProtocol(path);
        }

        public void SetDefaultAudioAndSubtitleStreamIndices(BaseItem item, MediaSourceInfo source, User user)
        {
            _inner.SetDefaultAudioAndSubtitleStreamIndices(item, source, user);
        }

        public Task AddMediaInfoWithProbe(
            MediaSourceInfo mediaSource,
            bool isAudio,
            string cacheKey,
            bool addProbeDelay,
            bool isLiveStream,
            CancellationToken cancellationToken)
        {
            return _inner.AddMediaInfoWithProbe(mediaSource, isAudio, cacheKey, addProbeDelay, isLiveStream, cancellationToken);
        }

        internal bool ShouldAllowPlaybackProbe(BaseItem item, bool allowMediaProbe)
        {
            if (!allowMediaProbe || item == null || !StrmMediaInfoService.IsStrmFile(item.Path))
            {
                return allowMediaProbe;
            }

            if (item.MediaType != MediaType.Audio)
            {
                return allowMediaProbe;
            }

            if (string.IsNullOrWhiteSpace(ResolveStrmTargetPath(item)))
            {
                return true;
            }

            try
            {
                var streams = _inner.GetMediaStreams(item.Id);
                if (streams != null && streams.Any(s => s.Type == MediaStreamType.Audio))
                {
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to check existing media streams for {Path}", item.Path);
            }

            return true;
        }

        internal void RewriteStrmMediaSources(BaseItem item, IReadOnlyList<MediaSourceInfo> sources)
        {
            if (item == null || sources == null || sources.Count == 0)
            {
                return;
            }

            foreach (var source in sources)
            {
                RewriteStrmMediaSource(item, source);
            }
        }

        internal void RewriteStrmMediaSource(BaseItem item, MediaSourceInfo source)
        {
            if (item == null || source == null)
            {
                return;
            }

            if (source.Type == MediaSourceType.Placeholder || !string.IsNullOrEmpty(source.LiveStreamId))
            {
                return;
            }

            if (!StrmMediaInfoService.IsStrmFile(item.Path) || !StrmMediaInfoService.IsStrmFile(source.Path))
            {
                return;
            }

            var targetPath = ResolveStrmTargetPath(item);
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                return;
            }

            var protocol = _inner.GetPathProtocol(targetPath);
            source.Path = targetPath;
            source.Protocol = protocol;
            source.IsRemote = protocol != MediaProtocol.File;

            if (string.IsNullOrWhiteSpace(source.Container) ||
                string.Equals(source.Container, "strm", StringComparison.OrdinalIgnoreCase))
            {
                var inferredContainer = InferContainerFromTargetPath(targetPath);
                if (!string.IsNullOrWhiteSpace(inferredContainer))
                {
                    source.Container = inferredContainer;
                }
            }

            if (!string.IsNullOrEmpty(source.Path))
            {
                source.SupportsDirectStream = _inner.SupportsDirectStream(source.Path, source.Protocol);
            }

            if (item.MediaType == MediaType.Audio &&
                !source.DefaultAudioStreamIndex.HasValue &&
                source.MediaStreams != null)
            {
                var audioStream = source.MediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Audio);
                if (audioStream != null)
                {
                    source.DefaultAudioStreamIndex = audioStream.Index;
                }
            }
        }

        internal string ResolveStrmTargetPath(BaseItem item)
        {
            if (item == null || !StrmMediaInfoService.IsStrmFile(item.Path))
            {
                return string.Empty;
            }

            // 热路径优先使用内存中的 ShortcutPath，避免每次获取 MediaSources 或渲染列表时同步读盘。
            if (!string.IsNullOrWhiteSpace(item.ShortcutPath))
            {
                var shortcut = item.ShortcutPath.Trim();
                if (!MediaInfoCache.ContainsPathTraversal(shortcut))
                {
                    return shortcut;
                }

                _logger?.LogWarning("Potential path traversal attack detected in ShortcutPath for: {Path}", item.Path);
                return string.Empty;
            }

            var fromFile = StrmMediaInfoService.ReadStrmTargetPath(item.Path, _logger);
            if (!string.IsNullOrWhiteSpace(fromFile))
            {
                if (_inner.GetPathProtocol(fromFile) != MediaProtocol.File)
                {
                    item.IsShortcut = true;
                    item.ShortcutPath = fromFile;
                }

                return fromFile;
            }

            return string.Empty;
        }

        internal static string InferContainerFromTargetPath(string targetPath)
        {
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                return null;
            }

            var pathPart = targetPath.Trim();
            int queryOrHash = pathPart.IndexOfAny(new[] { '?', '#' });
            if (queryOrHash >= 0)
            {
                pathPart = pathPart.Substring(0, queryOrHash);
            }

            if (Uri.TryCreate(pathPart, UriKind.Absolute, out var uri) && uri.Scheme != Uri.UriSchemeFile)
            {
                pathPart = Uri.UnescapeDataString(uri.AbsolutePath);
            }

            var ext = Path.GetExtension(pathPart)?.TrimStart('.');
            if (string.IsNullOrWhiteSpace(ext) || string.Equals(ext, "strm", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return ext.ToLowerInvariant();
        }

        public void Dispose()
        {
            if (_inner is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
