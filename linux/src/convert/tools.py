"""Finding the external tools, asking them what they can do, and running one.

Mirrors ``Tools.cs`` / ``Process.swift``. On Linux the tools are the point:
there is no ImageIO or textutil here, so images are ImageMagick, documents are
LibreOffice and a browser, PDFs are Poppler, and fonts are fontTools. Every one
of them is looked for on each use, so installing one while Convert is open
takes effect without a restart, and what a format needs is asked of the tool
itself rather than assumed.
"""

from __future__ import annotations

import os
import shutil
import subprocess
import threading

from .errors import ConvertError, Stopped

# Candidates per tool, in the order they're preferred. LibreOffice ships both
# names depending on the distro and the package.
_CANDIDATES = {
    "ffmpeg": ["ffmpeg"],
    "ffprobe": ["ffprobe"],
    "soffice": ["soffice", "libreoffice"],
    "pdftoppm": ["pdftoppm"],
    "pdftotext": ["pdftotext"],
    "pdfinfo": ["pdfinfo"],
    "magick": ["magick"],
    # ImageMagick 6 has no `magick` driver; its converter is `convert`.
    "convert": ["convert"],
    "cwebp": ["cwebp"],
    "dwebp": ["dwebp"],
    # Any Chromium-family browser can print a page to PDF. Brave and Chrome are
    # preferred because they are the ones this gets tested against.
    "chrome": [
        "google-chrome-stable", "google-chrome", "chromium", "chromium-browser",
        "brave-browser", "brave", "microsoft-edge-stable", "vivaldi-stable",
    ],
}


def find(tool):
    """The path of a tool, or None. Checked on every call -- see the module note."""
    for name in _CANDIDATES.get(tool, [tool]):
        path = shutil.which(name)
        if path:
            return path
    return None


def has(tool):
    return find(tool) is not None


def magick():
    """The command that converts an image: ImageMagick 7's `magick`, else 6's `convert`."""
    return find("magick") or find("convert")


# ── What a tool can do, asked once per installed copy ────────────────────


_cache = {}
_cache_lock = threading.Lock()


def _cached(key, produce):
    with _cache_lock:
        if key in _cache:
            return _cache[key]
    value = produce()
    with _cache_lock:
        _cache[key] = value
    return value


def encoders(ffmpeg):
    """The encoder names compiled into this ffmpeg."""
    def ask():
        try:
            out = run([ffmpeg, "-hide_banner", "-encoders"], timeout=20)[1]
        except ConvertError:
            return set()
        names = set()
        for line in out.splitlines():
            parts = line.split()
            # " V..... libx264              H.264 / AVC"
            if len(parts) >= 2 and len(parts[0]) == 6 and parts[0][0] in "VAS":
                names.add(parts[1])
        return names
    return _cached(("encoders", ffmpeg), ask)


def has_encoder(name):
    ffmpeg = find("ffmpeg")
    return bool(ffmpeg) and name in encoders(ffmpeg)


def magick_writes(ext):
    """Whether ImageMagick can WRITE this format here.

    Asked by writing one: a distro's policy.xml can deny a coder (PDF writing is
    denied on many), and a format can be missing a delegate (HEIC needs libheif,
    AVIF needs libavif) while the tool itself is installed. The answer decides
    whether the format is offered, so it can't be guessed at.
    """
    def ask():
        exe = magick()
        if not exe:
            return False
        import tempfile

        with tempfile.TemporaryDirectory(prefix="convert-probe-") as tmp:
            src = os.path.join(tmp, "probe.png")
            dst = os.path.join(tmp, "probe." + ext)
            try:
                run([exe, "-size", "2x2", "xc:white", src], timeout=20)
                run([exe, src, dst], timeout=30)
            except ConvertError:
                return False
            try:
                return os.path.getsize(dst) > 0
            except OSError:
                return False
    return _cached(("magick-writes", ext, magick()), ask)


def fonttools():
    """fontTools and, for WOFF2, Brotli: the two things font conversion needs."""
    def ask():
        try:
            import fontTools  # noqa: F401
        except Exception:
            return (False, False)
        try:
            import brotli  # noqa: F401
            brotli_ok = True
        except Exception:
            brotli_ok = False
        return (True, brotli_ok)
    return _cached("fonttools", ask)


def chrome_version(exe):
    """The browser's version line, which is also the test that it starts at all."""
    def ask():
        try:
            return run([exe, "--version"], timeout=20)[1].strip()
        except ConvertError:
            return ""
    return _cached(("chrome-version", exe), ask)


# ── Running one thing ────────────────────────────────────────────────────


class Runner:
    """Carries Stop: whatever is running is killed, and in-process work checks in between steps."""

    def __init__(self):
        self._stop = threading.Event()
        self._lock = threading.Lock()
        self._proc = None

    @property
    def cancelled(self):
        return self._stop.is_set()

    def cancel(self):
        self._stop.set()
        with self._lock:
            proc = self._proc
        if proc is not None and proc.poll() is None:
            _kill_tree(proc)

    def check(self):
        if self._stop.is_set():
            raise Stopped("Stopped")

    def attach(self, proc):
        with self._lock:
            self._proc = proc
        if self._stop.is_set():
            _kill_tree(proc)

    def detach(self):
        with self._lock:
            self._proc = None


def _kill_tree(proc):
    """Kill the tool and everything it started.

    LibreOffice's own process only starts the real one, and a browser starts a
    dozen, so killing just the child would leave the work running.
    """
    try:
        os.killpg(proc.pid, 15)
    except (ProcessLookupError, PermissionError, OSError):
        try:
            proc.terminate()
        except OSError:
            return
    try:
        proc.wait(timeout=3)
    except subprocess.TimeoutExpired:
        try:
            os.killpg(proc.pid, 9)
        except (ProcessLookupError, PermissionError, OSError):
            try:
                proc.kill()
            except OSError:
                pass


def run(cmd, runner=None, timeout=None, on_line=None):
    """Run a command to completion and return ``(code, stdout, stderr)``.

    The tool's error output is kept whole (up to 64 KB, so a chatty decoder
    can't fill memory); the message a caller shows is the last line of it,
    which is where ffmpeg and friends put the sentence that matters.
    """
    try:
        proc = subprocess.Popen(
            cmd,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            errors="replace",
            # Its own process group, so Stop can take the whole tree.
            start_new_session=True,
        )
    except (OSError, ValueError) as e:
        raise ConvertError("Couldn't start %s: %s" % (os.path.basename(str(cmd[0])), e))

    if runner is not None:
        runner.attach(proc)

    out_lines = []
    err_tail = []
    err_size = 0

    def read_stdout():
        assert proc.stdout is not None
        for line in proc.stdout:
            if on_line is not None:
                on_line(line)
            else:
                out_lines.append(line)

    def read_stderr():
        nonlocal err_size
        assert proc.stderr is not None
        for line in proc.stderr:
            err_tail.append(line)
            err_size += len(line)
            while err_size > 65536 and len(err_tail) > 1:
                err_size -= len(err_tail.pop(0))

    threads = [threading.Thread(target=read_stdout, daemon=True),
               threading.Thread(target=read_stderr, daemon=True)]
    for t in threads:
        t.start()

    try:
        proc.wait(timeout=timeout)
    except subprocess.TimeoutExpired:
        _kill_tree(proc)
        proc.wait()
        raise ConvertError("%s took too long" % os.path.basename(str(cmd[0])))
    finally:
        if runner is not None:
            runner.detach()
        for t in threads:
            t.join(timeout=5)

    if runner is not None and runner.cancelled:
        raise Stopped("Stopped")

    return (proc.returncode, "".join(out_lines), "".join(err_tail))


def last_line(text):
    """The sentence at the end of a tool's error output."""
    for line in reversed((text or "").strip().splitlines()):
        if line.strip():
            return line.strip()
    return ""
