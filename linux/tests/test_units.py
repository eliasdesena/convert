"""The rules that don't need an external tool, and the writers.

Most of what Convert does is a conversation with ffmpeg or ImageMagick, which
the smoke test exercises on real files. What is here is what can be checked
without them: naming, sniffing, settings, availability, Markdown rendering and
the DOCX/ODT/RTF writers -- the parts that decide what happens *around* a tool.
"""

import base64
import os
import shutil
import sys
import tempfile
import unittest
import zipfile

SRC = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "src")
sys.path.insert(0, SRC)

from convert import cli, core, docwrite, formats, kinds, markdown, paths, tools  # noqa: E402
from convert.errors import ConvertError  # noqa: E402

PNG = base64.b64decode(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFAAH/q842iQAAAABJRU5ErkJggg=="
)
JPEG = b"\xff\xd8\xff\xe0\x00\x10JFIF\x00\x01\x01\x00\x00\x01\x00\x01\x00\x00" + b"\x00" * 16


def write(folder, name, data):
    path = os.path.join(folder, name)
    mode = "wb" if isinstance(data, bytes) else "w"
    with open(path, mode, **({} if mode == "wb" else {"encoding": "utf-8"})) as f:
        f.write(data)
    return path


class TempFolderTest(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp(prefix="convert-test-")
        self.addCleanup(shutil.rmtree, self.dir, True)

    def path(self, name):
        return os.path.join(self.dir, name)


class TestNames(TempFolderTest):
    def test_next_free_name(self):
        original = write(self.dir, "song.mp3", b"x")
        self.assertEqual(paths.unique_output(original, "flac"), self.path("song.flac"))
        write(self.dir, "song.flac", b"x")
        self.assertEqual(paths.unique_output(original, "flac"), self.path("song 2.flac"))
        write(self.dir, "song 2.flac", b"x")
        self.assertEqual(paths.unique_output(original, "flac"), self.path("song 3.flac"))

    def test_name_with_dots_keeps_all_but_the_last(self):
        original = write(self.dir, "my.song.v2.wav", b"x")
        self.assertEqual(paths.unique_output(original, "mp3"), self.path("my.song.v2.mp3"))

    def test_never_overwrites(self):
        target = write(self.dir, "taken.mp3", b"original")
        with self.assertRaises(ConvertError):
            paths.write_new(target, b"new")
        with open(target, "rb") as f:
            self.assertEqual(f.read(), b"original")

    def test_move_into_place_keeps_what_is_there(self):
        source = write(self.dir, "temp.out", b"new")
        target = write(self.dir, "taken.mp3", b"original")
        with self.assertRaises(ConvertError):
            paths.move_into_place(source, target)
        with open(target, "rb") as f:
            self.assertEqual(f.read(), b"original")
        self.assertTrue(os.path.exists(source), "the temp file stays until it is moved")

    def test_read_only_folder_falls_back(self):
        if hasattr(os, "geteuid") and os.geteuid() == 0:
            self.skipTest("root can write anywhere")
        locked = os.path.join(self.dir, "locked")
        os.mkdir(locked)
        original = write(locked, "photo.png", PNG)
        os.chmod(locked, 0o500)
        self.addCleanup(os.chmod, locked, 0o700)
        fallback = os.path.join(self.dir, "downloads")
        os.mkdir(fallback)
        old = os.environ.get("CONVERT_FALLBACK_DIR")
        os.environ["CONVERT_FALLBACK_DIR"] = fallback
        self.addCleanup(lambda: os.environ.pop("CONVERT_FALLBACK_DIR", None)
                        if old is None else os.environ.__setitem__("CONVERT_FALLBACK_DIR", old))
        self.assertEqual(paths.unique_output(original, "jpg"),
                         os.path.join(fallback, "photo.jpg"))

    def test_temp_folders_are_separate(self):
        a = paths.temp_dir()
        b = paths.temp_dir()
        self.addCleanup(paths.try_delete, a)
        self.addCleanup(paths.try_delete, b)
        self.assertNotEqual(a, b)
        self.assertTrue(os.path.isdir(a) and os.path.isdir(b))


class TestKinds(TempFolderTest):
    def test_extension_decides_the_kind(self):
        self.assertEqual(kinds.of("/music/Song.MP3"), kinds.AUDIO)
        self.assertEqual(kinds.of("clip.MOV"), kinds.VIDEO)
        self.assertEqual(kinds.of("page.pdf"), kinds.PDF)
        self.assertEqual(kinds.of("font.woff2"), kinds.FONT)
        self.assertIsNone(kinds.of("archive.zip"))
        self.assertIsNone(kinds.of("noextension"))

    def test_video_and_audio_can_be_converted_together(self):
        self.assertTrue(kinds.compatible([kinds.AUDIO, kinds.VIDEO]))
        self.assertFalse(kinds.compatible([kinds.IMAGE, kinds.AUDIO]))
        self.assertTrue(kinds.compatible([kinds.IMAGE]))

    def test_transport_stream_sniffing(self):
        head = bytearray(188 * 4)
        for offset in range(0, len(head), 188):
            head[offset] = 0x47
        self.assertTrue(kinds.is_transport_stream(bytes(head)))

    def test_ts_is_text_or_video(self):
        script = write(self.dir, "types.ts", b"const answer: number = 42;\n")
        self.assertEqual(kinds.kind_of(script), kinds.DOCUMENT)
        stream = bytearray(188 * 4)
        for offset in range(0, len(stream), 188):
            stream[offset] = 0x47
        binary = write(self.dir, "show.ts", bytes(stream))
        self.assertEqual(kinds.kind_of(binary), kinds.VIDEO)

    def test_heic_is_recognised(self):
        actual = kinds.format_of(b"\x00\x00\x00\x18ftypheic\x00\x00\x00\x00mif1heic")
        self.assertEqual(actual.ext, "heic")
        self.assertIn(kinds.IMAGE, actual.kinds)

    def test_renamed_file_is_caught(self):
        path = write(self.dir, "song.mp3", JPEG)
        with self.assertRaises(ConvertError) as caught:
            kinds.verify(path, kinds.AUDIO)
        message = str(caught.exception)
        self.assertIn(".mp3", message)
        self.assertIn("JPEG", message)
        self.assertIn("Rename it to .jpg", message)

    def test_text_named_an_image_is_caught(self):
        path = write(self.dir, "photo.jpg", b"just some words\nand more\n")
        with self.assertRaises(ConvertError):
            kinds.verify(path, kinds.IMAGE)

    def test_pdf_behind_an_image_name_is_allowed(self):
        path = write(self.dir, "drawing.ai", b"%PDF-1.7\n%...\n")
        kinds.verify(path, kinds.IMAGE)  # no exception

    def test_agreeing_contents_pass(self):
        path = write(self.dir, "photo.png", PNG)
        kinds.verify(path, kinds.IMAGE)


class TestFormats(unittest.TestCase):
    def test_the_catalogue_is_the_macs(self):
        self.assertEqual(len(formats.CATALOGUE), 45)
        self.assertEqual(formats.CATALOGUE[0].id, "mp4-h264")
        self.assertEqual(formats.CATALOGUE[-1].id, "font-desktop")
        self.assertEqual(formats.BY_ID["he-aac"].label, "M4A (HE-AAC, small)")
        self.assertEqual(formats.BY_ID["he-aac"].ext, "m4a")
        self.assertEqual(formats.BY_ID["gif"].group, formats.VIDEO)
        self.assertEqual(formats.BY_ID["gif-img"].group, formats.IMAGE)

    def test_every_target_is_in_its_group(self):
        for target in formats.CATALOGUE:
            self.assertIn(target.group, formats.GROUP_LABELS)

    def test_settings_defaults_match_the_mac(self):
        settings = formats.Settings()
        self.assertEqual(settings.videoQuality, 0.65)
        self.assertEqual(settings.imageQuality, 0.85)
        self.assertEqual(settings.pdfDpi, 150)
        self.assertTrue(settings.hardware)
        self.assertTrue(settings.keepAudio)
        self.assertTrue(settings.paperA4)

    def test_settings_are_partial(self):
        settings = formats.Settings.from_json('{"audioBitrate": 192}')
        self.assertEqual(settings.audioBitrate, 192)
        self.assertEqual(settings.sampleRate, 0)

    def test_a_typo_is_an_error(self):
        with self.assertRaises(ValueError) as caught:
            formats.Settings.from_json('{"videoQulaity": 0.5}')
        self.assertIn("videoQulaity", str(caught.exception))

    def test_settings_round_trip(self):
        settings = formats.Settings(videoQuality=0.9, pdfDpi=300)
        again = formats.Settings.from_json(settings.to_json())
        self.assertEqual(again.videoQuality, 0.9)
        self.assertEqual(again.pdfDpi, 300)

    def test_a_format_needing_a_missing_tool_is_not_offered(self):
        # Whatever the machine has, the answer has to agree with it.
        self.assertEqual(formats.available(formats.BY_ID["pdf-png"], kinds.PDF, "pdf"),
                         tools.has("pdftoppm"))
        self.assertEqual(formats.available(formats.BY_ID["pdf-docx"], kinds.PDF, "pdf"),
                         tools.has("pdftotext"))
        self.assertEqual(formats.available(formats.BY_ID["docx"], kinds.DOCUMENT, "docx"),
                         tools.has("soffice"))
        self.assertEqual(formats.available(formats.BY_ID["pdf-doc"], kinds.DOCUMENT, "docx"),
                         tools.has("soffice"))
        self.assertEqual(formats.available(formats.BY_ID["pdf-doc"], kinds.DOCUMENT, "md"),
                         tools.has("chrome"))
        have, brotli = tools.fonttools()
        self.assertEqual(formats.available(formats.BY_ID["font-woff2"], kinds.FONT, "ttf"),
                         have and brotli)

    def test_pdf_targets_need_a_pdf(self):
        self.assertFalse(formats.available(formats.BY_ID["pdf-png"], kinds.IMAGE, "png"))

    def test_markdown_and_text_need_nothing(self):
        self.assertTrue(formats.available(formats.BY_ID["html"], kinds.DOCUMENT, "md"))
        self.assertTrue(formats.available(formats.BY_ID["txt"], kinds.DOCUMENT, "md"))

    def test_a_video_is_offered_audio(self):
        offered = {t.id for t in formats.CATALOGUE
                   if t.group in kinds.GROUPS_FOR_KIND[kinds.VIDEO]}
        self.assertIn("mp3", offered)
        self.assertIn("mp4-h264", offered)
        self.assertNotIn("jpg", offered)

    def test_preferred_skips_the_format_the_file_already_is(self):
        offered = [formats.BY_ID["mp3"], formats.BY_ID["wav"]]
        self.assertEqual(formats.preferred(["/tmp/song.wav"], offered).id, "mp3")


class TestMarkdown(unittest.TestCase):
    def test_headings_and_inline(self):
        html = markdown.to_html("# Title\n\nSome **bold** and *slanted* and `code`.")
        self.assertIn("<h1>Title</h1>", html)
        self.assertIn("<strong>bold</strong>", html)
        self.assertIn("<em>slanted</em>", html)
        self.assertIn("<code>code</code>", html)

    def test_a_star_in_code_stays_a_star(self):
        html = markdown.to_html("Use `a * b` here")
        self.assertIn("<code>a * b</code>", html)
        self.assertNotIn("<em>", html)

    def test_lists_and_tables(self):
        html = markdown.to_html("- one\n- two\n\n| a | b |\n|---|---|\n| 1 | 2 |")
        self.assertIn("<ul>", html)
        self.assertEqual(html.count("<li>"), 2)
        self.assertIn("<table>", html)
        self.assertIn("<th", html)

    def test_code_block_is_escaped(self):
        html = markdown.to_html("```python\nif a < b:\n    print('<x>')\n```")
        self.assertIn("<pre>", html)
        self.assertIn("a &lt; b", html)
        self.assertNotIn("<x>", html)

    def test_links(self):
        html = markdown.to_html("See [the site](https://example.com/a?b=1&c=2).")
        self.assertIn('href="https://example.com/a?b=1&amp;c=2"', html)

    def test_plain_text_drops_the_markup(self):
        text = markdown.plain_text("# Title\n\n**bold** and [link](https://example.com)\n")
        self.assertIn("Title", text)
        self.assertIn("bold", text)
        self.assertNotIn("**", text)
        self.assertNotIn("](", text)

    def test_paper_size(self):
        self.assertIn("size: A4", markdown.page("x"))
        self.assertIn("size: Letter", markdown.page("x", paper_a4=False))

    def test_an_ampersand_in_a_title_is_escaped(self):
        self.assertIn("<title>Fish &amp; Chips</title>", markdown.page("x", "Fish & Chips"))


class TestDocWrite(TempFolderTest):
    def test_paragraphs_are_put_back_together(self):
        paragraphs = docwrite.split_paragraphs("first line\nsecond line\n\nnew paragraph\n")
        self.assertEqual(paragraphs, ["first line second line", "new paragraph"])

    def test_docx(self):
        path = self.path("out.docx")
        docwrite.write_docx(path, ["Hello <world>", "Second"])
        with zipfile.ZipFile(path) as z:
            self.assertIn("word/document.xml", z.namelist())
            self.assertIn("[Content_Types].xml", z.namelist())
            document = z.read("word/document.xml").decode()
        self.assertIn("Hello &lt;world&gt;", document)
        self.assertEqual(document.count("<w:p>"), 2)

    def test_odt(self):
        path = self.path("out.odt")
        docwrite.write_odt(path, ["Hello"])
        with zipfile.ZipFile(path) as z:
            self.assertEqual(z.read("mimetype"), b"application/vnd.oasis.opendocument.text")
            self.assertIn("META-INF/manifest.xml", z.namelist())
            self.assertIn("Hello", z.read("content.xml").decode())
            # The mimetype entry has to be first and uncompressed, or LibreOffice
            # won't open the file.
            first = z.infolist()[0]
            self.assertEqual(first.filename, "mimetype")
            self.assertEqual(first.compress_type, zipfile.ZIP_STORED)

    def test_rtf(self):
        path = self.path("out.rtf")
        docwrite.write_rtf(path, ["A {brace} and a backslash \\ here"])
        with open(path, encoding="ascii") as f:
            rtf = f.read()
        self.assertTrue(rtf.startswith("{\\rtf1"))
        self.assertIn("\\{brace\\}", rtf)
        self.assertIn("\\\\", rtf)
        self.assertTrue(rtf.rstrip().endswith("}"))

    def test_rtf_handles_non_ascii(self):
        path = self.path("out.rtf")
        docwrite.write_rtf(path, ["Zürich — 東京"])
        with open(path, encoding="ascii") as f:
            rtf = f.read()
        self.assertIn("\\u", rtf)
        self.assertTrue(all(ord(c) < 128 for c in rtf))


class TestConvertGuards(TempFolderTest):
    def test_an_unknown_format_is_named_back(self):
        with self.assertRaises(ConvertError) as caught:
            core.target_for("mp9")
        self.assertIn("mp9", str(caught.exception))
        self.assertIn("--list-targets", str(caught.exception))

    def test_a_target_is_found_by_its_label(self):
        self.assertEqual(core.target_for("MP4 (H.264)").id, "mp4-h264")

    def test_a_missing_file_says_so(self):
        with self.assertRaises(ConvertError) as caught:
            core.convert(self.path("gone.mp3"), "wav")
        self.assertIn("gone.mp3", str(caught.exception))

    def test_the_wrong_kind_is_refused_before_any_tool_runs(self):
        path = write(self.dir, "photo.png", PNG)
        with self.assertRaises(ConvertError) as caught:
            core.convert(path, "mp3")
        self.assertIn("can't become", str(caught.exception))

    def test_a_folder_is_not_a_file(self):
        folder = os.path.join(self.dir, "folder.mp3")
        os.mkdir(folder)
        with self.assertRaises(ConvertError):
            core.convert(folder, "wav")

    def test_a_batch_reports_each_failure_and_keeps_going(self):
        batch = core.Batch()
        done, failed = batch.run([self.path("a.mp3"), self.path("b.mp3")], "wav")
        self.assertEqual(done, [])
        self.assertEqual(len(failed), 2)
        self.assertIn("a.mp3", failed[0][1])

    def test_an_unavailable_format_says_what_is_missing(self):
        message = core.not_available_message(formats.BY_ID["mp3"])
        self.assertIn("ffmpeg", message)


class TestCommandLine(TempFolderTest):
    """The two outputs a program reads: the menu's table and a run's lines."""

    def test_matrix_is_a_table_of_known_formats(self):
        import contextlib
        import io
        import json

        buffer = io.StringIO()
        with contextlib.redirect_stdout(buffer):
            self.assertEqual(cli.main(["--matrix-json"]), 0)
        matrix = json.loads(buffer.getvalue())
        self.assertIn("png", matrix)
        ids = {entry[0] for entries in matrix.values() for entry in entries}
        for target_id in ids:
            self.assertIn(target_id, formats.BY_ID)
        for ext, entries in matrix.items():
            self.assertEqual(kinds.of("sample." + ext) is not None, True)
            for target_id, label in entries:
                self.assertEqual(formats.BY_ID[target_id].label, label)

    def test_matrix_offers_what_the_machine_has(self):
        import contextlib
        import io
        import json

        buffer = io.StringIO()
        with contextlib.redirect_stdout(buffer):
            cli.main(["--matrix-json"])
        matrix = json.loads(buffer.getvalue())
        # Markdown and text need nothing, so they are always there; a format
        # that needs a missing tool is not.
        self.assertIn(["html", "HTML"], matrix["md"])
        self.assertEqual(["pdf-png", "PNG (one per page)"] in matrix.get("pdf", []),
                         tools.has("pdftoppm"))

    def test_machine_output_is_progress_then_the_path(self):
        import contextlib
        import io

        source = write(self.dir, "note.md", "# Title\n\ntext\n")
        buffer = io.StringIO()
        with contextlib.redirect_stdout(buffer):
            self.assertEqual(cli.main(["--machine", "html", source]), 0)
        lines = [line for line in buffer.getvalue().splitlines() if line.strip()]
        self.assertTrue(all(line.startswith("PROGRESS ") for line in lines[:-1]), lines)
        self.assertTrue(lines[-1].endswith("note.html"), lines)
        self.assertTrue(os.path.exists(lines[-1]))

    def test_machine_output_reports_a_failure_as_an_error_line(self):
        import contextlib
        import io

        source = write(self.dir, "song.mp3", JPEG)
        buffer = io.StringIO()
        with contextlib.redirect_stdout(buffer):
            self.assertEqual(cli.main(["--machine", "wav", source]), 1)
        output = buffer.getvalue()
        self.assertIn("ERROR ", output)
        self.assertIn("JPEG", output)

    def test_an_unknown_option_says_so(self):
        import contextlib
        import io

        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            self.assertEqual(cli.main(["--nonsense"]), 2)
        self.assertIn("--nonsense", err.getvalue())


if __name__ == "__main__":
    unittest.main()
