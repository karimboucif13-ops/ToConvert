# FreeGameX Converter

A Windows desktop app for converting images, audio, video, data, text, and selected document/archive formats. Conversion happens locally; your files are never uploaded.

## Run and publish

The project targets Windows x64 and .NET 8. To run it from the project folder:

```powershell
dotnet run
```

Create a self-contained, single-file executable with:

```powershell
dotnet publish -c Release
```

The standalone executable is written to `bin\Release\net8.0-windows\win-x64\publish\ToConvert.exe`. The .NET runtime, app libraries, FFmpeg tools and native libraries, soundtrack, and logo are bundled into the executable. At runtime, FFmpeg and the soundtrack are automatically extracted under the current user's local application data folder; users do not need to download or manage any additional files.

## Automatic updates

The app checks the latest release in [karimboucif13-ops/ToConvert](https://github.com/karimboucif13-ops/ToConvert) at startup and provides a dedicated update card with a manual **Check for updates** button. After confirmation, it downloads `ToConvert.exe`, verifies GitHub's SHA-256 asset digest, then replaces the running executable and restarts.

To publish an update, push the complete project to that repository and push a version tag such as `v1.3.0`. The release workflow builds the single-file app and attaches `ToConvert.exe` to a GitHub Release. Updates are not available until the repository has a release with that asset.

## Features and formats

- **Audio:** MP3, WAV, AAC, M4A, FLAC, OGG, Opus, WMA, AIFF, AC-3, MP2, CAF, AU, and MKA.
- **Video:** MP4, MKV, MOV, WebM, AVI, WMV, FLV, MPEG, M4V, MPEG-TS, OGV, 3GP, VOB, ASF, and MXF. Audio can also be extracted from video files.
- **Images:** PNG, JPEG, BMP, GIF, TIFF, ICO, WebP, AVIF, JPEG 2000, TGA, PCX, DDS, PPM, PGM, PBM, and PAM.
- **Data:** JSON to XML or CSV, XML to JSON, and CSV to JSON. JSON is validated and formatted; CSV supports quoted fields.
- **Text:** TXT to Markdown or HTML, Markdown to TXT or HTML, and HTML to TXT.
- **Documents:** DOCX text extraction to TXT or RTF. Images and page layout are not retained.
- **Archives:** ZIP to TAR, TAR to ZIP or GZIP, and GZIP to TAR.
- **Batch processing:** Convert multiple files of the same format in one run, or package mixed files into a ZIP archive. Each converted file is saved separately.
- **ZIP tools:** Create a ZIP archive from selected files or extract a ZIP archive into a new folder.
- **Visual feedback:** Drag-and-drop highlighting, elevated cards, responsive button states, and progress indicators for conversions and archive operations.
- **Startup:** A short loading screen appears before the main window opens.
- **Music player:** The bundled soundtrack starts automatically. Choose another audio file, pause/stop playback, or adjust volume.
- **Appearance:** Choose a custom background color; the choice is saved for the next launch.
- **Drag and drop:** Drop one or more files into the main window to select them.

## Limitations

This is not a universal converter. HEIC, SVG, PDF, most Office formats, RAR, 7Z, database files, and source-code compilation (including Java and Python) are not supported. PDF export, layout-preserving DOCX conversion, and the other formats listed above that require Word, Acrobat, Access, LibreOffice, or additional codecs need separate conversion engines. CSS has no direct equivalent format. ZIP/TAR/GZIP conversions preserve regular files and directories; unsupported TAR entry types are reported rather than silently discarded. JSON-to-CSV requires an array of objects, and CSV-to-JSON requires unique column names.

## FFmpeg and licensing

The app bundles FFmpeg 9.0.2 for Windows x64 from `TqkLibrary.FFmpeg.Lgpl2.Tools.Win.x64`, licensed under LGPL-2.1-or-later. The FFmpeg license text is embedded in the executable along with its tools and libraries. Package source and license information are available from the [FFmpeg build project](https://github.com/tqk2811/FFmpegBuild) and the [NuGet package](https://www.nuget.org/packages/TqkLibrary.FFmpeg.Lgpl2.Tools.Win.x64/9.0.2).
