using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using NetOps.Domain;

namespace NetOps.Infrastructure;

/// <summary>只读取首张可见工作表中的单元格值；不运行公式、宏、外部链接或解压到磁盘。</summary>
public static class DeviceXlsx
{
    public static Device[] Parse(Stream stream)
    {
        if (!stream.CanSeek || stream.Length > 2_000_000) throw new ArgumentException("XLSX 文件最多 2 MB。");
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, true);
        if (zip.Entries.Count > 128 || zip.Entries.Sum(e => e.Length) > 10_000_000) throw new ArgumentException("工作簿内容过大，请只保留设备清单后重试。");
        XDocument Read(string path)
        {
            if (path.Contains("..") || path.StartsWith('/')) throw new ArgumentException("工作簿路径无效。");
            var entry = zip.GetEntry(path) ?? throw new ArgumentException("工作簿缺少必要内容。");
            if (entry.Length > 2_000_000) throw new ArgumentException("工作表内容最多 2 MB。");
            using var input = entry.Open(); using var reader = XmlReader.Create(input, new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2_000_000 }); return XDocument.Load(reader);
        }
        XNamespace sheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace docRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var workbook = Read("xl/workbook.xml"); var sheet = workbook.Descendants(sheetNs + "sheet").FirstOrDefault(s => (string?)s.Attribute("state") is not ("hidden" or "veryHidden")) ?? throw new ArgumentException("没有可见工作表。");
        var id = (string?)sheet.Attribute(docRel + "id"); var relationship = Read("xl/_rels/workbook.xml.rels").Root?.Elements().SingleOrDefault(e => (string?)e.Attribute("Id") == id);
        if (relationship is null || (string?)relationship.Attribute("TargetMode") == "External") throw new ArgumentException("首张工作表关系无效。");
        var target = ((string?)relationship.Attribute("Target") ?? "").Replace('\\', '/');
        var path = target.StartsWith("/xl/", StringComparison.Ordinal) ? target[1..] : "xl/" + target;
        var shared = zip.GetEntry("xl/sharedStrings.xml") is null ? [] : Read("xl/sharedStrings.xml").Descendants(sheetNs + "si").Select(e => string.Concat(e.Descendants(sheetNs + "t").Select(t => t.Value))).ToArray();
        var table = Read(path); var lines = new List<string>();
        foreach (var row in table.Descendants(sheetNs + "row"))
        {
            if (lines.Count > 1000) throw new ArgumentException("每次最多导入 1000 台设备。");
            var cells = new string[32]; var next = 0;
            foreach (var cell in row.Elements(sheetNs + "c"))
            {
                if (cell.Element(sheetNs + "f") is not null) throw new ArgumentException("清单含公式，请将单元格转换为值后导入。");
                var reference = (string?)cell.Attribute("r"); var col = 0;
                if (reference is not null) { foreach (var ch in reference.TakeWhile(char.IsLetter)) { if (ch is < 'A' or > 'Z') throw new ArgumentException("单元格地址无效。"); col = checked(col * 26 + ch - 'A' + 1); } col--; } else col = next;
                if (col is < 0 or >= 32) throw new ArgumentException("设备清单最多 32 列。");
                var type = (string?)cell.Attribute("t"); var value = cell.Element(sheetNs + "v")?.Value ?? "";
                if (type == "s") { if (!int.TryParse(value, out var index) || index < 0 || index >= shared.Length) throw new ArgumentException("共享文本索引无效。"); value = shared[index]; }
                else if (type == "inlineStr") value = string.Concat(cell.Descendants(sheetNs + "t").Select(e => e.Value));
                else if (type == "e") throw new ArgumentException("清单包含错误单元格。");
                cells[col] = value; next = col + 1;
            }
            if (cells.All(string.IsNullOrWhiteSpace)) continue;
            lines.Add(string.Join(",", cells.Select(s => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"")));
        }
        return DeviceCsv.Parse(new StringReader(string.Join("\n", lines)));
    }
}
