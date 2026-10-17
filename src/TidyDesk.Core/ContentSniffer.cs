using System.IO.Compression;
using System.Text;

namespace TidyDesk.Core;

/// <summary>
/// Detects the real type of files without a known extension from their first bytes (file signature).
/// The file is only read; no data ever leaves the computer.
/// </summary>
public static class ContentSniffer
{
    private const int HeaderSize = 16;

    /// <summary>An extension for the detected type (such as ".pdf"), or null if unknown.</summary>
    public static string? DetectExtension(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var header = new byte[HeaderSize];
            var read = stream.Read(header, 0, header.Length);
            if (read < 4)
                return null;

            if (StartsWith(header, "%PDF"u8)) return ".pdf";
            if (StartsWith(header, [0x89, 0x50, 0x4E, 0x47])) return ".png";
            if (StartsWith(header, [0xFF, 0xD8, 0xFF])) return ".jpg";
            if (StartsWith(header, "GIF8"u8)) return ".gif";
            if (StartsWith(header, "RIFF"u8) && read >= 12 && Encoding.ASCII.GetString(header, 8, 4) == "WEBP") return ".webp";
            if (StartsWith(header, "Rar!"u8)) return ".rar";
            if (StartsWith(header, [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C])) return ".7z";
            if (StartsWith(header, [0x1F, 0x8B])) return ".gz";
            if (read >= 8 && Encoding.ASCII.GetString(header, 4, 4) == "ftyp") return ".mp4";
            if (StartsWith(header, "ID3"u8)) return ".mp3";
            if (StartsWith(header, "MZ"u8)) return ".exe";
            if (StartsWith(header, [0x50, 0x4B, 0x03, 0x04]))
            {
                stream.Position = 0;
                return DetectZipFlavor(stream);
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>Word/Excel/PowerPoint files are ZIP archives; they're told apart by their inner folder names.</summary>
    private static string DetectZipFlavor(Stream stream)
    {
        try
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.StartsWith("word/", StringComparison.Ordinal)) return ".docx";
                if (entry.FullName.StartsWith("xl/", StringComparison.Ordinal)) return ".xlsx";
                if (entry.FullName.StartsWith("ppt/", StringComparison.Ordinal)) return ".pptx";
            }
        }
        catch (InvalidDataException)
        {
            // Corrupt ZIP; still treated as an archive.
        }
        return ".zip";
    }

    private static bool StartsWith(byte[] data, ReadOnlySpan<byte> prefix) => data.AsSpan().StartsWith(prefix);
}
