# Emby 到 Jellyfin 媒体信息缓存迁移脚本

`migrate_emby_mediainfo.py` 用于将 Emby 分支 StrmTool 生成的 `*-mediainfo.json` 文件批量就地转换为本分支（Jellyfin 版）使用的 `*.strmtool.json` 缓存文件，从而在迁移至 Jellyfin 时直接复用已有媒体技术信息，省去重新发起远程 FFprobe 探测。

---

## 功能特性

1. **同目录就地转换（无损源数据）**
   - 递归扫描指定目录下的 `{basename}-mediainfo.json` 与同目录同名 `{basename}.strm`，在同目录生成 `{basename}.strmtool.json`。
   - **只读不改**原 `*-mediainfo.json` 与 `*.strm` 文件。
   - 采用同目录临时文件原子发布：默认使用硬链接保证目标已存在时绝不覆盖；指定 `--overwrite` 时使用 `os.replace`。
   - 多个输入映射到同一输出文件时，在写入前报错退出（包括 `--overwrite` / `--dry-run`）；重复的源文件与目标组合自动去重。

2. **内容指纹对齐 (`strmContentHash`)**
   - 默认使用 UTF-8，并识别 UTF-8 / UTF-16 / UTF-32 BOM；读取 `.strm` 首个非空白行并去除首尾空白，计算大写 SHA256 十六进制字符串，与插件的内容指纹校验对接。无法解码的文件计入错误，不中断其他文件迁移。

3. **Emby → Jellyfin 流结构深度映射**
   - **枚举数值化**：将 Emby 的字符串枚举 `Type`（`Audio`/`Video`/`Subtitle`/`EmbeddedImage`/`Data`/`Lyric`）或数值枚举（Emby 的 `Audio=1`/`Video=2`/`Subtitle=3`/`EmbeddedImage=4`/`Attachment=5`/`Data=6`）转换为 Jellyfin 的整数枚举（`0`~`5`）。
     字幕 `DeliveryMethod` 同样转换为整数枚举；无法识别的值不写入缓存。
   - **不兼容流过滤**：
     - 自动剔除 Emby 特有的 `"Attachment"` 流（如 MKV 内嵌字体附件，Jellyfin 的 `MediaStreamType` 无此枚举）。
     - 自动剔除纯音频中 Emby 以字幕形式导出的歌词流（内嵌 `Codec="text"`，以及外挂 `.lrc`/`.elrc`/`.txt`），再由脚本按磁盘文件补一条 `Type=5` 歌词流，避免同一歌词同时以字幕和歌词两条轨道出现。
   - **HDR / 杜比视界（Dolby Vision）补偿**：
     - HDR10 / HLG / HDR10+ 补齐 `ColorTransfer` 与 `VideoRangeType`（Jellyfin 实际按 `ColorTransfer` 计算范围；HDR10+ 另写入 `Hdr10PlusPresentFlag`）。
     - 自动将 Emby 私有字段 `ExtendedVideoSubType`（`DoviProfile50` / `DoviProfile76` / `DoviProfile81` 等 `DoviProfileXX`）映射为 Jellyfin `MediaStream` 计算所需的 `DvProfile`、`DvBlSignalCompatibilityId`、`BlPresentFlag`、`RpuPresentFlag`、`ElPresentFlag`。已显式支持 Profile 5 / 7.6 / 8.1 / 8.2 / 8.4，其余 Profile 保留原始 `DvProfile` 与兼容位，由 Jellyfin 计算；不会由脚本直接写成 SDR。
   - **空间音频（Dolby Atmos / DTS:X）补偿**：
     - 自动检测音轨 `Profile` / `Title` / `DisplayTitle` 中的 `Atmos` 或 `DTS:X` 标识并补齐 `Profile`（如 `Dolby Digital Plus + Dolby Atmos`、`Dolby TrueHD + Dolby Atmos`），让 Jellyfin 正确计算只读属性 `AudioSpatialFormat`。已有普通音轨 Profile 时也会补齐空间音频信息。
   - **外挂字幕与歌词索引重排**：
     - 保留同目录外挂字幕文件名，并按 Jellyfin `MergeExternalStreams` 规则将外挂流 `Index` 排在内封流之后。
     - 远程 URL 外挂字幕保留完整地址并标记 `IsExternalUrl`；指向绝对路径但不在同目录的外挂字幕会被丢弃。

4. **默认跳过纯音频与已有缓存**
   - 默认仅转换包含视频流的条目（电影、剧集、动画、MV 等），跳过纯音频文件（可通过 `--include-audio` 开启）。
   - 默认跳过已存在 `*.strmtool.json` 的条目，不覆盖已有缓存（可通过 `--overwrite` 强制覆盖）。

---

## 参数说明

```text
usage: migrate_emby_mediainfo.py [-h] [--output-dir OUTPUT_DIR] [--overwrite]
                                 [--include-audio] [--dry-run]
                                 [--workers WORKERS]
                                 paths [paths ...]
```

| 参数 | 说明 |
| :--- | :--- |
| `paths` | 必填，支持传入一个或多个目录路径（递归扫描）或单个 `*-mediainfo.json` 文件路径 |
| `--dry-run` | 仅扫描、校验并输出转换统计结果，**不写入任何文件** |
| `--output-dir DIR` | 将生成的 `*.strmtool.json` 按相对目录结构输出到指定目录，不写入源目录（用于隔离测试） |
| `--overwrite` | 覆盖已存在的 `*.strmtool.json`（默认跳过已存在文件） |
| `--include-audio` | 同时转换纯音频文件（默认跳过纯音频；若同目录存在 `.lrc/.elrc/.txt` 会自动附加 `Type=5` 歌词流）。**注意：不会迁移 Emby 的 `AudioMetadata` 标签，运行时会打印警告** |
| `--workers N` | 并发工作线程数（默认 `8`，上限 `64`） |

---

## 常用示例

### 1. 预览统计（推荐先执行，不写入任何文件）

```bash
python3 scripts/migrate_emby_mediainfo.py --dry-run "/opt/appdata/emby/strm"
```

### 2. 对单个剧集/电影目录就地转换

```bash
python3 scripts/migrate_emby_mediainfo.py "/opt/appdata/emby/strm/NAS/电视剧/舌尖上的中国 (2012)  [tmdb-60781]"
```

### 3. 对整个 STRM 库批量就地转换

```bash
python3 scripts/migrate_emby_mediainfo.py "/opt/appdata/emby/strm"
```

### 4. 隔离输出到临时目录进行测试（不触碰源目录）

```bash
python3 scripts/migrate_emby_mediainfo.py \
  --output-dir "/tmp/strmtool-preview" \
  "/opt/appdata/emby/strm/NAS/电视剧/舌尖上的中国 (2012)  [tmdb-60781]"
```

---

## Jellyfin 侧后续操作

1. 确保插件设置中已开启 **「启用媒体信息缓存」**（`EnableMediaInfoCache = true`）。
2. 进入 Jellyfin **控制台 → 计划任务 → StrmTool**：
   - 执行 **「从缓存恢复 STRM 媒体信息」**（纯本地批量读取 `.strmtool.json` 写入 Jellyfin 数据库，不访问远程媒体源）；
   - 或直接执行 **「提取 STRM 媒体信息」**（有缓存的影视条目会自动命中本地缓存跳过远程探测，未转换的纯音频条目则会正常走局域网探测并提取内嵌音乐标签）。

---

## 注意事项

- **文件系统要求**：默认不覆盖模式需要目标文件系统支持硬链接；不支持时该文件会报错且不生成半成品，不会自动降级为可能覆盖已有缓存的写入方式。
- **批次错误**：单文件读取、转换或写入失败会计入错误并继续处理其他文件；存在错误时退出码为 `1`。
- **章节信息 (`Chapters`)**：Emby 的 `-mediainfo.json` 中包含章节时间点，而本分支 `.strmtool.json` 仅缓存媒体流与条目元数据（不缓存章节），转换时会忽略 `Chapters` 字段。
- **纯音频标签**：新版 Emby 的 `-mediainfo.json` 会在顶层 `AudioMetadata` 中保存标签（标题、专辑、歌手、音轨号、年份、流派等），但本脚本**暂不迁移这些标签**。因此纯音频默认跳过；使用 `--include-audio` 只会迁移技术流与歌词路径，生成的缓存不含 `audioTagsProbed: true`，Jellyfin 仍可能对缺少专辑/艺术家信息的音频发起一次探测以补齐标签。

## 回归测试

```bash
python3 -B -m unittest discover -s tests -p 'test_migrate_*.py' -v
```

测试使用临时目录，不操作真实媒体库。安装 .NET SDK 时，还会调用项目实际的 Jellyfin 缓存读取逻辑验证转换结果与内容指纹；没有 SDK 时跳过该集成测试。
