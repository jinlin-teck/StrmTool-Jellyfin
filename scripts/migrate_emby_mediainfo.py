#!/usr/bin/env python3
"""
将 Emby 版 StrmTool 生成的 `*-mediainfo.json` 批量就地迁移为 Jellyfin 版 `*.strmtool.json`。

特性：
1. 默认在对应 `.strm` 同目录生成 `{basename}.strmtool.json`，不修改、不删除原 `*-mediainfo.json`。
2. 自动读取同目录 `.strm` 计算 SHA256 内容指纹 (`strmContentHash`)，满足 Jellyfin 版缓存校验。
3. 自动完成 Emby -> Jellyfin MediaStream 结构映射：
   - 字符串/数值枚举 `Type` / `DeliveryMethod` 转为 Jellyfin 整数枚举（两套枚举编号不同）；
   - 过滤 Emby 专有的 `Attachment` 流，以及纯音频中以字幕形式导出的内嵌/外挂歌词流；
   - 将 Emby 的杜比视界 `DoviProfileXX` 映射为 Jellyfin `DvProfile` / 兼容位 / 标志位，
     实际 HDR 范围由 Jellyfin 根据这些字段计算，未识别 profile 不再由脚本直接写成 SDR；
   - 自动补齐 HDR10 / HLG / HDR10+ 的 `ColorTransfer`（HDR10+ 另写 `Hdr10PlusPresentFlag`）；
   - 自动补齐 Dolby Atmos / DTS:X 音轨的 `Profile`；
   - 剔除 Emby 专有冗余字段，原子写入目标文件。
4. 默认同时转换影视与纯音频文件；纯音频会迁移 Emby `AudioMetadata` 中
   Jellyfin 插件缓存支持的核心音乐标签字段（`--skip-audio` 可显式排除纯音频）。
   保留 `--include-audio` 作为兼容参数（与 `--skip-audio` 互斥）。
5. 默认跳过已存在的 `.strmtool.json`（可通过 `--overwrite` 覆盖）。
6. 支持 `--dry-run` 预览与 `--output-dir` 隔离输出（用于无污染测试）。
"""

from __future__ import annotations

import argparse
import codecs
import contextlib
import datetime as dt
import hashlib
import json
import os
import re
import sys
import uuid
from collections import Counter
from concurrent.futures import ThreadPoolExecutor, as_completed
from dataclasses import dataclass, field
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

# Emby 的 MediaStreamType 数值枚举与 Jellyfin 不同：
# Emby: Audio=1 / Video=2 / Subtitle=3 / EmbeddedImage=4 / Attachment=5 / Data=6
# Jellyfin: Audio=0 / Video=1 / Subtitle=2 / EmbeddedImage=3 / Data=4 / Lyric=5
# Attachment 与 Unknown 在 Jellyfin 没有对应类型，返回 None 由调用方过滤。
EMBY_STREAM_TYPE_MAP: dict[int, int] = {
    1: 0,
    2: 1,
    3: 2,
    4: 3,
    6: 4,
}

DELIVERY_METHOD_MAP = {"Encode": 0, "Embed": 1, "External": 2, "Hls": 3, "Drop": 4}

# Emby 的 ExtendedVideoSubType -> Jellyfin 杜比视界字段。
# 值：(DvProfile, DvBlSignalCompatibilityId, VideoDoViTitle)
DOVI_PROFILE_TABLE: dict[str, tuple[int, int, str]] = {
    "DoviProfile50": (5, 0, "Dolby Vision Profile 5"),
    "DoviProfile76": (7, 6, "Dolby Vision Profile 7.6 (HDR10)"),
    "DoviProfile81": (8, 1, "Dolby Vision Profile 8.1 (HDR10)"),
    "DoviProfile82": (8, 2, "Dolby Vision Profile 8.2 (SDR)"),
    "DoviProfile84": (8, 4, "Dolby Vision Profile 8.4 (HLG)"),
}
# profile 8 的兼容基础层：1=HDR10, 2=SDR, 4=HLG
DOVI_COMPAT_RANGE_TYPES = {1: 5, 2: 7, 4: 6}

# 提示分类：只用于汇总展示，不影响转换成功与否或退出码。
NOTICE_AUDIO_TAGS_UNPROBED = "audio_tags_unprobed"
NOTICE_AUDIO_TAGS_IGNORED = "audio_tags_ignored"

INT32_MAX = 2_147_483_647
MAX_NOTICE_SAMPLES = 5


def _normalize_audio_text(value: Any) -> str | None:
    """仅接受字符串，去首尾空白后为空视为无效。"""
    if not isinstance(value, str):
        return None
    trimmed = value.strip()
    return trimmed or None


def _normalize_audio_string_list(value: Any) -> tuple[list[str] | None, bool]:
    """
    接受字符串数组：逐项去空白、去空项、按大小写无关规则去重且保持顺序。
    不按 / ; 等分隔符二次拆分名称。返回 (有效列表, 是否含被忽略的非字符串项)。
    """
    if not isinstance(value, list):
        return None, True

    normalized: list[str] = []
    seen: set[str] = set()
    ignored = False
    for item in value:
        if not isinstance(item, str):
            ignored = True
            continue
        trimmed = item.strip()
        if not trimmed:
            continue
        key = trimmed.casefold()
        if key in seen:
            continue
        seen.add(key)
        normalized.append(trimmed)

    return (normalized or None), ignored


def _normalize_positive_int(value: Any, max_value: int = INT32_MAX) -> int | None:
    """
    严格解析正整数：不接受布尔值、浮点数（不截断小数）或带小数/符号的字符串。
    字符串允许首尾空白，但只接受纯 ASCII 数字。
    """
    if isinstance(value, bool):
        return None
    if isinstance(value, int):
        return value if 0 < value <= max_value else None
    if isinstance(value, str):
        text = value.strip()
        if not re.fullmatch(r"[0-9]+", text):
            return None
        # 先去掉前导零，避免超长数字触发 Python 的 int 字符串位数限制；
        # 去零后仍超出目标范围时直接判定无效，不做无意义的大整数转换。
        significant = text.lstrip("0")
        if not significant or len(significant) > len(str(max_value)):
            return None
        parsed = int(significant)
        return parsed if parsed <= max_value else None
    return None


def build_audio_tag_fields(
    audio_metadata: Any, notices: list[tuple[str, str]] | None = None
) -> dict[str, Any]:
    """
    将 Emby `AudioMetadata` 转换为 Jellyfin 缓存可消费的音乐标签字段。

    仅迁移插件现有缓存格式支持的字段；`AudioTagsProbed` 严格保留源布尔语义，
    缺失、null 或类型错误时不写入，绝不因标签存在而推断为已探测。
    非对象元数据或个别非法字段会写入 notices，但不影响其余字段与技术流转换。
    """
    if not isinstance(audio_metadata, dict):
        if notices is not None:
            if audio_metadata is None:
                notices.append((NOTICE_AUDIO_TAGS_UNPROBED, "缺少 AudioMetadata"))
            else:
                notices.append(
                    (NOTICE_AUDIO_TAGS_IGNORED, "AudioMetadata 不是对象，已忽略音乐标签")
                )
                notices.append((NOTICE_AUDIO_TAGS_UNPROBED, "AudioMetadata 类型无效"))
        return {}

    mapped: dict[str, Any] = {}
    ignored_fields: list[str] = []

    probed = audio_metadata.get("AudioTagsProbed")
    if isinstance(probed, bool):
        mapped["audioTagsProbed"] = probed
    elif "AudioTagsProbed" in audio_metadata and probed is not None:
        ignored_fields.append("AudioTagsProbed")

    for source_key, target_key in (("Title", "title"), ("Album", "album")):
        if source_key not in audio_metadata:
            continue
        text = _normalize_audio_text(audio_metadata[source_key])
        if text is None:
            ignored_fields.append(source_key)
        else:
            mapped[target_key] = text

    for source_key, target_key in (
        ("Artists", "artists"),
        ("AlbumArtists", "albumArtists"),
        ("Genres", "genres"),
    ):
        if source_key not in audio_metadata:
            continue
        values, has_invalid = _normalize_audio_string_list(audio_metadata[source_key])
        if values is not None:
            mapped[target_key] = values
        if has_invalid:
            ignored_fields.append(source_key)

    for source_key, target_key, max_value in (
        ("TrackNumber", "trackNumber", INT32_MAX),
        ("DiscNumber", "discNumber", INT32_MAX),
        ("ProductionYear", "productionYear", 9999),
    ):
        if source_key not in audio_metadata:
            continue
        parsed = _normalize_positive_int(audio_metadata[source_key], max_value)
        if parsed is None:
            ignored_fields.append(source_key)
        else:
            mapped[target_key] = parsed

    if notices is not None:
        if ignored_fields:
            notices.append(
                (
                    NOTICE_AUDIO_TAGS_IGNORED,
                    "已忽略无效字段: " + ", ".join(ignored_fields),
                )
            )

        if "AudioTagsProbed" not in audio_metadata:
            notices.append((NOTICE_AUDIO_TAGS_UNPROBED, "audioTagsProbed 缺失"))
        elif probed is None:
            notices.append((NOTICE_AUDIO_TAGS_UNPROBED, "audioTagsProbed 为 null"))
        elif isinstance(probed, bool) and not probed:
            notices.append((NOTICE_AUDIO_TAGS_UNPROBED, "audioTagsProbed 为 false"))
        elif not isinstance(probed, bool):
            notices.append((NOTICE_AUDIO_TAGS_UNPROBED, "audioTagsProbed 类型无效"))

    return mapped


def normalize_stream_type(raw_type: Any) -> int | None:
    """把 Emby 的字符串或数值枚举统一转换为 Jellyfin 的数值枚举。"""
    if isinstance(raw_type, bool):
        return None
    if isinstance(raw_type, int):
        return EMBY_STREAM_TYPE_MAP.get(raw_type)
    if isinstance(raw_type, str):
        return STREAM_TYPE_MAP.get(raw_type)
    return None


def apply_dovi_mapping(stream: dict[str, Any], ext_sub: Any) -> bool:
    """
    把 Emby 的 DoviProfileXX 映射为 Jellyfin 杜比视界字段。
    Jellyfin 的 VideoRangeType 是计算属性，实际范围由这里写入的 DvProfile / 兼容位 / 标志位决定。
    未识别的 profile 也会保留这些字段，避免脚本直接把杜比视界流写成 SDR。
    """
    if not isinstance(ext_sub, str) or not ext_sub.startswith("DoviProfile"):
        return False

    suffix = ext_sub[len("DoviProfile"):]
    if not suffix.isdigit():
        return False
    profile, version = divmod(int(suffix), 10)
    if profile <= 0:
        return False

    mapped = DOVI_PROFILE_TABLE.get(ext_sub)
    if mapped:
        _, compat_id, title = mapped
    else:
        compat_id = 6 if profile == 7 else (version if profile == 8 and version in DOVI_COMPAT_RANGE_TYPES else 0)
        title = f"Dolby Vision Profile {profile}.{version}"

    range_type = 8 if profile == 7 else (DOVI_COMPAT_RANGE_TYPES.get(version, 4) if profile == 8 else 4)

    if profile in (5, 7, 8, 9):
        stream["DvProfile"] = profile
    stream["DvBlSignalCompatibilityId"] = compat_id
    stream["BlPresentFlag"] = 1
    stream["RpuPresentFlag"] = 1
    stream["ElPresentFlag"] = 1 if profile == 7 else 0
    stream["VideoRange"] = 2
    stream["VideoRangeType"] = range_type
    stream["VideoDoViTitle"] = title
    return True


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
    notices: list[tuple[str, str]] = field(default_factory=list)


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


def is_remote_url(path_val: Any) -> bool:
    return isinstance(path_val, str) and path_val.lower().startswith(("http://", "https://"))


def is_safe_local_filename(path_val: str) -> bool:
    if not path_val or "\0" in path_val:
        return False
    if "/" in path_val or "\\" in path_val:
        return False
    return path_val not in (".", "..")


def convert_single_stream(raw: dict[str, Any], has_video: bool) -> dict[str, Any] | None:
    stream_type = normalize_stream_type(raw.get("Type"))
    if stream_type is None:
        return None

    codec = raw.get("Codec")
    # 纯音频的歌词在 Emby 中以 Subtitle 导出（内嵌 Codec=text、本地 .lrc/.elrc 外挂流），
    # Jellyfin 只识别 Type=Lyric，统一剔除后由下方按磁盘文件补一条歌词流。
    if not has_video and stream_type == 2:
        codec_name = codec.lower() if isinstance(codec, str) else ""
        path_ext = os.path.splitext(str(raw.get("Path") or ""))[1].lower()
        if codec_name in {"text", "lrc", "elrc", "txt"} or path_ext in {".lrc", ".elrc", ".txt"}:
            return None

    is_external = bool(raw.get("IsExternal", False))
    external_url = False
    if is_external and stream_type == 2:
        sub_path = raw.get("Path")
        if not isinstance(sub_path, str) or not sub_path:
            return None
        external_url = is_remote_url(sub_path)
        if not external_url and not is_safe_local_filename(sub_path):
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
    if external_url:
        s["IsExternalUrl"] = True
    s["IsTextSubtitleStream"] = bool(s.get("IsTextSubtitleStream", False))
    # 内封流 SupportsExternalStream 在 Jellyfin 缓存中为 false，外挂文本字幕保持 true
    s["SupportsExternalStream"] = bool(s.get("SupportsExternalStream", False)) if is_external else False

    # Video 范围与 HDR / 杜比视界映射
    if stream_type == 1:
        ext_type = raw.get("ExtendedVideoType")
        ext_sub = raw.get("ExtendedVideoSubType")
        emby_vr = raw.get("VideoRange")
        color_transfer = raw.get("ColorTransfer")

        if apply_dovi_mapping(s, ext_sub):
            pass
        elif ext_type == "Hdr10Plus" or ext_sub == "Hdr10Plus0":
            s["ColorTransfer"] = "smpte2084"
            s["VideoRange"] = 2
            s["VideoRangeType"] = 12  # HDR10Plus
            s["Hdr10PlusPresentFlag"] = True
        elif ext_type == "DolbyVision" or emby_vr == "DolbyVision":
            # 未知 / 无法解析的 profile：写入缓存占位（Jellyfin 读取时按 DvProfile/兼容位/色彩字段计算）
            s["VideoRange"] = 2
            s["VideoRangeType"] = 4  # DOVI
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
    include_audio: bool = True,
    strm_dir: str | None = None,
    base_name: str | None = None,
    notices: list[tuple[str, str]] | None = None,
) -> tuple[dict[str, Any] | None, str | None]:
    """
    将 Emby JSON 对象转换为 Jellyfin MediaInfoCacheData 字典。
    返回 (payload, skip_or_error_reason)；纯音频的音乐标签提示追加到 notices。
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

    normalized_types = [
        normalize_stream_type(st.get("Type"))
        for st in raw_streams
        if isinstance(st, dict)
    ]
    has_video = 1 in normalized_types
    has_audio = 0 in normalized_types

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

    # 纯音频且同目录存在外挂歌词文件时，补充 Type=5 (Lyric) 流
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
        parsed_ticks = safe_int(runtime_ticks, 0)
        if parsed_ticks > 0:
            payload["runTimeTicks"] = parsed_ticks
    if container:
        payload["container"] = str(container)
    payload["width"] = width
    payload["height"] = height
    payload["totalBitrate"] = total_bitrate

    # 仅纯音频条目迁移音乐标签；电影 / MV 即使带 AudioMetadata 也不写入，避免污染影视元数据。
    if not has_video and has_audio:
        payload.update(build_audio_tag_fields(first.get("AudioMetadata"), notices))

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
    include_audio: bool = True,
    dry_run: bool = False,
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

    notices: list[tuple[str, str]] = []
    payload, reason = build_jellyfin_cache_payload(
        emby_data,
        strm_hash=strm_hash,
        include_audio=include_audio,
        strm_dir=dir_name,
        base_name=base_name,
        notices=notices,
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

    return ConvertResult("converted", source_json_path, target_json_path, notices=notices)


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


def collect_notice_stats(
    stats: dict[str, dict[str, Any]],
    result: ConvertResult,
    max_samples: int = MAX_NOTICE_SAMPLES,
) -> None:
    """按提示分类累计数量、原因与有界示例，避免大批量旧备份刷屏。"""
    for code, detail in result.notices:
        entry = stats.setdefault(code, {"count": 0, "reasons": Counter(), "samples": []})
        entry["count"] += 1
        entry["reasons"][detail] += 1
        if len(entry["samples"]) < max_samples:
            entry["samples"].append(f"{result.source_path}: {detail}")


def print_audio_tag_notices(stats: dict[str, dict[str, Any]]) -> None:
    """输出有界汇总提示；提示不等于转换失败，退出码保持不变。"""
    unprobed = stats.get(NOTICE_AUDIO_TAGS_UNPROBED)
    ignored = stats.get(NOTICE_AUDIO_TAGS_IGNORED)
    if not unprobed and not ignored:
        return

    print("\n音频标签提示：", file=sys.stderr)
    if unprobed:
        reasons = "；".join(
            f"{reason} × {count}" for reason, count in unprobed["reasons"].most_common()
        )
        print(
            f"  - {unprobed['count']} 个纯音频条目没有可信的 audioTagsProbed: true（{reasons}）。",
            file=sys.stderr,
        )
        print(
            "    Jellyfin 是否再次探测仍取决于条目缺失的专辑/艺术家等元数据；"
            "脚本不会伪造已探测状态。",
            file=sys.stderr,
        )
    if ignored:
        reasons = "；".join(
            f"{reason} × {count}" for reason, count in ignored["reasons"].most_common()
        )
        print(
            f"  - {ignored['count']} 个条目存在被忽略的无效音乐标签（{reasons}）。",
            file=sys.stderr,
        )
    print(
        "  以前迁移但不含音乐标签的旧缓存需先隔离预览，再用 --overwrite 显式重新生成。",
        file=sys.stderr,
    )
    for entry in (unprobed, ignored):
        if entry:
            for sample in entry["samples"]:
                print(f"    示例: {sample}", file=sys.stderr)


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
    audio_group = parser.add_mutually_exclusive_group()
    audio_group.add_argument(
        "--include-audio",
        action="store_true",
        help="兼容参数：转换纯音频（现为默认行为；与 --skip-audio 互斥）",
    )
    audio_group.add_argument(
        "--skip-audio",
        action="store_true",
        help="跳过纯音频文件，仅转换影视条目",
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
        help="并发工作线程数（默认: 8，上限: 64）",
    )

    args = parser.parse_args()
    include_audio = not args.skip_audio
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
    notice_stats: dict[str, dict[str, Any]] = {}

    worker_count = min(64, max(1, args.workers))
    with ThreadPoolExecutor(max_workers=worker_count) as executor:
        futures = {
            executor.submit(
                process_mediainfo_file,
                src_path,
                root_dir,
                output_dir,
                args.overwrite,
                include_audio,
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
            if res.notices:
                collect_notice_stats(notice_stats, res)
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

    print_audio_tag_notices(notice_stats)

    return 1 if counts["error"] > 0 else 0


if __name__ == "__main__":
    sys.exit(main())
