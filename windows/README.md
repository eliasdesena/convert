# Convert for Windows

The Windows version of Convert. Right-click files in Explorer → **Convert…** → pick a format → the new
file lands next to the original. Everything runs locally.

It sits directly in the Windows 11 context menu (not under "Show more options"), and the window follows
your light/dark theme and accent colour.

> [!NOTE]
> Early port. It does what the Mac app does (see the [main README](../README.md)), with the differences
> listed under *Known limitations*.

## Requirements

- Windows 11 (x64)
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build (`winget install Microsoft.DotNet.SDK.10`)
- **ffmpeg** for audio and video: `winget install Gyan.FFmpeg`
  (or put `ffmpeg.exe` + `ffprobe.exe` in `windows\tools\ffmpeg\`; `build.ps1` then bundles them)
- Optional: **LibreOffice** for Word, ODT and RTF documents: `winget install TheDocumentFoundation.LibreOffice`

Images, PDFs, and Markdown / plain text / HTML → PDF work without anything extra.

## Install

```powershell
cd windows
.\build.ps1 -Install
```

This builds Convert, copies it to `%LOCALAPPDATA%\Programs\Convert` and registers the context-menu
entry. Then right-click a supported file in Explorer → **Convert…**. Convert also appears in the Start
menu, where you can open it and drag files onto the window.

The first time, Windows asks for permission (UAC) once. Windows only adds context-menu entries from
signed packages, so `build.ps1` creates a certificate just for your PC (`CN=Convert Dev`) and adds it to
the trusted certificates. Nothing is sent anywhere.

## Uninstall

```powershell
.\uninstall.ps1        # removes the context menu and the program
.\uninstall.ps1 -All   # also removes settings and the signing certificate
```

## Development

```powershell
dotnet build            # everything
dotnet test             # converts generated test media for real (needs ffmpeg)
src\Convert\bin\Debug\net10.0-windows10.0.19041.0\win-x64\Convert.exe --cli mp3 song.opus
src\Convert\bin\Debug\net10.0-windows10.0.19041.0\win-x64\Convert.exe --cli          # lists format IDs
```

| Project | What's in it |
|---|---|
| `src/Convert.Core` | Formats, ffmpeg arguments, images (Magick.NET), documents (Markdig, WebView2, LibreOffice), PDF (Windows.Data.Pdf, PdfPig) |
| `src/Convert` | The window (WPF, Fluent theme) and the `--cli` test mode |
| `src/Convert.Shell` | The context-menu entry (`IExplorerCommand`, loaded by Explorer through the package) |
| `packaging/` | Package manifest template and logos |
| `tests/Convert.Core.Tests` | xUnit tests that run real conversions |

## Known limitations

- **HEIC output** uses Windows' own encoder and needs the free *HEIF Image Extensions* and the
  *HEVC Video Extensions* from the Microsoft Store; without them HEIC isn't offered. **HE-AAC** only
  shows up with an ffmpeg build that includes `libfdk_aac`.
- **Paper size** (A4 / Letter) applies to Markdown, text and HTML. Word/ODT/RTF keep their own page setup.
- The context-menu part needs the .NET 10 runtime installed. A self-contained release would need it
  rewritten as a native DLL.
