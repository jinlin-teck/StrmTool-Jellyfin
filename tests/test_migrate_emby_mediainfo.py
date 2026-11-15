"""运行：python3 -B -m unittest discover -s tests -p 'test_migrate_*.py' -v"""

import codecs
import concurrent.futures
import hashlib
import json
from pathlib import Path
import runpy
import shutil
import subprocess
import sys
import tempfile
import threading
import unittest
from unittest.mock import patch
from xml.sax.saxutils import escape


ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts/migrate_emby_mediainfo.py"
M = runpy.run_path(str(SCRIPT))


class MigrationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="strm-migration-test-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def source(self, directory="source", name="movie", streams=None, audio_metadata=None,
               include_audio_metadata=False):
        folder = self.root / directory
        folder.mkdir(parents=True, exist_ok=True)
        path = folder / (name + "-mediainfo.json")
        entry = {"MediaSourceInfo": {"MediaStreams": streams or [
            {"Type": "Video", "Codec": "h264", "Index": 0}
        ]}}
        if include_audio_metadata:
            entry["AudioMetadata"] = audio_metadata
        path.write_text(json.dumps([entry]), encoding="utf-8")
        (folder / (name + ".strm")).write_text("https://example.com/" + directory, encoding="utf-8")
        return path

    def audio_source(self, directory="music", name="song", audio_metadata=None, **kwargs):
        return self.source(
            directory,
            name,
            streams=kwargs.pop("streams", [{"Type": "Audio", "Codec": "flac", "Index": 0}]),
            audio_metadata=audio_metadata,
            include_audio_metadata=audio_metadata is not None,
            **kwargs,
        )

    def cli(self, *args):
        return subprocess.run([sys.executable, "-B", str(SCRIPT), *map(str, args)],
                              capture_output=True, text=True, timeout=30)

    def test_delivery_methods(self):
        for name, value in M["DELIVERY_METHOD_MAP"].items():
            for raw in (name, value):
                with self.subTest(raw=raw):
                    stream = M["convert_single_stream"]({"Type": "Subtitle", "DeliveryMethod": raw}, True)
                    self.assertEqual(value, stream["DeliveryMethod"])
        for raw in (None, "Unexpected", True, 99):
            stream = M["convert_single_stream"]({"Type": "Subtitle", "DeliveryMethod": raw}, True)
            self.assertNotIn("DeliveryMethod", stream)

    def test_remote_external_subtitle_and_nonpositive_runtime(self):
        stream = M["convert_single_stream"]({
            "Type": "Subtitle", "Codec": "srt", "IsExternal": True,
            "Path": "https://example.com/sub/a.srt",
        }, True)
        self.assertEqual("https://example.com/sub/a.srt", stream["Path"])
        self.assertTrue(stream["IsExternalUrl"])

        self.assertIsNone(M["convert_single_stream"]({
            "Type": "Subtitle", "Codec": "srt", "IsExternal": True,
            "Path": "/abs/outside/a.srt",
        }, True))

        payload, reason = M["build_jellyfin_cache_payload"](
            [{"MediaSourceInfo": {
                "MediaStreams": [{"Type": "Video", "Codec": "h264", "Index": 0}],
                "RunTimeTicks": 0,
            }}],
            strm_hash="H",
        )
        self.assertIsNone(reason)
        self.assertNotIn("runTimeTicks", payload)

    def test_emby_numeric_stream_types(self):
        expected = {1: 0, 2: 1, 3: 2, 4: 3, 6: 4}
        for emby_type, jellyfin_type in expected.items():
            with self.subTest(emby_type=emby_type):
                stream = M["convert_single_stream"]({"Type": emby_type, "Codec": "h264"}, True)
                self.assertEqual(jellyfin_type, stream["Type"])
        for raw in (0, 5, 7, True):
            with self.subTest(raw=raw):
                self.assertIsNone(M["normalize_stream_type"](raw))

    def test_numeric_types_detected_for_video_and_audio(self):
        payload, reason = M["build_jellyfin_cache_payload"](
            [{"MediaSourceInfo": {"MediaStreams": [
                {"Type": 2, "Codec": "hevc", "Index": 0},
                {"Type": 1, "Codec": "aac", "Index": 1},
            ]}}],
            strm_hash="H",
        )
        self.assertIsNone(reason)
        self.assertEqual([1, 0], [stream["Type"] for stream in payload["mediaStreams"]])

    def test_hdr10plus_and_dovi_profiles(self):
        # Jellyfin 的 VideoRangeType 是计算属性，真正决定 HDR 范围的是 DvProfile / 兼容位 / 标志位，
        # 因此这里断言这些输入字段，而不是会被 Jellyfin 忽略的 VideoRangeType 存储值。
        cases = {
            "DoviProfile50": (5, 0, 0),
            "DoviProfile76": (7, 6, 1),
            "DoviProfile81": (8, 1, 0),
            "DoviProfile82": (8, 2, 0),
            "DoviProfile84": (8, 4, 0),
            "DoviProfile83": (8, 0, 0),
            "DoviProfile92": (9, 0, 0),
        }
        for sub_type, (profile, compat_id, el_flag) in cases.items():
            with self.subTest(sub_type=sub_type):
                stream = M["convert_single_stream"]({
                    "Type": "Video",
                    "ExtendedVideoType": "DolbyVision",
                    "ExtendedVideoSubType": sub_type,
                }, True)
                self.assertEqual(profile, stream["DvProfile"])
                self.assertEqual(compat_id, stream["DvBlSignalCompatibilityId"])
                self.assertEqual(el_flag, stream["ElPresentFlag"])
                self.assertEqual(1, stream["BlPresentFlag"])
                self.assertEqual(1, stream["RpuPresentFlag"])

        hdr10plus = M["convert_single_stream"]({
            "Type": "Video",
            "ExtendedVideoType": "Hdr10Plus",
            "ExtendedVideoSubType": "Hdr10Plus0",
        }, True)
        self.assertEqual("smpte2084", hdr10plus["ColorTransfer"])
        self.assertTrue(hdr10plus["Hdr10PlusPresentFlag"])

        unknown = M["convert_single_stream"]({
            "Type": "Video",
            "ExtendedVideoType": "DolbyVision",
            "ExtendedVideoSubType": "DoviProfileXX",
        }, True)
        self.assertEqual(2, unknown["VideoRange"])
        self.assertEqual(4, unknown["VideoRangeType"])

    def test_audio_lyric_streams_collapse_to_single_lyric(self):
        folder = self.root / "audio"
        folder.mkdir()
        (folder / "song.lrc").write_text("[00:00.00]lyrics", encoding="utf-8")
        payload, reason = M["build_jellyfin_cache_payload"](
            [{"MediaSourceInfo": {"MediaStreams": [
                {"Type": "Audio", "Codec": "flac", "Index": 0},
                {"Type": "Subtitle", "Codec": "text", "Index": 1, "Extradata": "embedded lyrics"},
                {"Type": "Subtitle", "Codec": "lrc", "Index": 2, "IsExternal": True, "Path": "song.lrc"},
            ]}}],
            strm_hash="H",
            include_audio=True,
            strm_dir=str(folder),
            base_name="song",
        )
        self.assertIsNone(reason)
        self.assertEqual([], [stream for stream in payload["mediaStreams"] if stream["Type"] == 2])
        lyrics = [stream for stream in payload["mediaStreams"] if stream["Type"] == 5]
        self.assertEqual(1, len(lyrics))
        self.assertEqual("song.lrc", lyrics[0]["Path"])

    def test_audio_tags_full_mapping_and_supported_fields(self):
        audio_metadata = {
            "AudioTagsProbed": True,
            "Title": " 雪地上的回忆 ",
            "SortName": "雪地上的回忆",
            "Album": " 皇牌影视金曲 ",
            "Artists": ["陈佳", " 陈佳 ", "李四/王五"],
            "AlbumArtists": ["陈佳"],
            "Composers": ["某人"],
            "Genres": ["Pop", "pop", " 华语 "],
            "TrackNumber": 3,
            "DiscNumber": "2",
            "ProductionYear": "2010",
            "PremiereDate": "2010-01-01T00:00:00.0000000+00:00",
            "ProviderIds": {"MusicBrainzTrack": "x"},
        }
        notices = []
        payload, reason = M["build_jellyfin_cache_payload"](
            [{"MediaSourceInfo": {"MediaStreams": [{"Type": "Audio", "Codec": "flac", "Index": 0}]},
              "AudioMetadata": audio_metadata}],
            strm_hash="H",
            notices=notices,
        )
        self.assertIsNone(reason)
        self.assertEqual({
            "audioTagsProbed": True,
            "title": "雪地上的回忆",
            "album": "皇牌影视金曲",
            "artists": ["陈佳", "李四/王五"],
            "albumArtists": ["陈佳"],
            "genres": ["Pop", "华语"],
            "trackNumber": 3,
            "discNumber": 2,
            "productionYear": 2010,
        }, {key: payload[key] for key in (
            "audioTagsProbed", "title", "album", "artists", "albumArtists",
            "genres", "trackNumber", "discNumber", "productionYear")})
        for unsupported in ("sortName", "composers", "premiereDate", "providerIds"):
            self.assertNotIn(unsupported, payload)
        self.assertEqual([], notices)

    def test_audio_tag_invalid_values_are_ignored_individually(self):
        audio_metadata = {
            "AudioTagsProbed": "true",
            "Title": 5,
            "Album": "   ",
            "Artists": "陈佳/李四",
            "AlbumArtists": ["", "  "],
            "Genres": ["Pop", 7, "pop"],
            "TrackNumber": True,
            "DiscNumber": 2.5,
            "ProductionYear": 10000,
        }
        notices = []
        payload, reason = M["build_jellyfin_cache_payload"](
            [{"MediaSourceInfo": {"MediaStreams": [{"Type": "Audio", "Index": 0}]},
              "AudioMetadata": audio_metadata}],
            strm_hash="H",
            notices=notices,
        )
        self.assertIsNone(reason)
        for key in ("audioTagsProbed", "title", "album", "artists", "albumArtists",
                    "trackNumber", "discNumber", "productionYear"):
            self.assertNotIn(key, payload)
        self.assertEqual(["Pop"], payload["genres"])
        ignored = [detail for code, detail in notices if code == "audio_tags_ignored"]
        self.assertEqual(1, len(ignored))
        for field in ("AudioTagsProbed", "Title", "Album", "Artists",
                      "Genres", "TrackNumber", "DiscNumber", "ProductionYear"):
            self.assertIn(field, ignored[0])
        # 空字符串数组项只被丢弃，不算非法字段
        self.assertNotIn("AlbumArtists", ignored[0])
        self.assertTrue(any(code == "audio_tags_unprobed" and "类型无效" in detail
                            for code, detail in notices))

    def test_audio_numeric_fields_are_strict(self):
        parse = M["_normalize_positive_int"]
        for value, expected in ((3, 3), ("03", 3), (" 12 ", 12), (2147483647, 2147483647)):
            with self.subTest(value=value):
                self.assertEqual(expected, parse(value))
        for value in (True, False, 0, -1, 2.5, "2.5", "1e3", "-3", "２０１０",
                      2147483648, None, [1]):
            with self.subTest(value=value):
                self.assertIsNone(parse(value))
        self.assertEqual(9999, parse("9999", 9999))
        self.assertIsNone(parse(10000, 9999))

        # 超长数字不得触发 int 字符串位数限制；全零视为 0，前导零 + 小正整数正常解析
        huge = "9" * 5000
        zeros = "0" * 5000
        for value in (huge, zeros, zeros + "0", zeros + "2147483648"):
            with self.subTest(length=len(value)):
                self.assertIsNone(parse(value))
        self.assertEqual(7, parse(zeros + "7"))
        self.assertEqual(2010, parse(zeros + "2010", 9999))
        self.assertIsNone(parse(huge, 9999))
        self.assertIsNone(parse(zeros + "10000", 9999))

    def test_audio_tags_probed_preserves_source_semantics(self):
        missing = object()
        cases = (
            (True, True, None),
            (False, False, "为 false"),
            (None, missing, "为 null"),
            ("true", missing, "类型无效"),
            (1, missing, "类型无效"),
            (missing, missing, "缺失"),
        )
        for raw, expected, reason_text in cases:
            with self.subTest(raw=raw):
                metadata = {"Title": "歌"}
                if raw is not missing:
                    metadata["AudioTagsProbed"] = raw
                notices = []
                payload, reason = M["build_jellyfin_cache_payload"](
                    [{"MediaSourceInfo": {"MediaStreams": [{"Type": "Audio", "Index": 0}]},
                      "AudioMetadata": metadata}],
                    strm_hash="H",
                    notices=notices,
                )
                self.assertIsNone(reason)
                if expected is missing:
                    self.assertNotIn("audioTagsProbed", payload)
                else:
                    self.assertIs(expected, payload["audioTagsProbed"])
                if reason_text:
                    self.assertTrue(any(code == "audio_tags_unprobed" and reason_text in detail
                                        for code, detail in notices))
                else:
                    self.assertFalse(any(code == "audio_tags_unprobed" for code, _ in notices))

    def test_audio_is_included_by_default_and_skip_audio_excludes_it(self):
        source = self.audio_source(audio_metadata={"AudioTagsProbed": True, "Title": "歌"})
        target = source.with_name("song.strmtool.json")
        result = self.cli(source, "--dry-run")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("跳过纯音频: 0", result.stdout)
        self.assertFalse(target.exists())

        result = self.cli(source)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertTrue(target.exists())
        payload = json.loads(target.read_text(encoding="utf-8"))
        self.assertEqual("歌", payload["title"])
        self.assertIs(True, payload["audioTagsProbed"])

        target.unlink()
        result = self.cli(source, "--skip-audio")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("跳过纯音频: 1", result.stdout)
        self.assertFalse(target.exists())

        result = self.cli(source, "--include-audio")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertNotIn("--include-audio", result.stderr)
        self.assertTrue(target.exists())

    def test_mutually_exclusive_audio_flags_fail_without_writing(self):
        source = self.audio_source(audio_metadata={"AudioTagsProbed": True})
        out = self.root / "out"
        result = self.cli(source, "--include-audio", "--skip-audio", "--output-dir", out)
        self.assertEqual(2, result.returncode)
        self.assertIn("not allowed with argument", result.stderr)
        self.assertFalse(out.exists())

    def test_legacy_audio_backup_migrates_streams_and_reports_missing_tags(self):
        source = self.audio_source()
        result = self.cli(source)
        self.assertEqual(0, result.returncode, result.stderr)
        target = source.with_name("song.strmtool.json")
        payload = json.loads(target.read_text(encoding="utf-8"))
        self.assertEqual([0], [stream["Type"] for stream in payload["mediaStreams"]])
        self.assertNotIn("audioTagsProbed", payload)
        self.assertIn("音频标签提示", result.stderr)
        self.assertIn("缺少 AudioMetadata", result.stderr)
        self.assertIn("--overwrite", result.stderr)
        self.assertNotIn("已忽略无效字段: AudioTagsProbed", result.stderr)
        self.assertNotIn("Traceback", result.stderr)

    def test_invalid_audio_tags_do_not_abort_batch(self):
        good = self.audio_source(
            "good", "good-song",
            audio_metadata={"AudioTagsProbed": True, "Title": "好歌", "Album": "专辑"})
        bad = self.audio_source(
            "bad", "bad-song",
            audio_metadata={"AudioTagsProbed": "true", "Title": 5, "Album": "可用专辑"})
        result = self.cli(self.root)
        self.assertEqual(0, result.returncode, result.stderr)
        good_payload = json.loads(good.with_name("good-song.strmtool.json").read_text(encoding="utf-8"))
        bad_payload = json.loads(bad.with_name("bad-song.strmtool.json").read_text(encoding="utf-8"))
        self.assertEqual("好歌", good_payload["title"])
        self.assertIs(True, good_payload["audioTagsProbed"])
        self.assertNotIn("title", bad_payload)
        self.assertNotIn("audioTagsProbed", bad_payload)
        self.assertEqual("可用专辑", bad_payload["album"])
        self.assertIn("已忽略无效字段", result.stderr)
        self.assertIn("1 个纯音频条目没有可信的 audioTagsProbed: true", result.stderr)
        self.assertNotIn("Traceback", result.stderr)

    def test_oversized_numeric_tags_do_not_abort_batch(self):
        huge = "9" * 5000
        zeros = "0" * 5000
        first = self.audio_source("huge-track", "huge-track", audio_metadata={
            "AudioTagsProbed": True,
            "Album": "可用专辑",
            "TrackNumber": huge,
            "DiscNumber": zeros,
            "ProductionYear": zeros + "2010",
        })
        second = self.audio_source("huge-year", "huge-year", audio_metadata={
            "AudioTagsProbed": True,
            "Title": "可用标题",
            "TrackNumber": zeros + "7",
            "ProductionYear": huge,
        })
        result = self.cli(self.root)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertNotIn("Traceback", result.stderr)
        self.assertIn("已忽略无效字段", result.stderr)

        first_payload = json.loads(
            first.with_name("huge-track.strmtool.json").read_text(encoding="utf-8"))
        second_payload = json.loads(
            second.with_name("huge-year.strmtool.json").read_text(encoding="utf-8"))
        self.assertEqual("可用专辑", first_payload["album"])
        self.assertEqual(2010, first_payload["productionYear"])
        self.assertNotIn("trackNumber", first_payload)
        self.assertNotIn("discNumber", first_payload)
        self.assertEqual([0], [s["Type"] for s in first_payload["mediaStreams"]])
        self.assertEqual("可用标题", second_payload["title"])
        self.assertEqual(7, second_payload["trackNumber"])
        self.assertNotIn("productionYear", second_payload)
        self.assertEqual([0], [s["Type"] for s in second_payload["mediaStreams"]])

    def test_video_entries_never_receive_music_tags(self):
        metadata = {"AudioTagsProbed": True, "Title": "MV 标题", "Album": "专辑", "Artists": ["歌手"]}
        payload, reason = M["build_jellyfin_cache_payload"](
            [{"MediaSourceInfo": {"MediaStreams": [
                {"Type": "Video", "Codec": "h264", "Index": 0},
                {"Type": "Audio", "Codec": "aac", "Index": 1},
            ]}, "AudioMetadata": metadata}],
            strm_hash="H",
        )
        self.assertIsNone(reason)
        for key in ("audioTagsProbed", "title", "album", "artists", "albumArtists",
                    "trackNumber", "discNumber", "productionYear", "genres"):
            self.assertNotIn(key, payload)

    def test_lyric_priority_index_and_no_fabrication(self):
        folder = self.root / "lyrics"
        folder.mkdir()
        for name in ("song.lrc", "song.elrc", "song.txt"):
            (folder / name).write_text("lyrics", encoding="utf-8")
        streams = [{"Type": "Audio", "Codec": "flac", "Index": 4}]
        payload, reason = M["build_jellyfin_cache_payload"](
            [{"MediaSourceInfo": {"MediaStreams": streams + [
                {"Type": "Subtitle", "Codec": "text", "Index": 5},
                {"Type": "Subtitle", "Codec": "lrc", "Index": 6, "IsExternal": True, "Path": "song.lrc"},
            ]}}],
            strm_hash="H",
            strm_dir=str(folder),
            base_name="song",
        )
        self.assertIsNone(reason)
        self.assertEqual([0], [s["Type"] for s in payload["mediaStreams"] if s["Type"] != 5])
        lyrics = [s for s in payload["mediaStreams"] if s["Type"] == 5]
        self.assertEqual(1, len(lyrics))
        self.assertEqual("song.lrc", lyrics[0]["Path"])
        self.assertEqual(5, lyrics[0]["Index"])
        self.assertFalse(lyrics[0]["IsExternal"])
        self.assertNotIn("IsExternalUrl", lyrics[0])

        no_lyrics = self.root / "no-lyrics"
        no_lyrics.mkdir()
        payload, reason = M["build_jellyfin_cache_payload"](
            [{"MediaSourceInfo": {"MediaStreams": streams}}],
            strm_hash="H",
            strm_dir=str(no_lyrics),
            base_name="song",
        )
        self.assertIsNone(reason)
        self.assertEqual([], [s for s in payload["mediaStreams"] if s["Type"] == 5])

        embedded_only = self.root / "embedded"
        embedded_only.mkdir()
        payload, reason = M["build_jellyfin_cache_payload"](
            [{"MediaSourceInfo": {"MediaStreams": streams + [
                {"Type": "Subtitle", "Codec": "text", "Index": 5},
            ]}}],
            strm_hash="H",
            strm_dir=str(embedded_only),
            base_name="song",
        )
        self.assertIsNone(reason)
        self.assertEqual([], [s for s in payload["mediaStreams"] if s["Type"] in (2, 5)])

    def test_existing_audio_cache_requires_explicit_overwrite(self):
        source = self.audio_source(
            audio_metadata={"AudioTagsProbed": True, "Title": "新标题"})
        target = source.with_name("song.strmtool.json")
        target.write_text('{"legacy": true}', encoding="utf-8")
        result = self.cli(source)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual('{"legacy": true}', target.read_text(encoding="utf-8"))
        result = self.cli(source, "--overwrite")
        self.assertEqual(0, result.returncode, result.stderr)
        payload = json.loads(target.read_text(encoding="utf-8"))
        self.assertEqual("新标题", payload["title"])

    def test_output_dir_writes_only_json_and_leaves_sources_untouched(self):
        source = self.audio_source(
            audio_metadata={"AudioTagsProbed": True, "Title": "歌"})
        lrc = source.parent / "song.lrc"
        lrc.write_text("lyrics", encoding="utf-8")
        originals = {
            path: path.read_bytes()
            for path in (source, source.parent / "song.strm", lrc)
        }
        out = self.root / "out"
        result = self.cli(self.root, "--output-dir", out)
        self.assertEqual(0, result.returncode, result.stderr)
        produced = sorted(path.relative_to(out).as_posix() for path in out.rglob("*") if path.is_file())
        self.assertEqual(["music/song.strmtool.json"], produced)
        for path, content in originals.items():
            self.assertEqual(content, path.read_bytes(), path.name)

    def test_audio_tag_notice_samples_are_bounded(self):
        for index in range(8):
            self.audio_source(f"legacy-{index}", f"song-{index}")
        result = self.cli(self.root)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("8 个纯音频条目没有可信的 audioTagsProbed: true", result.stderr)
        self.assertEqual(5, result.stderr.count("示例:"))

    def test_dry_run_shows_audio_tag_notices_without_writing(self):
        source = self.audio_source()
        result = self.cli(source, "--dry-run")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("音频标签提示", result.stderr)
        self.assertFalse(source.with_name("song.strmtool.json").exists())

    def test_bom_hashes(self):
        target = "https://example.com/电影"
        expected = hashlib.sha256(target.encode()).hexdigest().upper()
        path = self.root / "encoding.strm"
        for encoding, bom in (("utf-8", b""), ("utf-8", codecs.BOM_UTF8),
                              ("utf-16-le", codecs.BOM_UTF16_LE), ("utf-16-be", codecs.BOM_UTF16_BE),
                              ("utf-32-le", codecs.BOM_UTF32_LE), ("utf-32-be", codecs.BOM_UTF32_BE)):
            with self.subTest(encoding=encoding, bom=bom):
                path.write_bytes(bom + ("\r\n  " + target + " \r\nignored").encode(encoding))
                self.assertEqual(expected, M["compute_strm_content_hash"](str(path)))

    def test_conflicting_outputs_fail_before_any_write(self):
        a, b = self.source("a"), self.source("b")
        out = self.root / "out"
        for flags in ([], ["--overwrite"], ["--dry-run"]):
            result = self.cli(a, b, "--output-dir", out, *flags)
            self.assertEqual(1, result.returncode, result.stderr)
            self.assertIn("输出路径冲突", result.stderr)
            self.assertFalse(out.exists())

    def test_overlapping_inputs_are_deduplicated(self):
        source = self.source()
        tasks = M["collect_tasks"]([str(source.parent), str(source)])
        self.assertEqual(1, len(M["prepare_tasks"](tasks, None)))

    def test_atomic_publication_never_overwrites(self):
        target = str(self.root / "cache.json")
        barrier = threading.Barrier(2)

        def publish(value):
            barrier.wait(timeout=5)
            try:
                M["write_json_atomically"](target, {"value": value})
                return "created", value
            except FileExistsError:
                return "exists", value

        with concurrent.futures.ThreadPoolExecutor(2) as executor:
            results = list(executor.map(publish, (1, 2)))
        self.assertEqual(["created", "exists"], sorted(r[0] for r in results))
        winner = next(value for status, value in results if status == "created")
        self.assertEqual({"value": winner}, json.loads(Path(target).read_text()))
        self.assertEqual([], list(self.root.glob("*.tmp")))
        M["write_json_atomically"](target, {"value": 3}, overwrite=True)
        self.assertEqual({"value": 3}, json.loads(Path(target).read_text()))

    def test_existing_cache_and_dry_run(self):
        source = self.source()
        target = source.parent / "movie.strmtool.json"
        self.assertEqual(0, self.cli(source, "--dry-run").returncode)
        self.assertFalse(target.exists())
        target.write_text("original")
        self.assertEqual(0, self.cli(source).returncode)
        self.assertEqual("original", target.read_text())

    def test_bad_files_do_not_abort_batch(self):
        good = self.source(name="good")
        bad = self.source(name="bad")
        bad.with_name("bad.strm").write_bytes(b"\xffinvalid")
        self.source(name="bad-number", streams=[{"Type": "Video", "Width": float("inf")}])
        result = self.cli(good.parent)
        self.assertEqual(1, result.returncode)
        self.assertIn("错误: 2", result.stdout)
        self.assertNotIn("Traceback", result.stderr)
        self.assertTrue(good.with_name("good.strmtool.json").exists())

    def test_unsupported_hard_links_fail_without_partial_output(self):
        target = self.root / "cache.json"
        with patch("os.link", side_effect=OSError("hard links unsupported")):
            with self.assertRaises(OSError):
                M["write_json_atomically"](str(target), {"value": 1})
        self.assertFalse(target.exists())
        self.assertEqual([], list(self.root.glob("*.tmp")))

    @unittest.skipUnless(shutil.which("dotnet"), "需要 .NET SDK 验证实际 Jellyfin 缓存读取")
    def test_jellyfin_reads_migrated_cache(self):
        streams = [
            {"Type": "Video", "Codec": "hevc", "ExtendedVideoType": "Hdr10"},
            {"Type": "Video", "Codec": "hevc", "ExtendedVideoType": "HyperLogGamma"},
            {"Type": "Audio", "Codec": "truehd", "Profile": "TrueHD", "Title": "Atmos"},
            {"Type": "Audio", "Codec": "eac3", "Profile": "EAC3", "Title": "Atmos"},
            {"Type": "Audio", "Codec": "dts", "Profile": "DTS-HD MA", "Title": "DTS:X"},
            {"Type": "Subtitle", "Codec": "srt", "DeliveryMethod": "Embed"},
        ]
        paths = []
        for encoding in ("utf-8-sig", "utf-16", "utf-32"):
            source = self.source(encoding, streams=streams)
            strm = source.with_name("movie.strm")
            strm.write_text("\n https://example.com/电影 \n", encoding=encoding)
            paths.append(strm)
            result = self.cli(source)
            self.assertEqual(0, result.returncode, result.stderr)
        project = self.root / "Check.csproj"
        project.write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
            '<TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup>'
            f'<ProjectReference Include="{escape(str(ROOT / "StrmTool.csproj"))}" />'
            '</ItemGroup></Project>')
        (self.root / "Program.cs").write_text('''
using System;
using StrmTool;
using Microsoft.Extensions.Logging.Abstractions;
class Program {
    static void Check(bool ok) { if (!ok) throw new Exception("Cache assertion failed"); }
    static void Main(string[] paths) {
        var reader = new MediaInfoCache(NullLogger.Instance);
        foreach (var path in paths) {
            Check(reader.TryGetFullCache(path, out var cache, verifyContentHash: true));
            var s = cache.MediaStreams;
            Check(s[0].VideoRangeType.ToString() == "HDR10");
            Check(s[1].VideoRangeType.ToString() == "HLG");
            Check(s[2].AudioSpatialFormat.ToString() == "DolbyAtmos");
            Check(s[3].AudioSpatialFormat.ToString() == "DolbyAtmos");
            Check(s[4].AudioSpatialFormat.ToString() == "DTSX");
            Check(s[5].DeliveryMethod.ToString() == "Embed");
        }
        Console.WriteLine("Jellyfin cache validation passed");
    }
}
''')
        result = subprocess.run(["dotnet", "run", "--project", str(project), "--", *map(str, paths)],
                                capture_output=True, text=True, timeout=180)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("Jellyfin cache validation passed", result.stdout)

    @unittest.skipUnless(shutil.which("dotnet"), "需要 .NET SDK 验证音乐缓存读取与恢复")
    def test_jellyfin_reads_migrated_music_cache_and_restores(self):
        music_dir = self.root / "music"
        music_dir.mkdir()
        source = self.audio_source(
            "music",
            "song",
            streams=[
                {"Type": "Audio", "Codec": "flac", "Index": 0},
                {"Type": "Subtitle", "Codec": "text", "Index": 1},
            ],
            audio_metadata={
                "AudioTagsProbed": True,
                "Title": "雪地上的回忆",
                "Album": "皇牌影视金曲",
                "Artists": ["陈佳", "李四"],
                "AlbumArtists": ["陈佳"],
                "Genres": ["Pop", "华语"],
                "TrackNumber": 3,
                "DiscNumber": 2,
                "ProductionYear": 2010,
            },
        )
        (music_dir / "song.lrc").write_text("[00:00.00]lyrics", encoding="utf-8")
        (music_dir / "song.txt").write_text("plain lyrics", encoding="utf-8")
        result = self.cli(source)
        self.assertEqual(0, result.returncode, result.stderr)

        legacy_dir = self.root / "legacy"
        legacy_dir.mkdir()
        legacy_source = self.audio_source(
            "legacy",
            "old-song",
            streams=[{"Type": "Audio", "Codec": "mp3", "Index": 0}],
            audio_metadata={
                "Title": "旧标题",
                "Album": "旧专辑",
                "Artists": ["旧歌手"],
            },
        )
        result = self.cli(legacy_source)
        self.assertEqual(0, result.returncode, result.stderr)
        legacy_payload = json.loads(
            legacy_source.with_name("old-song.strmtool.json").read_text(encoding="utf-8"))
        self.assertNotIn("audioTagsProbed", legacy_payload)

        project = self.root / "CheckMusic.csproj"
        project.write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
            '<TargetFramework>net10.0</TargetFramework><Nullable>disable</Nullable></PropertyGroup><ItemGroup>'
            f'<ProjectReference Include="{escape(str(ROOT / "StrmTool.csproj"))}" />'
            '</ItemGroup></Project>')
        (self.root / "Program.cs").write_text('''
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging.Abstractions;
using StrmTool;

class Program
{
    static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception("Assertion failed: " + message);
    }

    static T Proxy<T>(Func<MethodInfo, object[], object> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, TestProxy>();
        ((TestProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    public class TestProxy : DispatchProxy
    {
        public Func<MethodInfo, object[], object> Handler { get; set; }
        protected override object Invoke(MethodInfo method, object[] args) => Handler(method, args);
    }

    sealed class TestAudio : Audio, IHasMediaSources
    {
        public List<MediaStream> Streams { get; set; } = new List<MediaStream>();
        public int Saves { get; private set; }
        IReadOnlyList<MediaStream> IHasMediaSources.GetMediaStreams() => Streams;
        public override Task UpdateToRepositoryAsync(ItemUpdateType reason, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Saves++;
            return Task.CompletedTask;
        }
    }

    sealed class TestMusicAlbum : MusicAlbum
    {
        public int Saves { get; private set; }
        public override Task UpdateToRepositoryAsync(ItemUpdateType reason, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Saves++;
            return Task.CompletedTask;
        }
    }

    sealed class Harness : ExtractTask
    {
        public Harness(ILibraryManager library, IMediaEncoder encoder, IMediaStreamRepository repository)
            : base(library, encoder, repository, NullLogger<ExtractTask>.Instance)
        {
            _config = new PluginConfiguration
            {
                EnableAutoExtract = false,
                EnableMediaInfoCache = true,
                ForceRefreshIgnoreCache = false,
                ForceRefreshIgnoreExisting = false,
                RefreshDelayMs = 0
            };
        }
    }

    static ILibraryManager CreateLibrary(
        Dictionary<Guid, BaseItem> items,
        Dictionary<Guid, List<PersonInfo>> people,
        MusicArtist artist)
    {
        return Proxy<ILibraryManager>((method, args) =>
        {
            switch (method.Name)
            {
                case nameof(ILibraryManager.GetItemById):
                    return items.TryGetValue((Guid)args[0], out var found) ? found : null;
                case nameof(ILibraryManager.GetPeople):
                    return (IReadOnlyList<PersonInfo>)new List<PersonInfo>();
                case nameof(ILibraryManager.UpdatePeople):
                    people[((BaseItem)args[0]).Id] = ((IReadOnlyList<PersonInfo>)args[1]).ToList();
                    return null;
                case nameof(ILibraryManager.GetArtist):
                    return artist;
                default:
                    return null;
            }
        });
    }

    static async Task Main(string[] args)
    {
        string musicDir = args[0];
        string legacyDir = args[1];
        var reader = new MediaInfoCache(NullLogger.Instance);

        string musicStrm = Path.Combine(musicDir, "song.strm");
        Check(reader.TryGetFullCache(musicStrm, out var cache, verifyContentHash: true),
            "music cache must pass content hash verification");
        Check(cache.AudioTagsProbed == true, "audioTagsProbed must stay true");
        Check(cache.Title == "雪地上的回忆", "title");
        Check(cache.Album == "皇牌影视金曲", "album");
        Check(cache.Artists.SequenceEqual(new[] { "陈佳", "李四" }), "artists");
        Check(cache.AlbumArtists.SequenceEqual(new[] { "陈佳" }), "albumArtists");
        Check(cache.TrackNumber == 3, "trackNumber");
        Check(cache.DiscNumber == 2, "discNumber");
        Check(cache.ProductionYear == 2010, "productionYear");
        Check(cache.Genres.SequenceEqual(new[] { "Pop", "华语" }), "genres");
        Check(cache.MediaStreams.Any(s => s.Type == MediaStreamType.Audio), "audio stream");
        Check(!cache.MediaStreams.Any(s => s.Type == MediaStreamType.Subtitle), "lyric pseudo subtitle removed");
        var cachedLyric = cache.MediaStreams.Single(s => s.Type == MediaStreamType.Lyric);
        Check(cachedLyric.Path == Path.Combine(musicDir, "song.lrc"), "lyric path restored to absolute: " + cachedLyric.Path);
        Check(cache.MediaStreams.Where(s => s.Type != MediaStreamType.Lyric).All(s => s.Index < cachedLyric.Index),
            "lyric index after internal streams");

        var artist = new MusicArtist { Id = Guid.NewGuid(), Name = "陈佳", Path = Path.Combine(musicDir, "陈佳") };
        var album = new TestMusicAlbum
        {
            Id = Guid.NewGuid(),
            ParentId = artist.Id,
            Name = "皇牌影视金曲",
            Path = musicDir
        };
        var audio = new TestAudio
        {
            Id = Guid.NewGuid(),
            ParentId = album.Id,
            Name = "song",
            Path = musicStrm
        };
        var people = new Dictionary<Guid, List<PersonInfo>>();
        var items = new Dictionary<Guid, BaseItem>
        {
            [artist.Id] = artist,
            [album.Id] = album,
            [audio.Id] = audio
        };
        int probeCalls = 0;
        var encoder = Proxy<IMediaEncoder>((method, args2) =>
        {
            if (method.Name == nameof(IMediaEncoder.GetMediaInfo)) probeCalls++;
            return Task.FromResult<MediaInfo>(null);
        });
        var repository = Proxy<IMediaStreamRepository>((method, args2) =>
        {
            if (method.Name == nameof(IMediaStreamRepository.SaveMediaStreams))
                audio.Streams = ((IReadOnlyList<MediaStream>)args2[1]).ToList();
            return null;
        });
        var library = CreateLibrary(items, people, artist);

        using (var task = new Harness(library, encoder, repository))
        {
            await task.ExtractSingleItemAsync(audio, CancellationToken.None);
        }

        Check(probeCalls == 0, "probed cache must not call encoder");
        Check(audio.Name == "雪地上的回忆", "restored title");
        Check(audio.Album == "皇牌影视金曲", "restored album");
        Check(audio.Artists.SequenceEqual(new[] { "陈佳", "李四" }), "restored artists");
        Check(audio.AlbumArtists.SequenceEqual(new[] { "陈佳" }), "restored albumArtists");
        Check(audio.IndexNumber == 3, "restored trackNumber");
        Check(audio.ParentIndexNumber == 2, "restored discNumber");
        Check(audio.ProductionYear == 2010, "restored productionYear");
        Check(audio.Genres.SequenceEqual(new[] { "Pop", "华语" }), "restored genres");
        Check(audio.HasLyrics == true, "lyrics flag restored");
        Check(audio.LyricFiles.Contains(Path.Combine(musicDir, "song.lrc")), "lyric file restored");
        Check(audio.Streams.Any(s => s.Type == MediaStreamType.Lyric), "lyric stream saved");
        Check(audio.Streams.Single(s => s.Type == MediaStreamType.Lyric).Path == Path.Combine(musicDir, "song.lrc"),
            "saved lyric stream path");
        Check(album.AlbumArtists.Contains("陈佳"), "parent album artists synced");
        Check(album.Artists.Contains("陈佳"), "parent album artists list synced");
        Check(album.ProductionYear == 2010, "parent album year synced");
        Check(album.Genres.Contains("Pop"), "parent album genres synced");
        Check(album.Saves > 0, "parent album persisted");
        Check(people.ContainsKey(audio.Id), "audio people synced");

        using (var task = new Harness(library, encoder, repository))
        {
            await task.ExtractSingleItemAsync(audio, CancellationToken.None);
        }
        Check(probeCalls == 0, "repeated extraction must still not call encoder");

        string legacyStrm = Path.Combine(legacyDir, "old-song.strm");
        Check(reader.TryGetFullCache(legacyStrm, out var legacyCache, verifyContentHash: true),
            "legacy cache readable");
        Check(legacyCache.AudioTagsProbed == null, "legacy cache keeps missing probed flag");
        Check(legacyCache.Title == "旧标题", "legacy cache still migrates available tags");

        var legacyAlbum = new TestMusicAlbum
        {
            Id = Guid.NewGuid(),
            Name = "旧专辑",
            Path = legacyDir
        };
        var legacyAudio = new TestAudio
        {
            Id = Guid.NewGuid(),
            ParentId = legacyAlbum.Id,
            Name = "old-song",
            Path = legacyStrm
        };
        var legacyItems = new Dictionary<Guid, BaseItem>
        {
            [legacyAlbum.Id] = legacyAlbum,
            [legacyAudio.Id] = legacyAudio
        };
        int legacyProbeCalls = 0;
        var probeInfo = new MediaInfo
        {
            Name = "探测标题",
            Album = "探测专辑",
            Artists = new[] { "探测歌手" },
            AlbumArtists = new[] { "探测歌手" },
            IndexNumber = 7,
            ParentIndexNumber = 1,
            ProductionYear = 2020,
            Genres = new[] { "Rock" },
            Size = 1234567,
            RunTimeTicks = 1800000000,
            Container = "mp3",
            Bitrate = 320000,
            MediaStreams = new List<MediaStream>
            {
                new MediaStream { Index = 0, Type = MediaStreamType.Audio, Codec = "mp3" }
            }
        };
        var legacyEncoder = Proxy<IMediaEncoder>((method, args2) =>
        {
            if (method.Name != nameof(IMediaEncoder.GetMediaInfo)) return null;
            legacyProbeCalls++;
            return Task.FromResult(probeInfo);
        });
        var legacyRepository = Proxy<IMediaStreamRepository>((method, args2) =>
        {
            if (method.Name == nameof(IMediaStreamRepository.SaveMediaStreams))
                legacyAudio.Streams = ((IReadOnlyList<MediaStream>)args2[1]).ToList();
            return null;
        });
        var legacyPeople = new Dictionary<Guid, List<PersonInfo>>();
        var legacyLibrary = CreateLibrary(legacyItems, legacyPeople, null);

        using (var task = new Harness(legacyLibrary, legacyEncoder, legacyRepository))
        {
            await task.ExtractSingleItemAsync(legacyAudio, CancellationToken.None);
        }

        Check(legacyProbeCalls == 1, "unprobed cache must keep controlled probe behavior");
        Check(legacyAudio.Name == "探测标题", "probe metadata applied");
        Check(legacyAudio.Album == "探测专辑", "probe album applied");
        Check(reader.TryGetFullCache(legacyStrm, out var upgraded, verifyContentHash: true), "upgraded cache readable");
        Check(upgraded.AudioTagsProbed == true, "probe upgrades cache probed flag");

        Console.WriteLine("Jellyfin music cache validation passed");
    }
}
''')
        result = subprocess.run(
            ["dotnet", "run", "--project", str(project), "--", str(music_dir), str(legacy_dir)],
            capture_output=True, text=True, timeout=300)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("Jellyfin music cache validation passed", result.stdout)


if __name__ == "__main__":
    unittest.main()
