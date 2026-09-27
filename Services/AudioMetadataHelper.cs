using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace StrmTool
{
    /// <summary>
    /// STRM 音频标签提取、标准目录结构（专辑艺术家/专辑/歌曲）回退、父级专辑同步与缓存恢复辅助类。
    /// </summary>
    internal static class AudioMetadataHelper
    {
        private static readonly char[] ArtistSplitDelimiters = new[] { '/', ';', '|', '\\', '、' };
        private static readonly Regex LeadingTrackRegex = new(
            @"^(?:(?<disc>\d{1,2})\s*-\s*)?(?<track>\d{1,3})(?:\s*[-._]\s*|\s+)(?<rest>.+)$",
            RegexOptions.Compiled);
        private static readonly Regex MultiDiscFolderRegex = new(
            @"^(?:cd|disc|disk)\s*(?<disc>\d{1,2})$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly SemaphoreSlim[] AlbumLocks = Enumerable.Range(0, 32)
            .Select(_ => new SemaphoreSlim(1, 1)).ToArray();

        private static SemaphoreSlim GetAlbumLock(Guid albumId)
        {
            return AlbumLocks[(int)((uint)albumId.GetHashCode() % (uint)AlbumLocks.Length)];
        }

        private sealed class AudioFallbackMetadata
        {
            public string Title { get; set; }
            public string Album { get; set; }
            public List<string> Artists { get; set; }
            public List<string> AlbumArtists { get; set; }
            public int? TrackNumber { get; set; }
            public int? DiscNumber { get; set; }
            public int? ProductionYear { get; set; }
            public List<string> Genres { get; set; }
        }

        public static bool HasMissingAudioMetadata(
            BaseItem item, ILibraryManager libraryManager = null, bool checkParentAlbum = true, ILogger logger = null)
        {
            if (!(item is Audio audio))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(audio.Album) ||
                audio.Artists == null || audio.Artists.Count == 0 ||
                audio.AlbumArtists == null || audio.AlbumArtists.Count == 0)
            {
                return true;
            }

            if (!checkParentAlbum)
            {
                return false;
            }

            var album = FindParent<MusicAlbum>(audio, libraryManager, logger);
            if (album != null && !album.IsLocked)
            {
                if (album.AlbumArtists == null || album.AlbumArtists.Count == 0 ||
                    album.Artists == null || album.Artists.Count == 0)
                {
                    return true;
                }
            }

            return false;
        }

        public static void PopulateProbeResult(
            Audio audio, MediaInfo mediaInfo, MediaProbeResult result, ILibraryManager libraryManager, ILogger logger = null)
        {
            if (audio == null || result == null)
            {
                return;
            }

            if (mediaInfo != null)
            {
                result.AudioTagsProbed = true;
                result.Title = NormalizeString(mediaInfo.Name);
                result.Album = NormalizeString(mediaInfo.Album);
                result.Artists = NormalizeList(mediaInfo.Artists);
                result.AlbumArtists = NormalizeList(mediaInfo.AlbumArtists);
                result.TrackNumber = mediaInfo.IndexNumber > 0 ? mediaInfo.IndexNumber : null;
                result.DiscNumber = mediaInfo.ParentIndexNumber > 0 ? mediaInfo.ParentIndexNumber : null;
                int? year = mediaInfo.ProductionYear ?? mediaInfo.PremiereDate?.Year;
                result.ProductionYear = year is > 0 and <= 9999 ? year : null;
                result.Genres = NormalizeList(mediaInfo.Genres);
            }

            var fallback = ResolveFallbackMetadata(audio, libraryManager, logger);

            if (string.IsNullOrWhiteSpace(result.Album))
            {
                result.Album = fallback.Album;
            }

            if (result.AlbumArtists == null || result.AlbumArtists.Count == 0)
            {
                result.AlbumArtists = fallback.AlbumArtists is { Count: > 0 }
                    ? fallback.AlbumArtists
                    : result.Artists;
            }

            if (result.Artists == null || result.Artists.Count == 0)
            {
                result.Artists = fallback.Artists is { Count: > 0 }
                    ? fallback.Artists
                    : result.AlbumArtists;
            }

            if (string.IsNullOrWhiteSpace(result.Title))
            {
                result.Title = fallback.Title;
            }

            if (!result.TrackNumber.HasValue)
            {
                result.TrackNumber = fallback.TrackNumber;
            }

            if (!result.DiscNumber.HasValue)
            {
                result.DiscNumber = fallback.DiscNumber;
            }
        }

        public static void ApplyProbeMetadata(Audio audio, MediaProbeResult result)
        {
            if (audio == null || result == null)
            {
                return;
            }

            ApplyEffectiveAudioMetadata(
                audio,
                new AudioFallbackMetadata
                {
                    Title = result.Title,
                    Album = result.Album,
                    Artists = result.Artists,
                    AlbumArtists = result.AlbumArtists,
                    TrackNumber = result.TrackNumber,
                    DiscNumber = result.DiscNumber,
                    ProductionYear = result.ProductionYear,
                    Genres = result.Genres
                });
        }

        public static bool NeedsAudioMetadataRestore(
            BaseItem item, MediaInfoCacheData cacheData, ILibraryManager libraryManager = null)
        {
            if (!(item is Audio audio) || cacheData == null)
            {
                return false;
            }

            var effective = GetEffectiveCacheMetadata(audio, cacheData, libraryManager);
            if (NeedsAudioItemRestore(audio, effective))
            {
                return true;
            }

            return NeedsParentAlbumSync(audio, effective, libraryManager);
        }

        public static bool TryRestoreAudioMetadataFromCache(
            BaseItem item, MediaInfoCacheData cacheData, ILibraryManager libraryManager = null)
        {
            if (!(item is Audio audio) || cacheData == null)
            {
                return false;
            }

            var effective = GetEffectiveCacheMetadata(audio, cacheData, libraryManager);
            return ApplyEffectiveAudioMetadata(audio, effective);
        }

        /// <summary>
        /// 结合缓存数据与目录/父级实体兜底生成有效音频元数据。
        /// 设计约定：无论 AudioTagsProbed 是否为 true，当缓存或远端内嵌标签中缺失专辑/艺术家时，
        /// 始终回退使用标准目录结构（专辑艺术家/专辑/歌曲）或父级实体补齐，保持与探测路径一致。
        /// </summary>
        private static AudioFallbackMetadata GetEffectiveCacheMetadata(
            Audio audio, MediaInfoCacheData cacheData, ILibraryManager libraryManager, ILogger logger = null)
        {
            var fallback = ResolveFallbackMetadata(audio, libraryManager, logger);

            var album = NormalizeString(cacheData.Album) ?? fallback.Album;
            var cachedArtists = NormalizeList(cacheData.Artists);
            var cachedAlbumArtists = NormalizeList(cacheData.AlbumArtists);

            var albumArtists = cachedAlbumArtists is { Count: > 0 }
                ? cachedAlbumArtists
                : (fallback.AlbumArtists is { Count: > 0 } ? fallback.AlbumArtists : cachedArtists);

            var artists = cachedArtists is { Count: > 0 }
                ? cachedArtists
                : (fallback.Artists is { Count: > 0 } ? fallback.Artists : albumArtists);

            return new AudioFallbackMetadata
            {
                Title = NormalizeString(cacheData.Title) ?? fallback.Title,
                Album = album,
                Artists = artists,
                AlbumArtists = albumArtists,
                TrackNumber = cacheData.TrackNumber ?? fallback.TrackNumber,
                DiscNumber = cacheData.DiscNumber ?? fallback.DiscNumber,
                ProductionYear = cacheData.ProductionYear,
                Genres = NormalizeList(cacheData.Genres)
            };
        }

        private static bool NeedsAudioItemRestore(Audio audio, AudioFallbackMetadata effective)
        {
            if (audio == null || effective == null || audio.IsLocked)
            {
                return false;
            }

            if (ShouldUpdateAudioTitle(audio, effective.Title))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(effective.Album) && string.IsNullOrWhiteSpace(audio.Album))
            {
                return true;
            }

            if (effective.Artists is { Count: > 0 } && (audio.Artists == null || audio.Artists.Count == 0))
            {
                return true;
            }

            if (effective.AlbumArtists is { Count: > 0 } && (audio.AlbumArtists == null || audio.AlbumArtists.Count == 0))
            {
                return true;
            }

            if (effective.TrackNumber.HasValue && !audio.IndexNumber.HasValue)
            {
                return true;
            }

            if (effective.DiscNumber.HasValue && !audio.ParentIndexNumber.HasValue)
            {
                return true;
            }

            if (effective.ProductionYear.HasValue && !audio.ProductionYear.HasValue)
            {
                return true;
            }

            if (!IsFieldLocked(audio, MetadataField.Genres) &&
                effective.Genres is { Count: > 0 } &&
                (audio.Genres == null || audio.Genres.Length == 0 || audio.Genres.All(string.IsNullOrWhiteSpace)))
            {
                return true;
            }

            return false;
        }

        private static bool ApplyEffectiveAudioMetadata(Audio audio, AudioFallbackMetadata effective)
        {
            if (audio == null || effective == null || audio.IsLocked)
            {
                return false;
            }

            bool changed = false;

            if (ShouldUpdateAudioTitle(audio, effective.Title))
            {
                audio.Name = effective.Title;
                changed = true;
            }

            if (!string.IsNullOrWhiteSpace(effective.Album) && string.IsNullOrWhiteSpace(audio.Album))
            {
                audio.Album = effective.Album;
                changed = true;
            }

            if (effective.Artists is { Count: > 0 } && (audio.Artists == null || audio.Artists.Count == 0))
            {
                audio.Artists = effective.Artists;
                changed = true;
            }

            if (effective.AlbumArtists is { Count: > 0 } && (audio.AlbumArtists == null || audio.AlbumArtists.Count == 0))
            {
                audio.AlbumArtists = effective.AlbumArtists;
                changed = true;
            }

            if (effective.TrackNumber.HasValue && !audio.IndexNumber.HasValue)
            {
                audio.IndexNumber = effective.TrackNumber;
                changed = true;
            }

            if (effective.DiscNumber.HasValue && !audio.ParentIndexNumber.HasValue)
            {
                audio.ParentIndexNumber = effective.DiscNumber;
                changed = true;
            }

            if (effective.ProductionYear is > 0 and <= 9999)
            {
                int year = effective.ProductionYear.Value;
                if (!audio.ProductionYear.HasValue)
                {
                    audio.ProductionYear = year;
                    changed = true;
                }

                if (!audio.PremiereDate.HasValue)
                {
                    audio.PremiereDate = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                    changed = true;
                }
            }

            if (!IsFieldLocked(audio, MetadataField.Genres) &&
                effective.Genres is { Count: > 0 } &&
                (audio.Genres == null || audio.Genres.Length == 0 || audio.Genres.All(string.IsNullOrWhiteSpace)))
            {
                audio.Genres = effective.Genres.ToArray();
                changed = true;
            }

            return changed;
        }

        private static bool ShouldUpdateAudioTitle(Audio audio, string candidateTitle)
        {
            if (audio == null || string.IsNullOrWhiteSpace(candidateTitle) || IsFieldLocked(audio, MetadataField.Name))
            {
                return false;
            }

            if (string.Equals(audio.Name, candidateTitle, StringComparison.Ordinal))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(audio.Name))
            {
                return true;
            }

            var fileNameWithoutExt = !string.IsNullOrWhiteSpace(audio.Path)
                ? Path.GetFileNameWithoutExtension(audio.Path)
                : null;
            return !string.IsNullOrWhiteSpace(fileNameWithoutExt) &&
                   string.Equals(audio.Name, fileNameWithoutExt, StringComparison.Ordinal);
        }

        private static bool IsFieldLocked(BaseItem item, MetadataField field)
        {
            return item?.LockedFields != null && item.LockedFields.Contains(field);
        }

        private static bool NeedsParentAlbumSync(
            Audio audio, AudioFallbackMetadata effective, ILibraryManager libraryManager)
        {
            if (audio == null || libraryManager == null)
            {
                return false;
            }

            var album = FindParent<MusicAlbum>(audio, libraryManager);
            if (album == null || album.IsLocked)
            {
                return false;
            }

            var desiredAlbumArtists = NormalizeList(audio.AlbumArtists) ?? effective?.AlbumArtists;
            if (desiredAlbumArtists is { Count: > 0 } &&
                (album.AlbumArtists == null || desiredAlbumArtists.Any(a => !album.AlbumArtists.Contains(a, StringComparer.OrdinalIgnoreCase))))
            {
                return true;
            }

            var desiredArtists = NormalizeList(audio.Artists) ?? effective?.Artists;
            if (desiredArtists is { Count: > 0 } &&
                (album.Artists == null || desiredArtists.Any(a => !album.Artists.Contains(a, StringComparer.OrdinalIgnoreCase))))
            {
                return true;
            }

            return false;
        }

        public static async Task<bool> SyncAudioRelationshipsAsync(
            Audio audio, ILibraryManager libraryManager, CancellationToken cancellationToken, ILogger logger = null)
        {
            if (audio == null || libraryManager == null)
            {
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();

            // 1. 同步单曲的 People（保留非 Artist/AlbumArtist 角色，仅在新增时写库）
            bool audioPeopleChanged = UpdateItemPeople(audio, audio.AlbumArtists, audio.Artists, libraryManager, logger);

            // 2. 确保所有关联的艺术家实体在库中已激活
            EnsureArtistEntitiesExist(audio.AlbumArtists, audio.Artists, libraryManager, logger);

            // 3. 同步父级 MusicAlbum 的艺术家、年份、流派与 People
            var album = FindParent<MusicAlbum>(audio, libraryManager, logger);
            if (album == null || album.IsLocked)
            {
                return audioPeopleChanged;
            }

            var gate = GetAlbumLock(album.Id);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var currentAlbum = libraryManager.GetItemById(album.Id) as MusicAlbum ?? album;
                if (currentAlbum.IsLocked)
                {
                    return audioPeopleChanged;
                }

                bool albumChanged = false;

                var parentArtist = FindParent<MusicArtist>(currentAlbum, libraryManager, logger);
                var incomingAlbumArtists = NormalizeList(audio.AlbumArtists);
                if ((incomingAlbumArtists == null || incomingAlbumArtists.Count == 0) &&
                    !string.IsNullOrWhiteSpace(parentArtist?.Name))
                {
                    incomingAlbumArtists = new List<string> { parentArtist.Name.Trim() };
                }

                if (incomingAlbumArtists is { Count: > 0 })
                {
                    var mergedAlbumArtists = (currentAlbum.AlbumArtists ?? Array.Empty<string>())
                        .Concat(incomingAlbumArtists)
                        .Where(a => !string.IsNullOrWhiteSpace(a))
                        .Select(a => a.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    if (currentAlbum.AlbumArtists == null ||
                        !currentAlbum.AlbumArtists.SequenceEqual(mergedAlbumArtists, StringComparer.Ordinal))
                    {
                        currentAlbum.AlbumArtists = mergedAlbumArtists;
                        albumChanged = true;
                    }
                }

                var incomingArtists = NormalizeList(audio.Artists) ?? incomingAlbumArtists;
                if (incomingArtists is { Count: > 0 })
                {
                    var mergedArtists = (currentAlbum.Artists ?? Array.Empty<string>())
                        .Concat(incomingArtists)
                        .Where(a => !string.IsNullOrWhiteSpace(a))
                        .Select(a => a.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    if (currentAlbum.Artists == null ||
                        !currentAlbum.Artists.SequenceEqual(mergedArtists, StringComparer.Ordinal))
                    {
                        currentAlbum.Artists = mergedArtists;
                        albumChanged = true;
                    }
                }

                if (!currentAlbum.ProductionYear.HasValue && audio.ProductionYear.HasValue)
                {
                    currentAlbum.ProductionYear = audio.ProductionYear;
                    albumChanged = true;
                }

                if (!currentAlbum.PremiereDate.HasValue && audio.PremiereDate.HasValue)
                {
                    currentAlbum.PremiereDate = audio.PremiereDate;
                    albumChanged = true;
                }

                if (!IsFieldLocked(currentAlbum, MetadataField.Genres) &&
                    (currentAlbum.Genres == null || currentAlbum.Genres.Length == 0) &&
                    audio.Genres != null && audio.Genres.Length > 0)
                {
                    currentAlbum.Genres = audio.Genres
                        .Where(g => !string.IsNullOrWhiteSpace(g))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    albumChanged = true;
                }

                if (albumChanged)
                {
                    UpdateItemPeople(currentAlbum, currentAlbum.AlbumArtists, currentAlbum.Artists, libraryManager, logger);
                    await currentAlbum.UpdateToRepositoryAsync(ItemUpdateType.MetadataImport, cancellationToken).ConfigureAwait(false);
                    logger?.LogInformation("Synced album artists ({AlbumArtists}) for album {AlbumName}",
                        string.Join(", ", currentAlbum.AlbumArtists ?? Array.Empty<string>()), currentAlbum.Name);
                }

                return audioPeopleChanged || albumChanged;
            }
            finally
            {
                gate.Release();
            }
        }

        private static bool UpdateItemPeople(
            BaseItem item,
            IReadOnlyList<string> albumArtists,
            IReadOnlyList<string> artists,
            ILibraryManager libraryManager,
            ILogger logger)
        {
            if (item == null || libraryManager == null || !item.SupportsPeople || IsFieldLocked(item, MetadataField.Cast))
            {
                return false;
            }

            var incoming = new List<PersonInfo>();
            if (albumArtists != null)
            {
                foreach (var albumArtist in albumArtists)
                {
                    if (!string.IsNullOrWhiteSpace(albumArtist))
                    {
                        PeopleHelper.AddPerson(incoming, new PersonInfo
                        {
                            Name = albumArtist.Trim(),
                            Type = PersonKind.AlbumArtist
                        });
                    }
                }
            }

            if (artists != null)
            {
                foreach (var artist in artists)
                {
                    if (!string.IsNullOrWhiteSpace(artist))
                    {
                        PeopleHelper.AddPerson(incoming, new PersonInfo
                        {
                            Name = artist.Trim(),
                            Type = PersonKind.Artist
                        });
                    }
                }
            }

            if (incoming.Count == 0)
            {
                return false;
            }

            // UpdatePeople 为全量替换语义：先读取并保留已有人员（如 Composer、Lyricist、Performer 等），
            // 且仅在确实缺少目标 Artist/AlbumArtist 时才调用 UpdatePeople。
            IReadOnlyList<PersonInfo> existing = null;
            try
            {
                existing = libraryManager.GetPeople(item);
            }
            catch (Exception ex) when (ex is NotSupportedException || ex is NotImplementedException || ex is NullReferenceException)
            {
                logger?.LogDebug(ex, "GetPeople not available for {Name}; proceeding with incoming people", item.Name);
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "Failed to read existing people for {Name}", item.Name);
            }

            var merged = new List<PersonInfo>();
            if (existing != null)
            {
                foreach (var person in existing)
                {
                    if (person != null && !string.IsNullOrWhiteSpace(person.Name))
                    {
                        PeopleHelper.AddPerson(merged, person);
                    }
                }
            }

            int beforeCount = merged.Count;
            foreach (var person in incoming)
            {
                PeopleHelper.AddPerson(merged, person);
            }

            if (existing != null && merged.Count == beforeCount)
            {
                return false;
            }

            try
            {
                libraryManager.UpdatePeople(item, merged);
                return true;
            }
            catch (Exception ex) when (ex is NotSupportedException || ex is NotImplementedException || ex is NullReferenceException)
            {
                logger?.LogDebug(ex, "Skipping UpdatePeople for {Name} in non-full library environment", item.Name);
                return false;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to update people for {Name}", item.Name);
                return false;
            }
        }

        private static void EnsureArtistEntitiesExist(
            IReadOnlyList<string> albumArtists,
            IReadOnlyList<string> artists,
            ILibraryManager libraryManager,
            ILogger logger)
        {
            if (libraryManager == null)
            {
                return;
            }

            var allNames = (albumArtists ?? Array.Empty<string>())
                .Concat(artists ?? Array.Empty<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var name in allNames)
            {
                try
                {
                    _ = libraryManager.GetArtist(name);
                }
                catch (Exception ex) when (ex is NotSupportedException || ex is NotImplementedException || ex is NullReferenceException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger?.LogDebug(ex, "Failed to resolve artist entity for {ArtistName}", name);
                }
            }
        }

        private static AudioFallbackMetadata ResolveFallbackMetadata(
            Audio audio, ILibraryManager libraryManager, ILogger logger = null)
        {
            var result = new AudioFallbackMetadata();
            if (audio == null)
            {
                return result;
            }

            // 1. 从文件名解析候选音轨号、碟片号、单曲歌手、歌名（如 "03 - 卓依婷 - 婉君.strm" 或 "卓依婷 - 婉君.strm"）
            var fileNameWithoutExt = !string.IsNullOrWhiteSpace(audio.Path)
                ? Path.GetFileNameWithoutExtension(audio.Path)
                : null;
            ParseTrackFileName(fileNameWithoutExt, out var parsedDisc, out var parsedTrack, out var parsedArtists, out var parsedTitle);

            result.DiscNumber = parsedDisc;
            result.TrackNumber = parsedTrack;
            result.Title = parsedTitle;
            result.Artists = parsedArtists;

            // 2. 优先从 Jellyfin 库实体层级（MusicArtist / MusicAlbum）获取标准专辑与专辑艺术家
            var parentAlbum = FindParent<MusicAlbum>(audio, libraryManager, logger);
            var parentArtist = FindParent<MusicArtist>(audio, libraryManager, logger);

            if (parentAlbum != null && !string.IsNullOrWhiteSpace(parentAlbum.Name))
            {
                result.Album = parentAlbum.Name.Trim();
            }

            if (parentArtist != null && !string.IsNullOrWhiteSpace(parentArtist.Name))
            {
                result.AlbumArtists = new List<string> { parentArtist.Name.Trim() };
            }
            else if (parentAlbum?.AlbumArtists is { Count: > 0 })
            {
                result.AlbumArtists = NormalizeList(parentAlbum.AlbumArtists);
            }

            // 3. 若脱离库实体上下文（如离线缓存恢复），且路径与文件名符合 "专辑艺术家/专辑/歌手 - 歌名.strm" 结构时从路径回退
            if ((string.IsNullOrWhiteSpace(result.Album) || result.AlbumArtists == null || result.AlbumArtists.Count == 0) &&
                !string.IsNullOrWhiteSpace(audio.Path) && Path.IsPathRooted(audio.Path))
            {
                ExtractFolderHierarchyFallback(
                    audio.Path,
                    parsedArtists,
                    out var pathAlbumArtist,
                    out var pathAlbum,
                    out var pathDiscNumber,
                    logger);

                if (string.IsNullOrWhiteSpace(result.Album) && !string.IsNullOrWhiteSpace(pathAlbum))
                {
                    result.Album = pathAlbum;
                }

                if ((result.AlbumArtists == null || result.AlbumArtists.Count == 0) && !string.IsNullOrWhiteSpace(pathAlbumArtist))
                {
                    result.AlbumArtists = new List<string> { pathAlbumArtist };
                }

                if (!result.DiscNumber.HasValue && pathDiscNumber.HasValue)
                {
                    result.DiscNumber = pathDiscNumber;
                }
            }

            // 4. Artists 与 AlbumArtists 互为兜底
            if ((result.Artists == null || result.Artists.Count == 0) && result.AlbumArtists is { Count: > 0 })
            {
                result.Artists = new List<string>(result.AlbumArtists);
            }
            else if ((result.AlbumArtists == null || result.AlbumArtists.Count == 0) && result.Artists is { Count: > 0 })
            {
                result.AlbumArtists = new List<string>(result.Artists);
            }

            return result;
        }

        private static void ParseTrackFileName(
            string fileNameWithoutExt,
            out int? discNumber,
            out int? trackNumber,
            out List<string> artists,
            out string title)
        {
            discNumber = null;
            trackNumber = null;
            artists = null;
            title = null;

            if (string.IsNullOrWhiteSpace(fileNameWithoutExt))
            {
                return;
            }

            string working = fileNameWithoutExt.Trim();
            var match = LeadingTrackRegex.Match(working);
            if (match.Success)
            {
                if (match.Groups["disc"].Success &&
                    int.TryParse(match.Groups["disc"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int disc) &&
                    disc > 0)
                {
                    discNumber = disc;
                }

                if (int.TryParse(match.Groups["track"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int track) &&
                    track > 0)
                {
                    trackNumber = track;
                    working = match.Groups["rest"].Value.Trim();
                }
            }

            int separatorIndex = working.IndexOf(" - ", StringComparison.Ordinal);
            if (separatorIndex > 0)
            {
                var artistPart = working.Substring(0, separatorIndex).Trim();
                var titlePart = working.Substring(separatorIndex + 3).Trim();
                if (!string.IsNullOrWhiteSpace(artistPart) && !string.IsNullOrWhiteSpace(titlePart))
                {
                    artists = SplitArtists(artistPart);
                    title = titlePart;
                    return;
                }
            }

            if (trackNumber.HasValue && !string.IsNullOrWhiteSpace(working))
            {
                title = working;
            }
        }

        private static void ExtractFolderHierarchyFallback(
            string strmPath,
            List<string> parsedFileArtists,
            out string albumArtist,
            out string album,
            out int? discNumber,
            ILogger logger = null)
        {
            albumArtist = null;
            album = null;
            discNumber = null;

            try
            {
                var dir = Path.GetDirectoryName(strmPath);
                if (string.IsNullOrWhiteSpace(dir))
                {
                    return;
                }

                var currentDirName = Path.GetFileName(dir);
                var parentDir = Path.GetDirectoryName(dir);

                var discMatch = !string.IsNullOrWhiteSpace(currentDirName)
                    ? MultiDiscFolderRegex.Match(currentDirName.Trim())
                    : Match.Empty;
                if (discMatch.Success && !string.IsNullOrWhiteSpace(parentDir))
                {
                    if (int.TryParse(discMatch.Groups["disc"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int disc) &&
                        disc > 0)
                    {
                        discNumber = disc;
                    }

                    dir = parentDir;
                    currentDirName = Path.GetFileName(dir);
                    parentDir = Path.GetDirectoryName(dir);
                }

                var grandParentDirName = !string.IsNullOrWhiteSpace(parentDir)
                    ? Path.GetFileName(parentDir)
                    : null;

                if (string.IsNullOrWhiteSpace(currentDirName) || string.IsNullOrWhiteSpace(grandParentDirName))
                {
                    return;
                }

                // 当文件名中的歌手与祖父目录名匹配，或祖父目录名出现在文件名歌手列表中时，确认符合 "专辑艺术家/专辑/歌曲" 标准层级
                bool matchesFileArtist = parsedFileArtists != null &&
                    parsedFileArtists.Any(a => string.Equals(a, grandParentDirName.Trim(), StringComparison.OrdinalIgnoreCase));

                if (matchesFileArtist)
                {
                    albumArtist = grandParentDirName.Trim();
                    album = currentDirName.Trim();
                }
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "Failed to extract folder hierarchy fallback from path: {Path}", strmPath);
            }
        }

        internal static T FindParent<T>(BaseItem item, ILibraryManager libraryManager, ILogger logger = null) where T : Folder
        {
            if (item == null)
            {
                return null;
            }

            var manager = libraryManager ?? BaseItem.LibraryManager;
            if (manager == null)
            {
                return null;
            }

            var visited = new HashSet<Guid>();
            var currentId = item.ParentId;
            while (currentId != Guid.Empty && visited.Add(currentId))
            {
                BaseItem parent;
                try
                {
                    parent = manager.GetItemById(currentId);
                }
                catch (Exception ex)
                {
                    logger?.LogDebug(ex, "Failed to resolve parent {ParentId} for item {ItemId}", currentId, item.Id);
                    return null;
                }

                if (parent == null)
                {
                    return null;
                }

                if (parent is T matched)
                {
                    return matched;
                }

                currentId = parent.ParentId;
            }

            return null;
        }

        private static string NormalizeString(string value)
        {
            return MediaInfoCache.NormalizeText(value);
        }

        private static List<string> NormalizeList(IEnumerable<string> values)
        {
            return MediaInfoCache.NormalizeStringList(values, ArtistSplitDelimiters);
        }

        private static List<string> SplitArtists(string raw)
        {
            return string.IsNullOrWhiteSpace(raw)
                ? new List<string>()
                : MediaInfoCache.NormalizeStringList(new[] { raw }, ArtistSplitDelimiters) ?? new List<string>();
        }
    }
}
