using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Xml;
using System.Xml.Linq;

namespace ClipShelf;

internal sealed class SafeOoxmlPackage : IDisposable
{
    internal const long TotalLimit = 256L * 1024 * 1024, EntryLimit = 32L * 1024 * 1024;
    private readonly FileStream file; private readonly ZipArchive zip; private readonly Dictionary<string, ZipArchiveEntry> entries;
    internal SafeOoxmlPackage(string path) {
        using var timing = PreviewMetrics.Measure("zip-directory");
        file = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try {
            Span<byte> header = stackalloc byte[8]; file.ReadExactly(header); file.Position = 0;
            if (header[0] == 0xD0 && header[1] == 0xCF) throw new PreviewException("Encrypted", "文档已加密或使用旧二进制格式，请使用默认应用打开。");
            zip = new ZipArchive(file, ZipArchiveMode.Read);
            if (zip.Entries.Count > 10000) throw Limit();
            long total = 0; entries = new(StringComparer.Ordinal);
            foreach (var entry in zip.Entries) {
                if (entry.Length > EntryLimit || (total += entry.Length) > TotalLimit || entry.FullName.Contains('\\') || entry.FullName.StartsWith('/') || entry.FullName.Split('/').Contains("..")) throw Limit();
                if (!entries.TryAdd(entry.FullName, entry)) throw new PreviewException("InvalidPackage", "文档包包含重复条目。");
            }
        } catch { file.Dispose(); throw; }
    }
    internal static PreviewException Limit() => new("SafetyLimit", "文档超出快速预览安全限制，请使用默认应用打开。");
    internal bool Has(string path) => entries.ContainsKey(path);
    internal long Length(string path) => entries.TryGetValue(path, out var e) ? e.Length : 0;
    internal byte[] Bytes(string path, CancellationToken token, int max = (int)EntryLimit) {
        token.ThrowIfCancellationRequested();
        if (!entries.TryGetValue(path, out var entry)) throw new PreviewException("MissingRelationship", "文档缺少所需内容或关联文件。");
        if (entry.Length > max) throw Limit();
        using var input = entry.Open(); using var output = new MemoryStream(); var buffer = new byte[16384];
        int read; while ((read = input.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); if (output.Length + read > max) throw Limit(); output.Write(buffer, 0, read); }
        return output.ToArray();
    }
    internal XDocument Xml(string path, CancellationToken token, bool optional = false) {
        if (optional && !Has(path)) return new XDocument(new XElement("empty"));
        using var timing = PreviewMetrics.Measure("xml-parse");
        var bytes = Bytes(path, token, 16 * 1024 * 1024);
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024, MaxCharactersFromEntities = 0 };
        // Validate limits before constructing the object graph. No external entities or URIs are followed.
        using (var input = new MemoryStream(bytes)) using (var reader = XmlReader.Create(input, settings)) {
            int nodes = 0; while (reader.Read()) { token.ThrowIfCancellationRequested(); if (reader.Depth > 64 || ++nodes > 300000 || reader.Value.Length > 200000) throw Limit(); }
        }
        using var stream = new MemoryStream(bytes); using var safe = XmlReader.Create(stream, settings); return XDocument.Load(safe);
    }
    internal Dictionary<string, (string Path, string Type)> Relations(string part, CancellationToken token) {
        int slash = part.LastIndexOf('/'); string rel = (slash < 0 ? "" : part[..(slash + 1)]) + "_rels/" + part[(slash + 1)..] + ".rels";
        var result = new Dictionary<string, (string, string)>();
        foreach (var e in Xml(rel, token, true).Descendants().Where(x => x.Name.LocalName == "Relationship")) {
            if ((string?)e.Attribute("TargetMode") == "External") continue;
            string target = (string?)e.Attribute("Target") ?? ""; string id = (string?)e.Attribute("Id") ?? "";
            if (target.Contains(':') || target.Contains('\\') || target.Contains('%') || target.Contains('?') || target.Contains('#')) continue;
            var parts = new List<string>(); string combined = target.StartsWith('/') ? target[1..] : (slash < 0 ? "" : part[..(slash + 1)]) + target;
            foreach (string segment in combined.Split('/')) { if (segment == "..") { if (parts.Count == 0) throw new PreviewException("InvalidPackage", "文档关系路径非法。"); parts.RemoveAt(parts.Count - 1); } else if (segment is not ("" or ".")) parts.Add(segment); }
            if (!result.TryAdd(id, (string.Join('/', parts), (string?)e.Attribute("Type") ?? ""))) throw new PreviewException("InvalidPackage", "文档关系重复。");
        }
        return result;
    }
    public void Dispose() { zip.Dispose(); file.Dispose(); }
}
