# StrmTool for Jellyfin

Jellyfin 插件，用于从 strm 文件中提取媒体技术信息（codec、分辨率、字幕），加速 strm 媒体文件的起播速度。

> **推荐搭配**：如果你还需要从 OpenList/Alist 网盘批量生成 strm 文件，可参考本人另外一个项目：[openlist-strm](https://github.com/jinlin-teck/openlist-strm)——从 OpenList/Alist 目录生成 .strm 文件的轻量服务，带 WebUI，配合本插件可在 Jellyfin 上完美播放 strm 媒体文件。

**v2.5.0**：新增缓存导出/恢复计划任务、数据库化库扫描，并增强缓存并发安全、字幕路径恢复和任务取消处理。

## 核心功能

1. **媒体信息提前提取**：在 strm 文件入库后立即向远程服务器请求并获取媒体技术信息（音视频编码、分辨率、字幕等）
2. **自动提取新文件**：新入库的 strm 文件可开启功能后自动在后台提取媒体信息，无需手动介入
3. **媒体信息缓存**：自动缓存提取的媒体信息为同名 `.strmtool.json` 文件（保存在 strm 文件同目录下），下次提取时可直接导入；默认启用 STRM 内容指纹校验，strm 内容变化后缓存自动失效并重新探测
4. **计划任务支持**：提供提取、导出缓存和从缓存恢复三个计划任务，支持手动触发和定时执行
5. **配置界面**：提供插件设置页面，可调整自动提取开关、刷新延迟时间、持久化缓存开关和最大并发数以及强制刷新策略

本版本面向 Jellyfin 12.1.0，使用 .NET 10 构建；不声明兼容 Jellyfin 10.11.x 或其他版本。

## 安装方法

1. 在 Jellyfin 的 `plugin` 目录下新建文件夹 `StrmTool`
2. 将编译生成的 `StrmTool.dll` 放入该文件夹
3. 重启 Jellyfin 服务

## 使用方法

### 插件功能设置

在插件详情页点击"设置"按钮，可以调整以下配置项：

- **自动提取新入库 strm 文件**：启用后，新增的 strm 文件会自动在后台执行媒体信息提取（默认：启用）
- **启用媒体信息缓存**：启用后，提取的媒体信息会保存为 xxx.strmtool.json 文件（与 strm 文件同目录），避免重复探测（默认：启用）
- **校验 STRM 内容指纹**：启用时，当 strm 文件指向的链接变动时旧缓存自动失效并重新探测；仅在网盘/NAS 路径迁移且确认媒体文件未变时关闭，以复用旧缓存。关闭后读取不会改写缓存或指纹，重新启用时指纹缺失或不匹配的缓存仍会失效并重新探测（默认：启用）
- **刷新延迟（毫秒）**：每次刷新媒体信息后等待的毫秒数，用于避免对远程服务器造成压力（默认：1000ms）
- **最大并发数（需重启生效）**：媒体信息提取任务的最大并发数，范围: 1-50（默认：5）。修改后需要重启 Jellyfin 才能生效。
- **强制刷新选项**：
  - **无视是否已有媒体流**：勾选后无论是否已有媒体信息都执行刷新（仍可利用缓存）
  - **无视缓存**：勾选后直接从远程服务器获取，忽略缓存文件（仍会判断是否已有媒体信息）

**注意**：除"最大并发数"外，其他配置修改后会立即生效（在下次任务执行时自动应用），无需重启 Jellyfin。

### 计划任务

1. 进入 Jellyfin 后台 → 计划任务
2. 找到 `Strm Tool` 分类下的`Extract Strm Media Info`（提取Strm媒体信息）任务
3. 可手动运行或设置定时触发
4. 提取任务通过设置页的强制刷新选项控制探测和缓存利用策略。
5. **导出Strm媒体信息缓存**：仅为尚无缓存的条目保存库内媒体信息，不访问远程媒体，也不覆盖任何已有缓存（包括指纹失效或损坏的缓存）。运行前请确认库内信息对应当前媒体源；源已变更时应先运行提取任务重新探测，不应通过删除缓存后导出来更新指纹。
6. **从缓存恢复Strm媒体信息**：从有效缓存恢复缺失的媒体流和元数据，不访问远程媒体；禁用缓存或启用“无视缓存”时不执行。这两个新任务默认没有自动触发器。

## 注意事项

- 请根据使用的 Jellyfin 版本选择对应版本的插件
- v1.0.0.3 相比之前版本不会调用任何第三方元数据服务，已有的元数据（标题、描述、海报等）不会被修改
- 媒体信息缓存文件格式为 `strm_filename.strmtool.json`，位于 strm 文件同目录
- 本地外部字幕仅缓存与 strm 同目录的文件名，读取时还原绝对路径；不修改库中原始字幕对象。其他目录的本地字幕不写入新缓存，需由 Jellyfin 重新扫描发现；远程字幕 URL 保留
- 默认通过 STRM 内容指纹校验缓存有效性；关闭校验会跳过媒体源变更检查，请确认媒体未变。读取不会迁移指纹，重新启用后仍按原指纹校验

---

# StrmTool for Jellyfin

Jellyfin plugin for extracting media technical information (codec, resolution, subtitles) from strm files to accelerate playback startup speed.

> **Recommended companion**: If you also need to batch-generate strm files from OpenList/Alist, check out my other project: [openlist-strm](https://github.com/jinlin-teck/openlist-strm) — a lightweight service with WebUI that generates .strm files from OpenList/Alist directories. Combined with this plugin, you can play strm media files perfectly on Jellyfin.

**v2.5.0**: Adds cache export/restoration tasks and database-backed library scanning, with safer concurrent cache access, subtitle path restoration, and task cancellation handling.

## Core Features

1. **Early Media Information Extraction**: Immediately requests and obtains media technical information (audio/video codec, resolution, subtitles, etc.) from remote servers after strm files are added to the library
2. **Automatic Extraction for New Files**: Newly added strm files can automatically extract media information in the background when the feature is enabled, no manual intervention required
3. **Media Information Caching**: Automatically caches extracted media information as `.strmtool.json` files with the same name (saved in the same directory as the strm file), allowing direct import during next extraction; STRM content fingerprint validation is enabled by default, invalidating the cache and triggering re-probing when the strm content changes
4. **Scheduled Task Support**: Provides extraction, cache export, and cache restoration tasks with manual or scheduled execution
5. **Configuration Interface**: Provides a plugin settings page to adjust automatic extraction toggle, refresh delay, persistent cache toggle, maximum concurrency, and force refresh strategies

This version targets Jellyfin 12.1.0 and is built with .NET 10. Compatibility with Jellyfin 10.11.x or other versions is not claimed.

## Installation

1. Create a new folder `StrmTool` in Jellyfin's `plugin` directory
2. Place the compiled `StrmTool.dll` into this folder
3. Restart the Jellyfin service

## Usage

### Plugin Settings

Click the "Settings" button on the plugin details page to adjust the following configuration items:

- **Automatically extract media info for new strm files**: When enabled, newly added strm files will automatically perform media information extraction in the background (Default: Enabled)
- **Enable media info caching**: When enabled, extracted media information will be saved as xxx.strmtool.json files (in the same directory as the strm file) to avoid repeated probing (Default: Enabled)
- **Verify STRM content fingerprint**: When enabled, URL changes invalidate the cache and trigger re-probing. Disable only after confirming the media is unchanged during a NAS/cloud drive path move. Cache reads do not modify the cache or its fingerprint; re-enabling validation rejects missing or mismatched fingerprints and triggers re-probing (Default: Enabled)
- **Refresh delay (ms)**: Milliseconds to wait after each media info refresh to avoid overwhelming remote servers (Default: 1000ms)
- **Maximum concurrent extractions (restart required)**: Maximum concurrency for media info extraction tasks, range: 1-50 (Default: 5). Requires Jellyfin restart to take effect.
- **Force Refresh Options**:
  - **Ignore existing media streams**: When enabled, will always execute refresh regardless of whether media stream info already exists (cache can still be used)
  - **Ignore cache**: When enabled, will always fetch from remote server directly, ignoring cache files (will still check if media streams exist)

**Note**: Except for "Maximum concurrent extractions", all other configuration changes take effect immediately (automatically applied on next task execution) without restarting Jellyfin.

### Scheduled Tasks

1. Go to Jellyfin admin → Scheduled Tasks
2. Find the `Extract Strm Media Info` task under the `Strm Tool` category
3. Can be run manually or set to trigger on a schedule
4. The extraction task uses the force refresh options to control probing and cache usage.
5. **Export Strm Media Info Cache** only fills missing cache files from library metadata, without remote probing. It never overwrites existing caches, including stale or corrupt ones. Confirm library metadata matches the current source before exporting; after a source change, re-probe rather than deleting the cache and exporting old metadata with a new fingerprint.
6. **Restore Strm Media Info from Cache** restores missing streams and metadata from valid caches without probing. It does nothing when caching is disabled or cache reads are bypassed. Both new tasks have no default triggers.

## Notes

- Please select the corresponding plugin version based on your Jellyfin version
- Compared to previous versions, v1.0.0.3 does not call any third-party metadata services, and existing metadata (title, description, posters, etc.) will not be modified
- Media info cache file format is `strm_filename.strmtool.json`, located in the same directory as the strm file
- Local external subtitles in the STRM directory are cached as filenames and restored to absolute paths on read, without mutating the original stream objects. Local subtitles in other directories are omitted from new caches and must be rediscovered by Jellyfin; remote subtitle URLs are preserved
- STRM content fingerprint validation is enabled by default. Disabling it bypasses source-change checks, so confirm the media is unchanged. Reads do not migrate fingerprints; re-enabling validation checks the original fingerprint
