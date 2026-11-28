using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace SLSKDONET.Services.Rekordbox;

/// <summary>Result of syncing a playlist entry in Rekordbox's <c>masterPlaylists6.xml</c>.</summary>
public sealed record MasterPlaylistsXmlResult(
    bool FileExists,
    bool Changed,
    bool CreatedNode,
    string? HexId,
    string? HexParentId);

/// <summary>
/// Reads and atomically updates Rekordbox's <c>masterPlaylists6.xml</c> file alongside <c>master.db</c>.
/// Verified against a real Rekordbox 6.7.4 library:
/// <code>
/// &lt;NODE Id="92B9D89F" ParentId="0" Attribute="0" Timestamp="1794610828144" Lib_Type="0" CheckType="0"/&gt;
/// </code>
/// where:
/// <list type="bullet">
///   <item><c>Id</c> is the uppercase hexadecimal representation of <c>djmdPlaylist.ID</c> (decimal);</item>
///   <item><c>ParentId</c> is <c>"0"</c> for root-level playlists (<c>djmdPlaylist.ParentID = 'root'</c>), or the uppercase hex of the parent folder's decimal ID;</item>
///   <item><c>Attribute</c> is <c>"0"</c> for a normal playlist (<c>"1"</c> for a folder);</item>
///   <item><c>Timestamp</c> is the Unix update time in milliseconds;</item>
///   <item><c>Lib_Type</c> and <c>CheckType</c> are <c>"0"</c>.</item>
/// </list>
/// </summary>
public static class RekordboxMasterPlaylistsXml
{
    public const string FileName = "masterPlaylists6.xml";

    public static string GetXmlPathForDatabase(string databasePath)
    {
        string dir = Path.GetDirectoryName(databasePath) ?? "";
        return Path.Combine(dir, FileName);
    }

    /// <summary>Converts a decimal <c>djmdPlaylist.ID</c> or <c>ParentID</c> to Rekordbox's uppercase hex format
    /// (<c>"root"</c>, <c>"0"</c>, or empty becomes <c>"0"</c>).</summary>
    public static string DecimalIdToHex(string? decimalId)
    {
        if (string.IsNullOrWhiteSpace(decimalId)
            || string.Equals(decimalId, "root", StringComparison.OrdinalIgnoreCase)
            || decimalId == "0")
            return "0";

        if (!ulong.TryParse(decimalId.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong value))
            throw new FormatException($"Expected numeric Rekordbox playlist ID, got '{decimalId}'.");

        return value == 0 ? "0" : value.ToString("X", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Adds or updates a playlist <c>&lt;NODE&gt;</c> in <c>masterPlaylists6.xml</c>.
    /// Preserves the XML declaration, UTF-8 encoding (without BOM), whitespace, and all other nodes.
    /// Writes atomically via a temporary file in the same directory followed by <c>File.Move(..., overwrite: true)</c>.
    /// </summary>
    public static MasterPlaylistsXmlResult UpsertPlaylistNode(
        string xmlPath,
        string? playlistDecimalId,
        string? parentDecimalId,
        long timestampMs,
        bool dryRun)
    {
        if (!File.Exists(xmlPath))
            return new MasterPlaylistsXmlResult(FileExists: false, Changed: false, CreatedNode: false, null, null);

        string? hexId = string.IsNullOrWhiteSpace(playlistDecimalId) ? null : DecimalIdToHex(playlistDecimalId);
        string hexParentId = DecimalIdToHex(parentDecimalId);

        if (dryRun)
        {
            if (hexId == null)
                return new MasterPlaylistsXmlResult(FileExists: true, Changed: true, CreatedNode: true, null, hexParentId);

            var previewDoc = XDocument.Load(xmlPath, LoadOptions.PreserveWhitespace);
            var previewPlaylists = previewDoc.Root?.Element("PLAYLISTS")
                ?? throw new InvalidOperationException($"{FileName} is missing a <PLAYLISTS> element.");
            bool exists = previewPlaylists.Elements("NODE")
                .Any(n => string.Equals((string?)n.Attribute("Id"), hexId, StringComparison.OrdinalIgnoreCase));
            return new MasterPlaylistsXmlResult(FileExists: true, Changed: true, CreatedNode: !exists, hexId, hexParentId);
        }

        if (hexId == null)
            throw new ArgumentException("A playlist ID is required to update masterPlaylists6.xml.", nameof(playlistDecimalId));

        var doc = XDocument.Load(xmlPath, LoadOptions.PreserveWhitespace);
        var playlists = doc.Root?.Element("PLAYLISTS")
            ?? throw new InvalidOperationException($"{FileName} is missing a <PLAYLISTS> element.");

        var existing = playlists.Elements("NODE")
            .FirstOrDefault(n => string.Equals((string?)n.Attribute("Id"), hexId, StringComparison.OrdinalIgnoreCase));

        bool createdNode;
        string ts = timestampMs.ToString(CultureInfo.InvariantCulture);

        if (existing != null)
        {
            createdNode = false;
            existing.SetAttributeValue("Timestamp", ts);
        }
        else
        {
            createdNode = true;
            var node = new XElement("NODE",
                new XAttribute("Id", hexId),
                new XAttribute("ParentId", hexParentId),
                new XAttribute("Attribute", "0"),
                new XAttribute("Timestamp", ts),
                new XAttribute("Lib_Type", "0"),
                new XAttribute("CheckType", "0"));

            // Match the file's existing indentation & line ending before </PLAYLISTS>.
            string newline = DetectNewLine(xmlPath);
            string indent = DetectNodeIndent(playlists);
            if (playlists.LastNode is XText trailingWs && string.IsNullOrWhiteSpace(trailingWs.Value))
            {
                string closingIndent = trailingWs.Value;
                int lastNl = closingIndent.LastIndexOf('\n');
                string closingPrefix = lastNl >= 0 ? closingIndent[(lastNl + 1)..] : "";
                trailingWs.Value = newline + indent;
                playlists.Add(node);
                playlists.Add(new XText(newline + closingPrefix));
            }
            else
            {
                playlists.Add(new XText(newline + indent));
                playlists.Add(node);
                playlists.Add(new XText(newline + "  "));
            }
        }

        string dir = Path.GetDirectoryName(xmlPath) ?? ".";
        string tempPath = Path.Combine(dir, $"{FileName}.orbit-tmp-{Guid.NewGuid():N}");
        try
        {
            var settings = new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                OmitXmlDeclaration = false,
                Indent = false,
            };
            using (var writer = XmlWriter.Create(tempPath, settings))
                doc.Save(writer);

            File.Move(tempPath, xmlPath, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch (IOException cleanupEx)
            {
                Serilog.Log.Debug(cleanupEx, "Couldn't delete temporary Rekordbox playlist file {File}", tempPath);
            }
            throw;
        }

        return new MasterPlaylistsXmlResult(FileExists: true, Changed: true, CreatedNode: createdNode, hexId, hexParentId);
    }

    private static string DetectNewLine(string xmlPath)
    {
        string raw = File.ReadAllText(xmlPath);
        return raw.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
    }

    private static string DetectNodeIndent(XElement playlists)
    {
        var firstNode = playlists.Elements("NODE").FirstOrDefault();
        if (firstNode?.PreviousNode is XText prev)
        {
            int nl = prev.Value.LastIndexOf('\n');
            if (nl >= 0) return prev.Value[(nl + 1)..];
        }
        return "    ";
    }
}
