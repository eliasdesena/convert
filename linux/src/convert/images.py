"""Images, through ImageMagick (``magick`` on 7, ``convert`` on 6).

The Mac uses ImageIO and the Windows port uses Magick.NET; here the policy is
the same in spirit:

* a file is copied into a plain-name temp file first, because ImageMagick gives
  meaning to parts of a name (``photo[1].jpg``, ``%d``) and a name is just a
  name;
* every conversion runs under memory, map and disk ceilings, so a small file
  claiming to be an enormous picture is refused instead of eating the machine;
* HEIC, AVIF, WebP and PDF are only offered when this ImageMagick can really
  write them -- asked by writing one, see ``tools.magick_writes``.
"""

from __future__ import annotations

import os
import shutil

from . import paths, tools
from .errors import ConvertError

# A photo's EXIF is part of the photo; a converted file keeps it. What is
# dropped is the Colour Management Profile only where the target can't hold it
# -- ImageMagick handles that itself by coder.

CEILINGS = ["-limit", "memory", "512MiB", "-limit", "map", "1GiB", "-limit", "disk", "2GiB"]

EXT_WEBP = "webp"


def convert(input_path, target, settings, runner, progress):
    work = paths.temp_dir()
    try:
        source_ext = input_path.rsplit(".", 1)[-1].lower() if "." in os.path.basename(input_path) else "bin"
        staged = os.path.join(work, "input." + source_ext)
        try:
            shutil.copyfile(input_path, staged)
        except OSError as e:
            raise ConvertError("Couldn't read the file: %s" % (e.strerror or e))
        runner.check()

        out = os.path.join(work, "output." + target.ext)
        quality = int(round(max(0.0, min(1.0, settings.imageQuality)) * 100))

        if target.id == "webp" and tools.find("cwebp"):
            _cwebp(staged, out, quality, runner)
        else:
            _magick(staged, out, target, settings, quality, input_path)
        runner.check()

        if not os.path.exists(out) or os.path.getsize(out) == 0:
            raise ConvertError("Couldn't convert this image")

        dest = paths.unique_output(input_path, target.ext)
        paths.move_into_place(out, dest)
        progress(1.0)
        return [dest]
    finally:
        paths.try_delete(work)


def _cwebp(staged, out, quality, runner):
    """WebP through cwebp, which is the Mac's engine too.

    It only reads PNG, JPEG and TIFF, so a file it refuses (or anything else
    that got here) goes back to ImageMagick rather than failing.
    """
    try:
        tools.run([tools.find("cwebp"), "-quiet", "-q", str(quality), staged, "-o", out],
                  runner=runner, timeout=120)
    except ConvertError:
        if os.path.exists(out):
            paths.try_delete(out)
        image = tools.magick()
        if not image:
            raise
        args = [image] + CEILINGS + [staged, "-quality", str(quality), out]
        tools.run(args, runner=runner)


def _magick(staged, out, target, settings, quality, original):
    image = tools.magick()
    if not image:
        raise ConvertError("ImageMagick isn't installed. Install it with your package "
                           "manager (for example: sudo apt install imagemagick)")
    args = [image] + CEILINGS

    # An SVG has no background of its own; drawing it on white would fill the
    # transparency a web icon is usually made of.
    if staged.lower().endswith(".svg"):
        args += ["-background", "none"]

    # The first frame of an animated GIF or a multi-image HEIC, which is what
    # the Mac keeps too.
    args.append(staged + "[0]" if not _keeps_animation(target) else staged)

    longest = int(settings.maxSize or 0)
    if longest > 0:
        # ">" means "only shrink": a small picture is never blown up into a
        # blurry big one.
        args += ["-resize", "%dx%d>" % (longest, longest)]

    if target.ext in ("jpg", "jpeg", "webp", "avif", "heic", "tiff", "pdf"):
        args += ["-quality", str(quality)]
    if target.ext == "png":
        # Keep the alpha channel, drop the heavy 16-bit depth a screenshot
        # doesn't need.
        args += ["-define", "png:compression-level=9"]
    if target.id == "pdf-img":
        # A PDF is a document, not a preview: no extra JPEG pass over it.
        args += ["-compress", "jpeg"]

    args.append(out)
    code, _, err = tools.run(args, runner=None)
    if code != 0 or not os.path.exists(out):
        raise ConvertError("Couldn't convert this image: %s"
                           % (tools.last_line(err) or "ImageMagick failed"))


def _keeps_animation(target):
    """A GIF stays a GIF: keeping every frame is the point of the format."""
    return target.id == "gif-img"
