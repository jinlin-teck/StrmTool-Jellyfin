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

    def source(self, directory="source", name="movie", streams=None):
        folder = self.root / directory
        folder.mkdir(parents=True, exist_ok=True)
        path = folder / (name + "-mediainfo.json")
        data = [{"MediaSourceInfo": {"MediaStreams": streams or [
            {"Type": "Video", "Codec": "h264", "Index": 0}
        ]}}]
        path.write_text(json.dumps(data), encoding="utf-8")
        (folder / (name + ".strm")).write_text("https://example.com/" + directory, encoding="utf-8")
        return path

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

    def test_include_audio_prints_warning(self):
        source = self.source()
        data = json.loads(source.read_text(encoding="utf-8"))
        data[0]["MediaSourceInfo"]["MediaStreams"] = [{"Type": "Audio", "Codec": "flac", "Index": 0}]
        source.write_text(json.dumps(data), encoding="utf-8")
        result = self.cli(source, "--include-audio", "--dry-run")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("--include-audio", result.stderr)
        self.assertIn("AudioMetadata", result.stderr)

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


if __name__ == "__main__":
    unittest.main()
