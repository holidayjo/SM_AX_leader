using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace GovAssistant.Services;

/// <summary>
/// 범정부오피스 미연결 시 텍스트를 기본 .hwpx 파일로 저장하는 폴백.
/// HWPX = ZIP 구조. 텍스트 단락을 hp:p/hp:run/hp:t 구조로 직렬화.
/// </summary>
public static class HwpxWriter
{
    private const string NsHsp = "http://www.hancom.co.kr/hwpml/2012/section";
    private const string NsHp  = "http://www.hancom.co.kr/hwpml/2012/paragraph";
    private const string NsHh  = "http://www.hancom.co.kr/hwpml/2012/head";

    public static void Write(string filePath, string content)
    {
        using var fs   = new FileStream(filePath, FileMode.Create, FileAccess.Write);
        using var zip  = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8);

        // mimetype (비압축 필수)
        AddEntry(zip, "mimetype", "application/hwp+zip", compress: false);
        AddEntry(zip, "[Content_Types].xml",  ContentTypesXml());
        AddEntry(zip, "_rels/.rels",           RelsXml());
        AddEntry(zip, "Contents/content.hpf", ContentHpf());
        AddEntry(zip, "Contents/header.xml",  HeaderXml());
        AddEntry(zip, "Contents/section0.xml", SectionXml(content));
    }

    private static void AddEntry(ZipArchive zip, string name, string text, bool compress = true)
    {
        var level = compress ? CompressionLevel.Optimal : CompressionLevel.NoCompression;
        var entry = zip.CreateEntry(name, level);
        using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
        w.Write(text);
    }

    private static string ContentTypesXml() => """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
          <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
          <Default Extension="xml"  ContentType="application/xml"/>
          <Default Extension="hpf"  ContentType="application/x-hwp-v2"/>
        </Types>
        """;

    private static string RelsXml() => """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://www.hancom.co.kr/hwpml/2012/core" Target="Contents/content.hpf"/>
        </Relationships>
        """;

    private static string ContentHpf() => """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <hpf:Package xmlns:hpf="http://www.hancom.co.kr/hwpml/2012/package">
          <hpf:Manifest>
            <hpf:Item Id="header"   MediaType="application/xml" HRef="Contents/header.xml"/>
            <hpf:Item Id="section0" MediaType="application/xml" HRef="Contents/section0.xml"/>
          </hpf:Manifest>
          <hpf:Spine>
            <hpf:ItemRef IdRef="section0"/>
          </hpf:Spine>
        </hpf:Package>
        """;

    private static string HeaderXml() => $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <hh:Head xmlns:hh="{NsHh}">
          <hh:DocInfo>
            <hh:HWPSummaryInfo>
              <hh:Title>생성된 공문</hh:Title>
            </hh:HWPSummaryInfo>
          </hh:DocInfo>
        </hh:Head>
        """;

    private static string SectionXml(string content)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        sb.AppendLine($"""<hsp:SEC xmlns:hsp="{NsHsp}" xmlns:hp="{NsHp}">""");
        sb.AppendLine("""  <hsp:SectionDefinition>""");
        sb.AppendLine("""    <hsp:PageDef Width="59528" Height="84188">""");
        sb.AppendLine("""      <hsp:PageMargin Left="8504" Right="8504" Top="5669" Bottom="4252" Header="4252" Footer="4252" Gutter="0"/>""");
        sb.AppendLine("""    </hsp:PageDef>""");
        sb.AppendLine("""  </hsp:SectionDefinition>""");

        // 빈 줄 포함 각 줄을 단락으로
        var lines = content.Replace("\r\n", "\n").Split('\n');
        foreach (var line in lines)
        {
            sb.AppendLine("""  <hp:p>""");
            sb.AppendLine("""    <hp:run>""");
            sb.AppendLine("""      <hp:CharShape/>""");
            sb.AppendLine($"      <hp:t>{EscapeXml(line)}</hp:t>");
            sb.AppendLine("""    </hp:run>""");
            sb.AppendLine("""  </hp:p>""");
        }

        sb.AppendLine("</hsp:SEC>");
        return sb.ToString();
    }

    private static string EscapeXml(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
         .Replace("\"", "&quot;").Replace("'", "&apos;");
}
