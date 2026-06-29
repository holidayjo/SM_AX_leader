using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace GovAssistant.Services;

/// <summary>
/// HWPX 양식 로더/에디터.
/// 단락(paragraph) 단위 + 표(table) 단위로 AI가 직접 치환/확장/축소.
/// </summary>
public class HwpxTemplate
{
    public record ParagraphRef(int Id, string Text);
    public record TableRef(int Id, int RowCount, int ColCount, List<List<string>> Rows);

    public string SourcePath { get; }
    public Dictionary<string, string> Entries { get; } = new();
    public List<string> SectionEntryNames { get; } = new();
    public List<ParagraphRef> Paragraphs { get; } = new();
    public List<TableRef> Tables { get; } = new();
    public string PreviewText { get; private set; } = "";

    private readonly Dictionary<string, XDocument> _sectionDocs = new();
    private readonly Dictionary<int, XElement> _tableElements = new();

    private HwpxTemplate(string path) { SourcePath = path; }

    public static HwpxTemplate Load(string hwpxPath)
    {
        var t = new HwpxTemplate(hwpxPath);

        byte[] all;
        using (var fs = new FileStream(hwpxPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var ms = new MemoryStream())
        {
            fs.CopyTo(ms);
            all = ms.ToArray();
        }

        using var mem = new MemoryStream(all, writable: false);
        using var zip = new ZipArchive(mem, ZipArchiveMode.Read);

        foreach (var e in zip.Entries)
        {
            using var s = e.Open();
            using var ems = new MemoryStream();
            s.CopyTo(ems);
            var bytes = ems.ToArray();
            var name = e.FullName;
            if (LooksTextual(name))
                t.Entries[name] = Encoding.UTF8.GetString(bytes);
            else
                t.Entries[name] = "BIN:" + Convert.ToBase64String(bytes);
            if (name.StartsWith("Contents/section", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                t.SectionEntryNames.Add(name);
        }
        t.SectionEntryNames.Sort();
        t.ParseSections();
        return t;
    }

    private static bool LooksTextual(string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext is ".xml" or ".rels" or ".hpf" or ".opf" or ".txt" or ""
            || name.Equals("mimetype", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLocal(XElement el, string name)
        => el.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase);

    private void ParseSections()
    {
        Paragraphs.Clear();
        Tables.Clear();
        _sectionDocs.Clear();
        _tableElements.Clear();
        var sb = new StringBuilder();
        int pid = 0, tid = 0;

        foreach (var sec in SectionEntryNames)
        {
            XDocument doc;
            try { doc = XDocument.Parse(Entries[sec], LoadOptions.PreserveWhitespace); }
            catch { continue; }
            _sectionDocs[sec] = doc;

            // 모든 단락 — 표 안에 있는 단락은 제외 (표는 별도 처리)
            foreach (var p in doc.Descendants().Where(x => IsLocal(x, "p")))
            {
                if (p.Ancestors().Any(a => IsLocal(a, "tbl"))) continue;
                var texts = p.Descendants().Where(x => IsLocal(x, "t")).Select(x => x.Value);
                var text = string.Concat(texts);
                Paragraphs.Add(new ParagraphRef(pid, text));
                if (!string.IsNullOrWhiteSpace(text)) sb.AppendLine($"P{pid}: {text}");
                pid++;
            }

            // 표 (중첩 포함, 단순 평탄 처리)
            foreach (var tbl in doc.Descendants().Where(x => IsLocal(x, "tbl")))
            {
                var rows = new List<List<string>>();
                foreach (var tr in tbl.Elements().Where(x => IsLocal(x, "tr")))
                {
                    var cells = new List<string>();
                    foreach (var tc in tr.Elements().Where(x => IsLocal(x, "tc")))
                    {
                        var ct = string.Concat(tc.Descendants().Where(x => IsLocal(x, "t")).Select(x => x.Value));
                        cells.Add(ct);
                    }
                    rows.Add(cells);
                }
                var colCnt = rows.FirstOrDefault()?.Count ?? 0;
                Tables.Add(new TableRef(tid, rows.Count, colCnt, rows));
                _tableElements[tid] = tbl;
                sb.AppendLine($"T{tid} ({rows.Count}행 {colCnt}열):");
                for (int r = 0; r < rows.Count; r++)
                    sb.AppendLine($"  행{r}: {string.Join(" | ", rows[r].Select(c => string.IsNullOrWhiteSpace(c) ? "[empty]" : c))}");
                tid++;
            }
        }
        PreviewText = sb.ToString();
    }

    /// <summary>단락 ID → 새 텍스트.</summary>
    public void ApplyRewrites(IDictionary<int, string> rewrites)
    {
        if (rewrites.Count == 0) return;

        int id = 0;
        foreach (var sec in SectionEntryNames)
        {
            if (!_sectionDocs.TryGetValue(sec, out var doc)) continue;
            foreach (var p in doc.Descendants().Where(x => IsLocal(x, "p")).ToList())
            {
                if (p.Ancestors().Any(a => IsLocal(a, "tbl"))) continue;
                if (rewrites.TryGetValue(id, out var newText))
                {
                    SetTextInElement(p, newText);
                    ResetLineSegInElement(p);
                }
                id++;
            }
        }
        SerializeSections();
    }

    /// <summary>표 ID → 새 행 데이터(2차원). 행 수가 다르면 자동으로 추가/삭제.</summary>
    public void ApplyTableUpdates(IDictionary<int, List<List<string>>> updates)
    {
        if (updates.Count == 0) return;

        foreach (var (tid, newRows) in updates)
        {
            if (!_tableElements.TryGetValue(tid, out var tbl)) continue;
            var trs = tbl.Elements().Where(x => IsLocal(x, "tr")).ToList();
            int oldCount = trs.Count;
            int newCount = newRows.Count;
            if (oldCount == 0 || newCount == 0) continue;

            // 1. 행 수 조절
            if (newCount > oldCount)
            {
                // 마지막 행 복제해서 부족분 추가 (서식/속성 유지)
                var template = trs[^1];
                for (int i = oldCount; i < newCount; i++)
                {
                    var clone = new XElement(template);
                    ResetLineSegInElement(clone);
                    // 새 행은 셀 텍스트 비워 두기 (다음 단계에서 채움)
                    foreach (var t in clone.Descendants().Where(x => IsLocal(x, "t")).ToList())
                        t.Value = "";
                    template.AddAfterSelf(clone);
                    trs.Add(clone);
                    template = clone;
                }
            }
            else if (newCount < oldCount)
            {
                for (int i = oldCount - 1; i >= newCount; i--)
                    trs[i].Remove();
                trs = trs.Take(newCount).ToList();
            }

            // 2. 각 셀 텍스트 갱신 + 셀 주소(rowAddr) 재정렬
            //    복제 행은 cellAddr rowAddr이 원본(마지막 행) 값 그대로라
            //    행마다 실제 인덱스로 다시 박지 않으면 한글이 주소 중복으로 행을 버린다.
            for (int r = 0; r < newCount; r++)
            {
                var tcs = trs[r].Elements().Where(x => IsLocal(x, "tc")).ToList();
                var rowData = newRows[r];
                for (int c = 0; c < tcs.Count; c++)
                {
                    SetCellAddr(tcs[c], r, c);
                    var val = c < rowData.Count ? rowData[c] : "";
                    SetCellText(tcs[c], val);
                }
                ResetLineSegInElement(trs[r]);
            }

            // 3. tbl 속성의 rowCnt 갱신
            var rowCntAttr = tbl.Attribute("rowCnt");
            if (rowCntAttr != null) rowCntAttr.Value = newCount.ToString();
        }
        SerializeSections();
    }

    private static void SetTextInElement(XElement p, string newText)
    {
        var ts = p.Descendants().Where(x => IsLocal(x, "t")).ToList();
        if (ts.Count > 0)
        {
            ts[0].Value = newText;
            for (int i = 1; i < ts.Count; i++) ts[i].Value = "";
        }
        else
        {
            var hpNs = p.GetNamespaceOfPrefix("hp") ?? p.Name.Namespace;
            var run = new XElement(hpNs + "run", new XElement(hpNs + "t", newText));
            p.Add(run);
        }
    }

    private static void SetCellText(XElement tc, string newText)
    {
        // 셀 안에 단락이 여러 개면 첫 번째에 텍스트, 나머지는 비움
        var ps = tc.Descendants().Where(x => IsLocal(x, "p")).ToList();
        if (ps.Count == 0) return;
        SetTextInElement(ps[0], newText);
        for (int i = 1; i < ps.Count; i++)
        {
            foreach (var t in ps[i].Descendants().Where(x => IsLocal(x, "t")).ToList())
                t.Value = "";
        }
    }

    /// <summary>셀의 cellAddr(rowAddr/colAddr)을 실제 위치로 보정. 속성이 없으면 무시.</summary>
    private static void SetCellAddr(XElement tc, int rowIndex, int colIndex)
    {
        var ca = tc.Elements().FirstOrDefault(x => IsLocal(x, "cellAddr"));
        if (ca == null) return;
        var rowAttr = ca.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals("rowAddr", StringComparison.OrdinalIgnoreCase));
        var colAttr = ca.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals("colAddr", StringComparison.OrdinalIgnoreCase));
        rowAttr?.SetValue(rowIndex);
        colAttr?.SetValue(colIndex);
    }

    private static void ResetLineSegInElement(XElement el)
    {
        var stale = el.Descendants()
            .Where(x => IsLocal(x, "lineSegArray") || IsLocal(x, "linesegarray"))
            .ToList();
        foreach (var n in stale) n.Remove();
    }

    private void SerializeSections()
    {
        foreach (var kv in _sectionDocs)
        {
            var doc = kv.Value;
            var decl = doc.Declaration?.ToString()
                ?? """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""";
            Entries[kv.Key] = decl + "\n" + doc.ToString(SaveOptions.DisableFormatting);
        }
    }

    public void SaveAs(string outPath)
    {
        if (File.Exists(outPath)) File.Delete(outPath);
        using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8);

        foreach (var kv in Entries)
        {
            var name = kv.Key;
            var data = kv.Value;
            var compress = !name.Equals("mimetype", StringComparison.OrdinalIgnoreCase);
            var lvl = compress ? CompressionLevel.Optimal : CompressionLevel.NoCompression;
            var entry = zip.CreateEntry(name, lvl);
            using var es = entry.Open();
            byte[] bytes = data.StartsWith("BIN:")
                ? Convert.FromBase64String(data.Substring(4))
                : Encoding.UTF8.GetBytes(data);
            es.Write(bytes, 0, bytes.Length);
        }
    }
}
