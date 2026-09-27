# StrmTool for Jellyfin

[中文文档 (README.md)](./README.md)

**StrmTool** is a `.strm` media enhancement plugin built for Jellyfin. By pre-extracting technical media information (video codec, resolution, HDR, audio tracks, subtitles, etc.), persisting local caches, protecting metadata from being reset, and enabling direct playback for `.strm` audio files, it dramatically improves library presentation and playback startup speed for cloud and remote media streams.

> **Current Version**: `v2.6.1` (Targets **Jellyfin 12.1.0** / .NET 10; for Emby servers, see [StrmTool for Emby](https://github.com/jinlin-teck/StrmTool))
>
> **Recommended Companion**: Need to batch-generate `.strm` files from OpenList / Alist cloud drives? Check out my companion project [openlist-strm](https://github.com/jinlin-teck/openlist-strm) — a lightweight `.strm` generator service with WebUI that pairs seamlessly with this plugin.

---

## ✨ Key Features

### 1. 🚀 Pre-Extraction & Instant Playback Startup
- **Automatic Background Extraction**: Automatically extracts video/audio codecs, resolution, frame rate, bitrate, audio channels, and embedded/external subtitles when new `.strm` files are added to the library. Media badges (`4K`, `HEVC`, `Dolby Vision`, `Atmos`, etc.) show up immediately on item detail pages.
- **Faster Playback Startup**: Eliminates on-the-fly FFprobe delays when starting playback, enabling near-instant stream startup.
- **Full Media Type Coverage**: Supports Movies, Episodes, Music Videos, and Audio `.strm` items.
- **Non-Intrusive to Metadata**: Only probes technical media streams and specs — **never overwrites** scraped metadata such as titles, overviews, cast, or artwork.

### 2. 💾 Smart Local Caching & Content Fingerprinting
- **Sidecar Cache Files**: Automatically saves extracted technical info to `{filename}.strmtool.json` alongside each `.strm` file. Subsequent scans or library rebuilds import directly from local cache in milliseconds, avoiding repeated remote probes and cloud drive rate limits.
- **STRM Content Fingerprint Validation**: Computes and verifies a SHA256 fingerprint of the `.strm` content by default. When the URL inside a `.strm` file changes to a new media source, stale caches are automatically invalidated and re-probed.
- **Automatic Corrupt Cache Isolation**: Corrupt or invalid `.strmtool.json` files are automatically renamed to `.bak` so they never block future extraction or export runs.

### 3. 🛡️ Metadata Reset Protection & Auto-Recovery
- **Fixes the "Tiny File Size" Issue**: When Jellyfin refreshes or plays a `.strm` item, it often resets the item's `Size` to the tiny size of the local `.strm` text file (under 1 KB) or drops resolution and bitrate fields.
- **Seamless Background Recovery**: Listens for item update events and automatically restores file size, runtime, container, resolution, and bitrate from the local `.strmtool.json` cache whenever a reset is detected.

### 4. 🎵 Direct STRM Music Playback, Tag Extraction & External Lyrics
- **Fixes Native `.strm` Audio Playback**: Resolves Jellyfin's native limitation where `.strm` audio items fail to substitute `ShortcutPath`, allowing web browsers and clients to stream remote `.strm` music tracks directly.
- **Skips Redundant Playback Probes**: Audio `.strm` items with already-extracted audio streams bypass redundant `FullRefresh` remote probes during playback startup for smoother track switching.
- **Audio Tag Extraction & Folder Hierarchy Fallback**: Automatically extracts track title, album, artists, album artists, track/disc numbers, year, and genres from remote audio tags, falls back to standard `AlbumArtist/Album/Track` folder and filename conventions when tags are absent or during offline cache restore, and syncs parent album and artist relationships.
- **Automatic External Lyrics**: Automatically discovers and attaches same-name `.lrc`, `.elrc`, and `.txt` lyric files located in the same folder as the `.strm` audio file, and cleans up stale lyric entries when local files are removed.

### 5. 📦 Batch Cache Export & Offline Restore Tasks
- Provides dedicated **Export Cache** and **Restore from Cache** scheduled tasks (100% local disk operations with zero remote network requests), ideal for initializing caches on existing libraries or restoring all media specs after a server migration or library rebuild.

---

## 📦 Installation

> ⚠️ **Compatibility**: Version `v2.6.1` is built specifically for **Jellyfin 12.1.0** (.NET 10). Compatibility with Jellyfin 10.11.x or earlier versions is not supported. Please choose the plugin build matching your Jellyfin server version.

### Method 1: Install via Jellyfin Plugin Repository (Recommended, Supports Updates)

1. Go to **Jellyfin Dashboard → Plugins** and click **Manage Repositories** in the top-right corner.
2. Click **+ New Repository** in the top-left corner, fill in the following details, and click **Save**:
   - **Repository Name**: `StrmTool`
   - **Repository URL**:
     ```text
     https://raw.githubusercontent.com/jinlin-teck/StrmTool-Jellyfin/main/manifest.json
     ```
3. Open the plugin **Catalog**, find **StrmTool** under the `General` category, and click **Install**.
4. **Restart the Jellyfin server** to activate the plugin.

### Method 2: Manual Installation

1. Download `StrmTool.dll` (or extract `StrmTool_x.x.x.x.zip`) from the [Releases](https://github.com/jinlin-teck/StrmTool-Jellyfin/releases) page.
2. Create a `StrmTool` folder inside your Jellyfin `plugins` directory (e.g., `/config/plugins/StrmTool` in Docker).
3. Copy the files into the `StrmTool` folder and restart the Jellyfin server.
4. Go to **Dashboard → Plugins** and verify that `StrmTool` shows as `Active`.

---

## ⚙️ Plugin Configuration

Open **Jellyfin Dashboard → Plugins → StrmTool** to configure the plugin.

> 💡 **Hot Reload**: All settings take effect immediately upon saving, **except "Maximum concurrent extractions"**, which requires a Jellyfin restart.

### Automation & Caching

| Setting | Default | Description |
| :--- | :---: | :--- |
| **Automatically extract media info for new strm files** | Enabled | Automatically queues newly added `.strm` files for background media info extraction without running scheduled tasks manually. |
| **Enable media info caching** | Enabled | Saves extracted media streams and technical metadata to `{filename}.strmtool.json` in the same folder for instant reuse. |
| **Verify STRM content fingerprint** | Enabled | Invalidates the cache and re-probes when the URL inside a `.strm` file changes. Disable temporarily only when migrating NAS/cloud mount paths where the underlying media file is unchanged. |

### Performance & Concurrency

| Setting | Default | Range | Description |
| :--- | :---: | :---: | :--- |
| **Extraction delay interval** | `1000` ms | `0 - 20000` ms | Milliseconds to wait after each remote probe to smooth request traffic and prevent cloud/WebDAV rate-limiting (cache hits are not delayed). |
| **Metadata restore timeout** | `5` min | `1 - 30` min | Maximum time to wait for background metadata restoration when a `.strm` item's file size or metadata is reset. |
| **Maximum concurrent extractions** | `5` | `1 - 50` | Maximum number of concurrent extraction workers (**requires Jellyfin restart to take effect**). |

### Force Refresh Options (Maintenance & Troubleshooting)

> ⚠️ **Use with Caution**: Force refresh significantly increases remote network probing requests and may trigger rate limits on cloud drives or WebDAV services. Keep these disabled during normal operation.

| Setting | Default | Description |
| :--- | :---: | :--- |
| **Ignore existing media streams** | Disabled | Refreshes all `.strm` items even if media stream info already exists in Jellyfin (valid local caches will still be used unless cache is also ignored). |
| **Ignore cache and force remote probe** | Disabled | Bypasses local `.strmtool.json` cache files and probes remote servers directly (by default only applies to items missing media streams; enable both options to force re-probe the entire library). |

---

## 🕒 Scheduled Tasks

Navigate to **Jellyfin Dashboard → Scheduled Tasks** under the **`StrmTool`** category. None of the tasks have automatic triggers by default; you can run them manually or add custom schedule triggers as needed:

| Task Name | Remote Network Requests | Purpose & Behavior |
| :--- | :---: | :--- |
| **Extract Strm Media Info** | On-demand (Cache first) | **Primary Task**. Scans the library for `.strm` items missing media streams or with mismatched cache fingerprints. Imports from valid local cache in milliseconds, or probes the remote server and writes a new cache file. Recommended for a daily off-peak schedule. |
| **Export Strm Media Info Cache** | **No** (100% Local) | **Initialize Caches**. Exports existing media streams and technical metadata from the Jellyfin database into `.strmtool.json` files for items that do not yet have a cache file. Never overwrites existing caches. |
| **Restore Strm Media Info from Cache** | **No** (100% Local) | **Fast Recovery**. Batch-restores missing media streams, file size, resolution, runtime, bitrate, and local external lyrics from valid `.strmtool.json` cache files without any remote probing. |

---

## 💡 Common Workflows

### 1. Fresh Setup & Daily Use
Keep the default settings. Newly added `.strm` movies, shows, and music tracks will be extracted in the background and cached to `.strmtool.json`. If playback or library scans reset the file size, StrmTool automatically restores it in the background.

### 2. First-Time Setup on an Existing Library
1. Run **Export Strm Media Info Cache** once to save already-probed library items into local `.strmtool.json` files (zero network requests).
2. Run **Extract Strm Media Info** to probe and cache any remaining `.strm` items that still lack media info.

### 3. Rebuilding a Library or Migrating Mount Paths
- **Same URLs / Paths**: Keep your `.strmtool.json` files alongside your `.strm` files. After scanning the library, run **Restore Strm Media Info from Cache** (or **Extract Strm Media Info**) to restore all media streams and specs locally in seconds.
- **Changed Cloud Domain / Mount Path (Same Underlying Media Files)**:
  1. Disable **Verify STRM content fingerprint** in plugin settings and save.
  2. Run **Restore Strm Media Info from Cache** to reuse the existing caches without remote probing.
  3. *(Note: Reading caches while validation is disabled does not rewrite the stored fingerprint; re-enabling validation later will still reject mismatched fingerprints and trigger re-probing.)*

---

## 📌 Notes & FAQ

1. **Will external subtitles or external audio tracks be lost?**
   - No. StrmTool merges and preserves external subtitles and audio tracks discovered by Jellyfin when saving or restoring media streams.
   - Local external subtitles and lyrics (`.lrc`/`.elrc`/`.txt`) in the same folder as the `.strm` file are cached by filename only and automatically resolved back to absolute paths on read, making directory moves portable.
2. **What if I replace a media source with a higher quality version (e.g., 1080p → 4K) under the same `.strm` filename?**
   - As long as **Verify STRM content fingerprint** is enabled (default) and the URL inside the `.strm` file changes, running **Extract Strm Media Info** will detect the fingerprint mismatch, re-probe the remote source, and update resolution, bitrate, runtime, and file size.
   - If your `.strm` file uses a static redirect URL whose text content never changes when the underlying file changes, temporarily enable both **Ignore existing media streams** and **Ignore cache and force remote probe**, run the extraction task, and then turn both options back off.
