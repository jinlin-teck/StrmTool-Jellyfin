#!/usr/bin/env python3
"""
将 Emby 版 StrmTool 生成的 `*-mediainfo.json` 批量就地迁移为 Jellyfin 版 `*.strmtool.json`。

特性：
1. 默认在对应 `.strm` 同目录生成 `{basename}.strmtool.json`，不修改、不删除原 `*-mediainfo.json`。
2. 自动读取同目录 `.strm` 计算 SHA256 内容指纹 (`strmContentHash`)，满足 Jellyfin 版缓存校验。
3. 自动完成 Emby -> Jellyfin MediaStream 结构映射：
   - 字符串枚举 `Type` / `DeliveryMethod` 转为 Jellyfin 整数枚举；
   - 过滤 Emby 专有的 `Attachment` 流及纯音频伪字幕歌词流；
   - 将 Emby 的杜比视界 (`DoviProfile50` / `DoviProfile76` / `DoviProfile81`) 映射为 Jellyfin `DvProfile` 等字段；
   - 自动补齐 HDR 的 `ColorTransfer` 与 Dolby Atmos / DTS:X 音轨的 `Profile`；
   - 剔除 Emby 专有冗余字段，原子写入目标文件。
4. 默认跳过纯音频文件（可通过 `--include-audio` 开启）与已存在的 `.strmtool.json`（可通过 `--overwrite` 覆盖）。
5. 支持 `--dry-run` 预览与 `--output-dir` 隔离输出（用于无污染测试）。
"""

from __future__ import annotations

import argparse
import codecs
import contextlib
import datetime as dt
import hashlib
import json
import os
import sys
import uuid
from concurrent.futures import ThreadPoolExecutor, as_completed
from dataclasses import dataclass
from typing import Any

EMBY_SUFFIX = "-mediainfo.json"
JELLYFIN_SUFFIX = ".strmtool.json"
STRM_EXT = ".strm"

STREAM_TYPE_MAP: dict[str, int] = {
    "Audio": 0,
    "Video": 1,
    "Subtitle": 2,
    "EmbeddedImage": 3,
    "Data": 4,
    "Lyric": 5,
}

DELIVERY_METHOD_MAP = {"Encode": 0, "Embed": 1, "External": 2, "Hls": 3, "Drop": 4}

# Jellyfin MediaStream 允许保留的可写/标准属性顺序（与 System.Text.Json 序列化对齐）
ALLOWED_STREAM_KEYS: tuple[str, ...] = (
    "Codec",
    "CodecTag",
    "Language",
    "ColorRange",
    "ColorSpace",
    "ColorTransfer",
    "ColorPrimaries",
    "DvVersionMajor",
    "DvVersionMinor",
    "DvProfile",
    "DvLevel",
    "RpuPresentFlag",
    "ElPresentFlag",
    "BlPresentFlag",
    "DvBlSignalCompatibilityId",
    "Rotation",
    "Comment",
    "TimeBase",
    "CodecTimeBase",
    "Title",
    "Hdr10PlusPresentFlag",
    "VideoRange",
    "VideoRangeType",
    "VideoDoViTitle",
    "AudioSpatialFormat",
    "LocalizedUndefined",
    "LocalizedDefault",
    "LocalizedForced",
    "LocalizedExternal",
    "LocalizedHearingImpaired",
    "LocalizedLanguage",
    "LocalizedOriginal",
    "DisplayTitle",
    "NalLengthSize",
    "IsInterlaced",
    "IsAVC",
    "ChannelLayout",
    "BitRate",
    "BitDepth",
    "RefFrames",
    "PacketLength",
    "Channels",
    "SampleRate",
    "IsDefault",
    "IsForced",
    "IsHearingImpaired",
    "IsOriginal",
    "Height",
    "Width",
    "AverageFrameRate",
    "RealFrameRate",
    "ReferenceFrameRate",
    "Profile",
    "Type",
    "AspectRatio",
    "Index",
    "Score",
    "IsExternal",
    "DeliveryMethod",
    "DeliveryUrl",
    "IsExternalUrl",
    "IsTextSubtitleStream",
    "SupportsExternalStream",
    "Path",
    "PixelFormat",
    "Level",
    "IsAnamorphic",
)


@dataclass
class ConvertResult:
    status: str  # "converted", "skipped_existing", "skipped_audio", "error"
    source_path: str
    target_path: str | None = None
    message: str | None = None


def safe_int(val: Any, default: int = 0) -> int:
    if isinstance(val, int) and not isinstance(val, bool):
        return val
    if isinstance(val, float):
        return int(val)
    if isinstance(val, str):
        try:
            return int(val)
        except ValueError:
            return default
    return default


def compute_strm_content_hash(strm_path: str) -> str | None:
    """
    按 C# File.ReadLines 的 BOM 检测规则读取首个非空白行，计算大写 SHA256 Hex。
    """
    try:
        with open(strm_path, "rb") as source:
            prefix = source.read(4)
        encoding = "utf-8-sig"
        if prefix.startswith((codecs.BOM_UTF32_LE, codecs.BOM_UTF32_BE)):
            encoding = "utf-32"
        elif prefix.startswith((codecs.BOM_UTF16_LE, codecs.BOM_UTF16_BE)):
            encoding = "utf-16"
        with open(strm_path, encoding=encoding) as f:
            for line in f:
                trimmed = line.strip()
                if trimmed:
                    return hashlib.sha256(trimmed.encode("utf-8")).hexdigest().upper()
        return None
    except (OSError, UnicodeError):
        return None


def is_safe_local_filename(path_val: str) -> bool:
    if not path_val or "\0" in path_val:
        return False
    if "/" in path_val or "\\" in path_val:
        return False
    return path_val not in (".", "..")


def convert_single_stream(raw: dict[str, Any], has_video: bool) -> dict[str, Any] | None:
    raw_type = raw.get("Type")
    if isinstance(raw_type, int):
        stream_type = raw_type
    elif isinstance(raw_type, str):
        if raw_type == "Attachment":
            return None
        stream_type = STREAM_TYPE_MAP.get(raw_type)
        if stream_type is None:
            return None
    else:
        return None

    codec = raw.get("Codec")
    # 纯音频中 Emby 会将歌词导出为 Type=Subtitle, Codec=text，在 Jellyfin 中需剔除
    if not has_video and stream_type == 2 and isinstance(codec, str) and codec.lower() == "text":
        return None

    is_external = bool(raw.get("IsExternal", False))
    if is_external and stream_type == 2:
        sub_path = raw.get("Path")
        if not isinstance(sub_path, str) or not is_safe_local_filename(sub_path):
            return None

    s: dict[str, Any] = dict(raw)
    delivery = s.get("DeliveryMethod")
    if isinstance(delivery, str):
        delivery = DELIVERY_METHOD_MAP.get(delivery)
    if type(delivery) is int and delivery in DELIVERY_METHOD_MAP.values():
        s["DeliveryMethod"] = delivery
    else:
        s.pop("DeliveryMethod", None)
    s["Type"] = stream_type
    s["IsInterlaced"] = bool(s.get("IsInterlaced", False))
    s["IsDefault"] = bool(s.get("IsDefault", False))
    s["IsForced"] = bool(s.get("IsForced", False))
    s["IsHearingImpaired"] = bool(s.get("IsHearingImpaired", False))
    s["IsOriginal"] = bool(s.get("IsOriginal", False))
    s["IsExternal"] = is_external
    s["IsTextSubtitleStream"] = bool(s.get("IsTextSubtitleStream", False))
    # 内封流 SupportsExternalStream 在 Jellyfin 缓存中为 false，外挂文本字幕保持 true
    s["SupportsExternalStream"] = bool(s.get("SupportsExternalStream", False)) if is_external else False

    # Video 范围与杜比视界映射
    if stream_type == 1:
        ext_type = raw.get("ExtendedVideoType")
        ext_sub = raw.get("ExtendedVideoSubType")
        emby_vr = raw.get("VideoRange")
        color_transfer = raw.get("ColorTransfer")

        if ext_sub == "DoviProfile50":
            s["DvProfile"] = 5
            s["DvBlSignalCompatibilityId"] = 0
            s["BlPresentFlag"] = 1
            s["RpuPresentFlag"] = 1
            s["ElPresentFlag"] = 0
            s["VideoRange"] = 2
            s["VideoRangeType"] = 4  # DOVI
            s["VideoDoViTitle"] = "Dolby Vision Profile 5"
        elif ext_sub == "DoviProfile81":
            s["DvProfile"] = 8
            s["DvBlSignalCompatibilityId"] = 1
            s["BlPresentFlag"] = 1
            s["RpuPresentFlag"] = 1
            s["ElPresentFlag"] = 0
            s["VideoRange"] = 2
            s["VideoRangeType"] = 5  # DOVIWithHDR10
            s["VideoDoViTitle"] = "Dolby Vision Profile 8.1 (HDR10)"
        elif ext_sub == "DoviProfile76":
            s["DvProfile"] = 7
            s["DvBlSignalCompatibilityId"] = 6
            s["BlPresentFlag"] = 1
            s["RpuPresentFlag"] = 1
            s["ElPresentFlag"] = 1
            s["VideoRange"] = 2
            s["VideoRangeType"] = 8  # DOVIWithEL
            s["VideoDoViTitle"] = "Dolby Vision Profile 7.6 (HDR10)"
        elif ext_type == "Hdr10" or emby_vr == "HDR 10" or color_transfer == "smpte2084":
            s["ColorTransfer"] = "smpte2084"
            s["VideoRange"] = 2
            s["VideoRangeType"] = 2  # HDR10
        elif ext_type == "HyperLogGamma" or emby_vr == "HLG" or color_transfer == "arib-std-b67":
            s["ColorTransfer"] = "arib-std-b67"
            s["VideoRange"] = 2
            s["VideoRangeType"] = 3  # HLG
        else:
            s["VideoRange"] = 1
            s["VideoRangeType"] = 1  # SDR

        if isinstance(codec, str) and codec.lower() == "h264" and "IsAVC" not in s:
            s["IsAVC"] = True
    else:
        s["VideoRange"] = 0
        s["VideoRangeType"] = 0

    # Audio 空间音频格式 (Dolby Atmos / DTS:X) 映射
    if stream_type == 0:
        profile_str = s.get("Profile") or ""
        title_str = s.get("Title") or ""
        display_str = s.get("DisplayTitle") or ""
        combined = f"{profile_str} {title_str} {display_str}".lower()

        if "atmos" in combined:
            s["AudioSpatialFormat"] = 1
            # AudioSpatialFormat 是只读计算属性，必须补齐其依赖的 Profile。
            if "atmos" not in str(profile_str).lower():
                c_low = (codec or "").lower()
                if c_low == "eac3":
                    s["Profile"] = "Dolby Digital Plus + Dolby Atmos"
                elif c_low == "truehd":
                    s["Profile"] = "Dolby TrueHD + Dolby Atmos"
                else:
                    s["Profile"] = "Dolby Atmos"
        elif "dts:x" in combined or "dts-x" in combined:
            s["AudioSpatialFormat"] = 2
            if "dts:x" not in str(profile_str).lower():
                s["Profile"] = f"{profile_str} + DTS:X" if profile_str else "DTS:X"
        else:
            s["AudioSpatialFormat"] = 0
    else:
        s["AudioSpatialFormat"] = 0

    # ReferenceFrameRate 补齐
    if "ReferenceFrameRate" not in s:
        if s.get("AverageFrameRate") is not None:
            s["ReferenceFrameRate"] = s["AverageFrameRate"]
        elif s.get("RealFrameRate") is not None:
            s["ReferenceFrameRate"] = s["RealFrameRate"]

    # 仅保留 Jellyfin MediaStream 标准字段并过滤 null
    cleaned: dict[str, Any] = {}
    for key in ALLOWED_STREAM_KEYS:
        val = s.get(key)
        if val is not None:
            cleaned[key] = val

    return cleaned


def build_jellyfin_cache_payload(
    emby_json_data: Any,
    strm_hash: str,
    include_audio: bool = False,
    strm_dir: str | None = None,
    base_name: str | None = None,
) -> tuple[dict[str, Any] | None, str | None]:
    """
    将 Emby JSON 对象转换为 Jellyfin MediaInfoCacheData 字典。
    返回 (payload, skip_or_error_reason)。
    """
    if not isinstance(emby_json_data, list) or len(emby_json_data) == 0:
        return None, "top-level JSON is not a non-empty array"

    first = emby_json_data[0]
    if not isinstance(first, dict):
        return None, "first element is not an object"

    msi = first.get("MediaSourceInfo")
    if not isinstance(msi, dict):
        return None, "missing MediaSourceInfo"

    raw_streams = msi.get("MediaStreams")
    if not isinstance(raw_streams, list) or len(raw_streams) == 0:
        return None, "missing or empty MediaStreams"

    has_video = any(
        isinstance(st, dict) and st.get("Type") in ("Video", 1)
        for st in raw_streams
    )
    has_audio = any(
        isinstance(st, dict) and st.get("Type") in ("Audio", 0)
        for st in raw_streams
    )

    if not has_video and not has_audio:
        return None, "no video or audio stream in MediaStreams"

    if not has_video and not include_audio:
        return None, "SKIP_AUDIO"

    converted_streams: list[dict[str, Any]] = []
    for st in raw_streams:
        if not isinstance(st, dict):
            continue
        conv = convert_single_stream(st, has_video=has_video)
        if conv is not None:
            converted_streams.append(conv)

    # 若启用了纯音频迁移且同目录存在外挂歌词文件，补充 Type=5 (Lyric) 流
    if not has_video and strm_dir and base_name:
        for ext in (".lrc", ".elrc", ".txt"):
            lyric_fn = base_name + ext
            if os.path.isfile(os.path.join(strm_dir, lyric_fn)):
                converted_streams.append(
                    {
                        "VideoRange": 0,
                        "VideoRangeType": 0,
                        "AudioSpatialFormat": 0,
                        "IsInterlaced": False,
                        "IsDefault": False,
                        "IsForced": False,
                        "IsHearingImpaired": False,
                        "IsOriginal": False,
                        "Type": 5,
                        "Index": 0,
                        "IsExternal": False,
                        "IsTextSubtitleStream": False,
                        "SupportsExternalStream": False,
                        "Path": lyric_fn,
                    }
                )
                break

    # 对齐 StrmMediaInfoService.MergeExternalStreams：外挂流 / 歌词流 Index 排在内封流之后
    internal_indices = [
        safe_int(st.get("Index"), 0)
        for st in converted_streams
        if not st.get("IsExternal", False) and st.get("Type") != 5
    ]
    next_ext_idx = (max(internal_indices) if internal_indices else -1) + 1
    for st in converted_streams:
        if st.get("IsExternal", False) or st.get("Type") == 5:
            st["Index"] = next_ext_idx
            next_ext_idx += 1

    # 取最高分辨率视频流宽高
    width = 0
    height = 0
    best_pixels = -1
    for st in converted_streams:
        if st.get("Type") == 1:
            w = safe_int(st.get("Width"), 0)
            h = safe_int(st.get("Height"), 0)
            if w > 0 and h > 0 and (w * h) > best_pixels:
                best_pixels = w * h
                width = w
                height = h

    size = safe_int(msi.get("Size"), 0)
    runtime_ticks = msi.get("RunTimeTicks")
    container = msi.get("Container")
    raw_bitrate = safe_int(msi.get("Bitrate"), 0)
    total_bitrate = min(2147483647, max(0, raw_bitrate))

    now_iso = dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%fZ")

    payload: dict[str, Any] = {
        "version": "1.0",
        "timestamp": now_iso,
        "mediaStreams": converted_streams,
        "strmContentHash": strm_hash,
        "isValid": True,
        "size": size,
    }
    if runtime_ticks is not None:
        payload["runTimeTicks"] = safe_int(runtime_ticks, 0)
    if container:
        payload["container"] = str(container)
    payload["width"] = width
    payload["height"] = height
    payload["totalBitrate"] = total_bitrate

    return payload, None


def write_json_atomically(target_path: str, payload: dict[str, Any], overwrite: bool = False) -> None:
    target_dir = os.path.dirname(target_path)
    temp_path = f"{target_path}.{uuid.uuid4().hex}.tmp"
    try:
        if target_dir:
            os.makedirs(target_dir, exist_ok=True)
        content = json.dumps(payload, ensure_ascii=False, indent=2)
        with open(temp_path, "x", encoding="utf-8") as f:
            f.write(content)
        if overwrite:
            os.replace(temp_path, target_path)
        else:
            # 同目录硬链接原子发布；目标已存在时失败，绝不先检查再覆盖。
            os.link(temp_path, target_path)
    finally:
        if os.path.exists(temp_path):
            with contextlib.suppress(OSError):
                os.remove(temp_path)


def get_target_path(source_json_path: str, root_dir: str, output_dir: str | None) -> str:
    directory, filename = os.path.split(source_json_path)
    if output_dir:
        directory = os.path.join(output_dir, os.path.relpath(directory, root_dir))
    return os.path.normpath(os.path.join(directory, filename[:-len(EMBY_SUFFIX)] + JELLYFIN_SUFFIX))


def prepare_tasks(tasks: list[tuple[str, str]], output_dir: str | None) -> list[tuple[str, str]]:
    """执行前拒绝不同源的目标冲突，并去掉重复的源/目标组合。"""
    targets: dict[str, str] = {}
    unique_tasks = []
    for source, root in tasks:
        target = os.path.normcase(os.path.realpath(get_target_path(source, root, output_dir)))
        canonical_source = os.path.normcase(os.path.realpath(source))
        if target in targets:
            if targets[target] != canonical_source:
                raise ValueError(f"输出路径冲突: {target} <- {targets[target]} / {source}")
            continue
        targets[target] = canonical_source
        unique_tasks.append((source, root))
    return unique_tasks


def process_mediainfo_file(
    source_json_path: str,
    root_dir: str,
    output_dir: str | None,
    overwrite: bool,
    include_audio: bool,
    dry_run: bool,
) -> ConvertResult:
    dir_name, file_name = os.path.split(source_json_path)
    if not file_name.endswith(EMBY_SUFFIX):
        return ConvertResult("error", source_json_path, message="invalid file suffix")

    base_name = file_name[: -len(EMBY_SUFFIX)]
    strm_path = os.path.join(dir_name, base_name + STRM_EXT)

    target_json_path = get_target_path(source_json_path, root_dir, output_dir)

    if not overwrite and os.path.exists(target_json_path):
        return ConvertResult("skipped_existing", source_json_path, target_json_path)

    if not os.path.isfile(strm_path):
        return ConvertResult("error", source_json_path, target_json_path, "corresponding .strm file not found")

    try:
        with open(source_json_path, encoding="utf-8-sig") as f:
            emby_data = json.load(f)
    except Exception as ex:
        return ConvertResult("error", source_json_path, target_json_path, f"JSON read error: {ex}")

    strm_hash = compute_strm_content_hash(strm_path)
    if not strm_hash:
        return ConvertResult("error", source_json_path, target_json_path, ".strm file is empty or unreadable")

    payload, reason = build_jellyfin_cache_payload(
        emby_data,
        strm_hash=strm_hash,
        include_audio=include_audio,
        strm_dir=dir_name,
        base_name=base_name,
    )

    if reason == "SKIP_AUDIO":
        return ConvertResult("skipped_audio", source_json_path, target_json_path)
    if payload is None:
        return ConvertResult("error", source_json_path, target_json_path, reason or "conversion failed")

    if not dry_run:
        try:
            write_json_atomically(target_json_path, payload, overwrite=overwrite)
        except FileExistsError:
            if not overwrite:
                return ConvertResult("skipped_existing", source_json_path, target_json_path)
            return ConvertResult("error", source_json_path, target_json_path, "target already exists")
        except Exception as ex:
            return ConvertResult("error", source_json_path, target_json_path, f"write error: {ex}")

    return ConvertResult("converted", source_json_path, target_json_path)


def collect_tasks(inputs: list[str]) -> list[tuple[str, str]]:
    """返回 (source_json_path, base_root_dir) 列表。"""
    tasks: list[tuple[str, str]] = []
    for raw_input in inputs:
        abs_input = os.path.abspath(raw_input)
        if os.path.isfile(abs_input):
            if abs_input.endswith(EMBY_SUFFIX):
                tasks.append((abs_input, os.path.dirname(abs_input)))
        elif os.path.isdir(abs_input):
            for dirpath, _, filenames in os.walk(abs_input):
                for fn in filenames:
                    if fn.endswith(EMBY_SUFFIX):
                        tasks.append((os.path.join(dirpath, fn), abs_input))
        else:
            print(f"[WARN] Path does not exist: {raw_input}", file=sys.stderr)
    return tasks


def main() -> int:
    parser = argparse.ArgumentParser(
        description="将 Emby 版 *-mediainfo.json 就地迁移为 Jellyfin 版 *.strmtool.json"
    )
    parser.add_argument(
        "paths",
        nargs="+",
        help="要扫描的目录（递归）或单个 *-mediainfo.json 文件路径",
    )
    parser.add_argument(
        "--output-dir",
        default=None,
        help="隔离输出目录（保持相对路径结构，不写入源目录；默认在源 .strm 同目录就地生成）",
    )
    parser.add_argument(
        "--overwrite",
        action="store_true",
        help="覆盖已存在的 *.strmtool.json（默认跳过已存在文件）",
    )
    parser.add_argument(
        "--include-audio",
        action="store_true",
        help="同时转换纯音频文件（默认跳过纯音频）",
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="仅预览统计，不写入任何文件",
    )
    parser.add_argument(
        "--workers",
        type=int,
        default=8,
        help="并发工作线程数（默认: 8）",
    )

    args = parser.parse_args()
    output_dir = os.path.abspath(args.output_dir) if args.output_dir else None
    try:
        tasks = prepare_tasks(collect_tasks(args.paths), output_dir)
    except ValueError as ex:
        print(f"[ERROR] {ex}", file=sys.stderr)
        return 1
    total = len(tasks)
    if total == 0:
        print("未找到任何 *-mediainfo.json 文件。")
        return 0

    mode_desc = "DRY-RUN (仅预览)" if args.dry_run else (f"输出至 {output_dir}" if output_dir else "同目录就地生成")
    print(f"扫描到 {total} 个 *-mediainfo.json 文件，模式: {mode_desc}")

    counts = {
        "converted": 0,
        "skipped_existing": 0,
        "skipped_audio": 0,
        "error": 0,
    }
    errors: list[ConvertResult] = []

    with ThreadPoolExecutor(max_workers=max(1, args.workers)) as executor:
        futures = {
            executor.submit(
                process_mediainfo_file,
                src_path,
                root_dir,
                output_dir,
                args.overwrite,
                args.include_audio,
                args.dry_run,
            ): src_path
            for src_path, root_dir in tasks
        }
        for idx, fut in enumerate(as_completed(futures), 1):
            try:
                res = fut.result()
            except Exception as ex:
                res = ConvertResult("error", futures[fut], message=f"conversion error: {ex}")
            counts[res.status] = counts.get(res.status, 0) + 1
            if res.status == "error":
                errors.append(res)
            if idx % 2000 == 0 or idx == total:
                print(
                    f"进度: {idx}/{total} | 已转换: {counts['converted']} | "
                    f"跳过已存在: {counts['skipped_existing']} | "
                    f"跳过纯音频: {counts['skipped_audio']} | 错误: {counts['error']}"
                )

    if errors:
        print("\n错误详情（最多显示前 20 条）：", file=sys.stderr)
        for err in errors[:20]:
            print(f"  - {err.source_path}: {err.message}", file=sys.stderr)

    return 1 if counts["error"] > 0 else 0


if __name__ == "__main__":
    sys.exit(main())
