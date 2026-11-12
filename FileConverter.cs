using System.Collections.Generic;
using System.Formats.Tar;
using System.IO.Compression;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brushes = System.Windows.Media.Brushes;

namespace ToConvert;

internal static class FileConverter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static void Convert(string sourcePath, string destinationPath, string targetExtension)
    {
        var sourceExtension = Path.GetExtension(sourcePath).ToLowerInvariant();
        targetExtension = targetExtension.ToLowerInvariant();

        if (sourceExtension == ".docx" && targetExtension is (".txt" or ".rtf"))
        {
            var documentText = ExtractDocxText(sourcePath);
            var convertedText = targetExtension == ".rtf" ? PlainTextToRtf(documentText) : documentText;
            File.WriteAllText(destinationPath, convertedText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return;
        }

        if (CanConvertArchive(sourceExtension, targetExtension))
        {
            ConvertArchive(sourcePath, destinationPath, sourceExtension, targetExtension);
            return;
        }

        if (IsImage(sourceExtension) && IsImage(targetExtension))
        {
            ConvertImage(sourcePath, destinationPath, targetExtension);
            return;
        }

        var text = ReadUtf8Text(sourcePath);
        var converted = (sourceExtension, targetExtension) switch
        {
            (".json", ".json") => FormatJson(text),
            (".json", ".xml") => JsonToXml(text),
            (".json", ".csv") => JsonToCsv(text),
            (".xml", ".xml") => FormatXml(text),
            (".xml", ".json") => XmlToJson(text),
            (".csv", ".csv") => text,
            (".csv", ".json") => CsvToJson(text),
            (".txt", ".txt") => text,
            (".txt", ".md") => text,
            (".txt", ".html") => TextToHtml(text),
            (".md", ".md") => text,
            (".md", ".txt") => MarkdownToText(text),
            (".md", ".html") => MarkdownToHtml(text),
            (".html", ".html") => text,
            (".html", ".txt") => HtmlToText(text),
            _ => throw new NotSupportedException(
                $"Conversion from {sourceExtension} to {targetExtension} is not supported.")
        };

        File.WriteAllText(destinationPath, converted, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static bool IsImage(string extension) => extension is
        ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff";

    private static void ConvertImage(string sourcePath, string destinationPath, string targetExtension)
    {
        var decoder = BitmapDecoder.Create(
            new Uri(sourcePath),
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);

        if (decoder.Frames.Count == 0)
        {
            throw new InvalidDataException("No readable image was found in this file.");
        }

        BitmapEncoder encoder = targetExtension switch
        {
            ".png" => new PngBitmapEncoder(),
            ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = 95 },
            ".bmp" => new BmpBitmapEncoder(),
            ".gif" => new GifBitmapEncoder(),
            ".tif" or ".tiff" => new TiffBitmapEncoder(),
            _ => throw new NotSupportedException($"The image format {targetExtension} is not supported.")
        };

        var preserveAnimation = targetExtension is ".gif" or ".tif" or ".tiff";
        IEnumerable<BitmapFrame> frames = preserveAnimation
            ? decoder.Frames
            : new[] { decoder.Frames[0] };
        foreach (var frame in frames)
        {
            encoder.Frames.Add(
                targetExtension is ".jpg" or ".jpeg"
                    ? FlattenOnWhite(frame)
                    : BitmapFrame.Create(frame));
        }

        using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write);
        encoder.Save(output);
    }

    internal static void ConvertPngToIcon(string sourcePath, string destinationPath)
    {
        var decoder = BitmapDecoder.Create(
            new Uri(sourcePath),
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0)
        {
            throw new InvalidDataException("No readable image was found for the icon.");
        }

        const int iconSize = 256;
        var source = decoder.Frames[0];
        var scale = Math.Min((double)iconSize / source.PixelWidth, (double)iconSize / source.PixelHeight);
        var width = source.PixelWidth * scale;
        var height = source.PixelHeight * scale;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawImage(source,
                new System.Windows.Rect((iconSize - width) / 2, (iconSize - height) / 2, width, height));
        }

        var bitmap = new RenderTargetBitmap(iconSize, iconSize, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var png = new MemoryStream();
        encoder.Save(png);
        var imageData = png.ToArray();

        using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(output);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write((uint)imageData.Length);
        writer.Write((uint)22);
        writer.Write(imageData);
    }

    private static bool CanConvertArchive(string sourceExtension, string targetExtension) =>
        (sourceExtension, targetExtension) is
            (".zip", ".tar") or
            (".tar", ".zip") or
            (".tar", ".gz") or
            (".gz", ".tar");

    private static void ConvertArchive(
        string sourcePath,
        string destinationPath,
        string sourceExtension,
        string targetExtension)
    {
        if (sourceExtension == ".zip")
        {
            ZipToTar(sourcePath, destinationPath);
        }
        else if (sourceExtension == ".tar" && targetExtension == ".zip")
        {
            TarToZip(sourcePath, destinationPath);
        }
        else if (sourceExtension == ".tar")
        {
            using var input = File.OpenRead(sourcePath);
            using var output = File.Create(destinationPath);
            using var gzip = new GZipStream(output, CompressionLevel.Optimal);
            input.CopyTo(gzip);
        }
        else
        {
            using var input = File.OpenRead(sourcePath);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = File.Create(destinationPath);
            gzip.CopyTo(output);
        }
    }

    private static void ZipToTar(string sourcePath, string destinationPath)
    {
        using var archive = ZipFile.OpenRead(sourcePath);
        using var output = File.Create(destinationPath);
        using var writer = new TarWriter(output, TarEntryFormat.Pax);
        foreach (var item in archive.Entries)
        {
            var isDirectory = item.FullName.EndsWith('/');
            var entry = new PaxTarEntry(
                isDirectory ? TarEntryType.Directory : TarEntryType.RegularFile,
                item.FullName);
            if (!isDirectory)
            {
                entry.DataStream = item.Open();
            }

            writer.WriteEntry(entry);
            entry.DataStream?.Dispose();
        }
    }

    private static void TarToZip(string sourcePath, string destinationPath)
    {
        using var input = File.OpenRead(sourcePath);
        using var reader = new TarReader(input);
        using var output = File.Create(destinationPath);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create);
        TarEntry? entry;
        while ((entry = reader.GetNextEntry(copyData: false)) is not null)
        {
            if (entry.EntryType == TarEntryType.Directory)
            {
                archive.CreateEntry(entry.Name.TrimEnd('/') + "/");
                continue;
            }

            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile))
            {
                throw new NotSupportedException(
                    $"The TAR entry type {entry.EntryType} cannot be represented in a ZIP file.");
            }

            var item = archive.CreateEntry(entry.Name, CompressionLevel.Optimal);
            using var destination = item.Open();
            entry.DataStream?.CopyTo(destination);
        }
    }

    private static string ExtractDocxText(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var documentEntry = archive.GetEntry("word/document.xml")
            ?? throw new InvalidDataException("The DOCX file does not contain its main document.");
        using var stream = documentEntry.Open();
        var document = XDocument.Load(stream);
        XNamespace word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var paragraphs = document.Descendants(word + "p").Select(paragraph =>
            string.Concat(paragraph.Descendants().Select(element =>
                element.Name == word + "t" ? element.Value :
                element.Name == word + "tab" ? "\t" :
                element.Name == word + "br" ? "\n" :
                string.Empty)));
        return string.Join(Environment.NewLine, paragraphs);
    }

    private static string PlainTextToRtf(string text)
    {
        var result = new StringBuilder(@"{\rtf1\ansi\deff0\uc1 ");
        foreach (var character in text)
        {
            switch (character)
            {
                case '\\':
                case '{':
                case '}':
                    result.Append('\\').Append(character);
                    break;
                case '\r':
                    break;
                case '\n':
                    result.Append(@"\par ");
                    break;
                case '\t':
                    result.Append(@"\tab ");
                    break;
                default:
                    if (character < 128)
                    {
                        result.Append(character);
                    }
                    else
                    {
                        result.Append(@"\u").Append(unchecked((short)character)).Append('?');
                    }
                    break;
            }
        }

        return result.Append('}').ToString();
    }

    private static BitmapFrame FlattenOnWhite(BitmapSource source)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null,
                new System.Windows.Rect(0, 0, source.PixelWidth, source.PixelHeight));
            context.DrawImage(source,
                new System.Windows.Rect(0, 0, source.PixelWidth, source.PixelHeight));
        }

        var flattened = new RenderTargetBitmap(
            source.PixelWidth, source.PixelHeight, source.DpiX, source.DpiY, PixelFormats.Pbgra32);
        flattened.Render(visual);
        return BitmapFrame.Create(flattened);
    }

    private static string ReadUtf8Text(string path)
    {
        using var reader = new StreamReader(path, new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string FormatJson(string text)
    {
        using var document = JsonDocument.Parse(text);
        return JsonSerializer.Serialize(document.RootElement, JsonOptions);
    }

    private static string FormatXml(string text)
    {
        var document = XDocument.Parse(text);
        return document.ToString(SaveOptions.None);
    }

    private static string JsonToXml(string text)
    {
        using var document = JsonDocument.Parse(text);
        var root = new XElement("root");
        AppendJsonValue(root, "item", document.RootElement);
        return new XDocument(root).ToString(SaveOptions.None);
    }

    private static void AppendJsonValue(XElement parent, string name, JsonElement value)
    {
        var element = new XElement(XmlConvert.EncodeLocalName(name));
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    AppendJsonValue(element, property.Name, property.Value);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    AppendJsonValue(element, "item", item);
                }
                break;
            case JsonValueKind.String:
                element.Value = value.GetString() ?? string.Empty;
                break;
            case JsonValueKind.Number:
                element.Value = value.GetRawText();
                break;
            case JsonValueKind.True:
                element.Value = "true";
                break;
            case JsonValueKind.False:
                element.Value = "false";
                break;
            case JsonValueKind.Null:
                element.SetAttributeValue("null", "true");
                break;
        }

        parent.Add(element);
    }

    private static string XmlToJson(string text)
    {
        var root = XElement.Parse(text);
        var result = new JsonObject
        {
            [root.Name.LocalName] = ConvertXmlElement(root)
        };
        return result.ToJsonString(JsonOptions);
    }

    private static JsonNode? ConvertXmlElement(XElement element)
    {
        var children = element.Elements().ToArray();
        var attributes = element.Attributes().ToArray();
        var value = string.Concat(element.Nodes().OfType<XText>().Select(node => node.Value)).Trim();
        if (children.Length == 0 && attributes.Length == 0)
        {
            return JsonValue.Create(value);
        }

        var result = new JsonObject();
        foreach (var attribute in attributes)
        {
            result[$"@{attribute.Name.LocalName}"] = attribute.Value;
        }

        foreach (var group in children.GroupBy(child => child.Name.LocalName))
        {
            var items = group.Select(ConvertXmlElement).ToArray();
            result[group.Key] = items.Length == 1
                ? items[0]
                : new JsonArray(items);
        }

        if (value.Length > 0)
        {
            result["#text"] = value;
        }

        return result;
    }

    private static string CsvToJson(string text)
    {
        var rows = ParseCsv(text);
        if (rows.Count == 0)
        {
            return "[]";
        }

        var headers = rows[0].Select(header => header.Trim()).ToArray();
        if (headers.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidDataException("The first row of the CSV file must contain column names.");
        }

        if (headers.Distinct(StringComparer.Ordinal).Count() != headers.Length)
        {
            throw new InvalidDataException("CSV column names must be unique.");
        }

        var result = new JsonArray();
        foreach (var row in rows.Skip(1).Where(row => row.Any(cell => cell.Length > 0)))
        {
            var item = new JsonObject();
            for (var index = 0; index < headers.Length; index++)
            {
                item[headers[index]] = index < row.Count ? row[index] : string.Empty;
            }
            result.Add(item);
        }

        return result.ToJsonString(JsonOptions);
    }

    private static string JsonToCsv(string text)
    {
        using var document = JsonDocument.Parse(text);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("To convert JSON to CSV, the file must contain an array of records.");
        }

        var objects = document.RootElement.EnumerateArray().ToArray();
        if (objects.Length == 0)
        {
            return string.Empty;
        }

        if (objects.Any(item => item.ValueKind != JsonValueKind.Object))
        {
            throw new InvalidDataException("Each JSON array item must be an object with columns.");
        }

        var headers = objects.SelectMany(item => item.EnumerateObject().Select(property => property.Name))
            .Distinct(StringComparer.Ordinal).ToArray();
        var lines = new List<string> { string.Join(",", headers.Select(EscapeCsv)) };
        foreach (var item in objects)
        {
            var values = headers.Select(header =>
            {
                if (!item.TryGetProperty(header, out var value) ||
                    value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    return string.Empty;
                }

                if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    throw new InvalidDataException("CSV does not support nested JSON values.");
                }

                return EscapeCsv(value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? string.Empty
                    : value.GetRawText());
            });
            lines.Add(string.Join(",", values));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (quoted)
            {
                if (character == '"' && index + 1 < text.Length && text[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else if (character == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(character);
                }
            }
            else if (character == '"' && field.Length == 0)
            {
                quoted = true;
            }
            else if (character == ',')
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (character is '\r' or '\n')
            {
                row.Add(field.ToString());
                field.Clear();
                if (row.Any(cell => cell.Length > 0) || index < text.Length - 1)
                {
                    rows.Add(row);
                }
                row = new List<string>();
                if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
                {
                    index++;
                }
            }
            else
            {
                field.Append(character);
            }
        }

        if (quoted)
        {
            throw new InvalidDataException("Invalid CSV: a quoted field was not closed.");
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }

    private static string EscapeCsv(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;

    private static string TextToHtml(string text) =>
        WrapHtml($"<pre>{System.Net.WebUtility.HtmlEncode(text)}</pre>");

    private static string MarkdownToHtml(string markdown)
    {
        var output = new StringBuilder();
        var inList = false;

        foreach (var rawLine in markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = rawLine.Trim();
            var bullet = Regex.Match(line, @"^[-*+]\s+(.*)$");
            if (bullet.Success)
            {
                if (!inList)
                {
                    output.AppendLine("<ul>");
                    inList = true;
                }
                output.Append("<li>").Append(FormatMarkdownInline(bullet.Groups[1].Value)).AppendLine("</li>");
                continue;
            }

            if (inList)
            {
                output.AppendLine("</ul>");
                inList = false;
            }

            if (line.Length == 0)
            {
                continue;
            }

            var heading = Regex.Match(line, @"^(#{1,6})\s+(.+)$");
            if (heading.Success)
            {
                var level = heading.Groups[1].Length;
                output.Append("<h").Append(level).Append('>')
                    .Append(FormatMarkdownInline(heading.Groups[2].Value))
                    .Append("</h").Append(level).AppendLine(">");
            }
            else
            {
                output.Append("<p>").Append(FormatMarkdownInline(line)).AppendLine("</p>");
            }
        }

        if (inList)
        {
            output.AppendLine("</ul>");
        }

        return WrapHtml(output.ToString());
    }

    private static string FormatMarkdownInline(string value)
    {
        var safe = System.Net.WebUtility.HtmlEncode(value);
        safe = Regex.Replace(safe, @"`([^`]+)`", "<code>$1</code>");
        safe = Regex.Replace(safe, @"\*\*(.+?)\*\*", "<strong>$1</strong>");
        safe = Regex.Replace(safe, @"(?<!\*)\*([^*]+)\*(?!\*)", "<em>$1</em>");
        return safe;
    }

    private static string MarkdownToText(string markdown)
    {
        var text = Regex.Replace(markdown, @"(?m)^\s{0,3}#{1,6}\s*", string.Empty);
        text = Regex.Replace(text, @"(\*\*|__)(.+?)\1", "$2");
        text = Regex.Replace(text, @"(?<!\*)\*([^*]+)\*(?!\*)|(?<!_)_([^_]+)_(?!_)", "$1$2");
        text = Regex.Replace(text, @"`([^`]+)`", "$1");
        return Regex.Replace(text, @"(?m)^\s*[-*+]\s+", "• ");
    }

    private static string HtmlToText(string html)
    {
        var withoutCode = Regex.Replace(html, @"(?is)<(script|style)\b[^>]*>.*?</\1>", string.Empty);
        var withBreaks = Regex.Replace(withoutCode, @"(?i)<\s*(br|/p|/div|/li|/h[1-6]|/tr)\b[^>]*>", "\n");
        var plain = Regex.Replace(withBreaks, @"(?s)<[^>]*>", string.Empty);
        plain = System.Net.WebUtility.HtmlDecode(plain);
        return Regex.Replace(plain, @"[ \t]+\r?\n", Environment.NewLine).Trim();
    }

    private static string WrapHtml(string body) =>
        "<!doctype html>\n<html lang=\"en\">\n<head>\n" +
        "<meta charset=\"utf-8\">\n<title>Converted file</title>\n</head>\n<body>\n" +
        body + "\n</body>\n</html>";
}
