<p align="center">
  <img src="docs/icon.png" width="128" height="128" alt="Convert app icon" />
</p>

<h1 align="center">Convert</h1>

<p align="center">
  <strong>Like CloudConvert, but on your Mac.</strong><br />
  Right-click a file in Finder, pick a format, done. Nothing gets uploaded; every conversion runs locally on your machine.
</p>

<p align="center">
  <a href="https://github.com/newtonstudios/convert/releases/latest/download/Convert.zip"><strong>Download for Mac</strong></a>
  &nbsp;·&nbsp;
  <a href="https://convert.newtonbuilds.dev">Website</a>
</p>

<p align="center">
  <img src="docs/screenshot.png" width="460" alt="The Convert panel converting an Opus file to MP3" />
</p>

> [!NOTE]
> **This app is vibe coded.** It started as an afternoon of chatting with an AI coding assistant (Claude) and is now built in the open by a growing group of contributors. The code is small enough to read, but the app is young. Keep your originals (Convert never overwrites them anyway), and please [open an issue](../../issues) if something breaks.

> [!TIP]
> **On Windows?** There's an early Windows 11 port in [`windows/`](windows/README.md), with the same
> formats and a "Convert…" entry right in Explorer's context menu.

---

## What it does

Select one or more files in Finder → right-click → **Quick Actions → Convert…**

A small window opens showing only the formats that make sense for what you selected. Pick one and hit **Convert**. The new file lands next to the original (`song.opus` → `song.mp3`, and if that exists, `song 2.mp3`).

- **Batches:** select as many files as you like (of the same kind: audio, video, images, documents or PDFs) and they're converted one after another with one progress bar. Video and audio can be mixed, since a video can become audio.
- **Move original to Trash:** tick the box and each original goes to the Trash once its conversion has finished. It only happens after the new file exists, and you can always put it back from the Trash.

| You select | You can convert to |
|---|---|
| 🎵 **Audio** (mp3, wav, opus, flac, m4a, ogg, …) | MP3, M4A (AAC), M4A (HE-AAC), WAV, AIFF, FLAC, ALAC, Opus, OGG, WMA, AC3, WavPack, MP2 |
| 🎬 **Video** (mp4, mov, mkv, webm, avi, …) | MP4 (H.264 / HEVC / AV1), MOV (ProRes 422), WebM (VP9), MKV, animated GIF, **or any audio format** (pulls out the soundtrack) |
| 🖼️ **Images** (heic, jpg, png, webp, tiff, raw, …) | JPEG, PNG, HEIC, AVIF, WebP, TIFF, GIF, BMP, PDF |
| 📄 **Documents** (docx, doc, odt, rtf, html, txt, md) | PDF, Word (DOCX / DOC), ODT, RTF, plain text, HTML |
| 📕 **PDF** | PNG or JPEG images (one per page), plain text |

**Settings** live under **Advanced** (collapsed until you open it; remembered per format):

- **Audio:** bitrate (e.g. MP3 VBR or 320/256/192/128 kbps), sample rate, mono/stereo, 16- or 24-bit for lossless formats
- **Video:** quality slider, resolution (4K → 480p, never upscales), frame rate, keep or drop audio, hardware encoding on/off
- **GIF:** width and frame rate
- **Images:** quality slider, max size
- **Documents → PDF:** A4 or US Letter
- **PDF → images:** resolution (72 / 150 / 300 dpi)

You can also open the app on its own and drag files onto the window.

---

## Install

### 1. Install ffmpeg (needed for audio and video)

Convert uses [ffmpeg](https://ffmpeg.org) for audio and video and `cwebp` for WebP images. Images (other than WebP), documents and PDFs work without them. The easiest way to install both is [Homebrew](https://brew.sh):

```bash
brew install ffmpeg webp
```

### 2. Get the app

**Option A: download it.** Grab `Convert.zip` from the [latest release](../../releases/latest), unzip it and move `Convert.app` into your **Applications** folder.

Convert isn't signed with a paid Apple Developer certificate, so the first time you open it macOS will say it can't verify the developer. To open it anyway:

- **macOS 15 and later:** try to open it once, then go to **System Settings → Privacy & Security**, scroll down, click **Open Anyway** and confirm.
- **macOS 14:** right-click `Convert.app` → **Open** → **Open**.

**Option B: build it yourself** (needs Xcode 15 or later):

```bash
git clone https://github.com/newtonstudios/convert.git
cd convert
./build.sh --install
```

This builds the app, puts it in `~/Applications` and sets up the right-click menu for you, so you can skip step 3.

### 3. Turn on the right-click menu

Open Convert. The window has an **Install** button for "Add Convert… to Finder's right-click menu". Click it. (It's also in the menu bar under **Convert → Install Finder Quick Action**.)

Now right-click any supported file in Finder → **Quick Actions → Convert…**

If it doesn't show up, check **System Settings → General → Login Items & Extensions → Finder** (on older macOS: **System Settings → Privacy & Security → Extensions → Finder**) and make sure **Convert** is ticked. On some setups it appears under **Services** instead of **Quick Actions**.

---

## Requirements

- macOS 14 Sonoma or later, on Apple Silicon or Intel
- `ffmpeg` for audio and video, `cwebp` for WebP (see above)

## Privacy

Convert has no network code, no analytics and no accounts. It reads the files you give it and writes the converted files next to them. That's it.

## Uninstall

1. In Convert, choose **Convert → Remove Finder Quick Action** (or delete `~/Library/Services/Convert.workflow`).
2. Delete `Convert.app`.

---

## Known limitations

- **Markdown** covers the common syntax (headings, bold, italic, lists, task lists, tables, code, quotes, links). Footnotes, HTML inside Markdown and images aren't rendered.
- **Excel and PowerPoint** files aren't supported (they'd need LibreOffice).
- **Converting images** keeps only the first frame of an animated GIF or a multi-image HEIC.
- The **downloaded** app isn't notarized by Apple (see the install notes above).

## How it works (for the curious)

It's a small SwiftUI app (~1,800 lines) with no dependencies beyond what's on your Mac:

| Kind | Engine |
|---|---|
| Audio & video | `ffmpeg`, using Apple's hardware encoders (VideoToolbox) for H.264, HEVC and ProRes |
| Images | Apple's ImageIO (plus `cwebp` for WebP) |
| Documents | macOS's built-in `textutil`, a small built-in Markdown renderer, and AppKit's text system for PDF output |
| PDF | Core Graphics and PDFKit |

The Finder menu item is a tiny Automator Quick Action (`~/Library/Services/Convert.workflow`) that runs `open -b local.convert.app <files>`.

| File | What's in it |
|---|---|
| `Sources/Convert/Formats.swift` | File type detection, output formats, per-format options |
| `Sources/Convert/FFmpeg.swift` | ffmpeg arguments for each audio/video format |
| `Sources/Convert/ImageConv.swift` | Image conversion |
| `Sources/Convert/DocConv.swift` | Document conversion, document → PDF, PDF → images/text |
| `Sources/Convert/Markdown.swift` | Markdown → HTML |
| `Sources/Convert/ConvertModel.swift`, `ContentView.swift` | The window and its state |
| `Sources/Convert/QuickAction.swift` | Installs/removes the Finder Quick Action |

### Testing from the command line

```bash
build/Convert.app/Contents/MacOS/Convert --cli mp3 song.opus
build/Convert.app/Contents/MacOS/Convert --cli        # lists all format IDs
```

### The icon

`tools/make_icon.swift` draws the icon with SwiftUI; `build.sh` turns it into `AppIcon.icns` when that file is missing.

### Making a release

```bash
./build.sh --zip     # creates build/Convert.zip (universal, ad-hoc signed)
```

## License

[MIT](LICENSE). Do whatever you like with it.

ffmpeg and cwebp are **not** bundled; they're installed separately by you and keep their own licenses.
