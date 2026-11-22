using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using DeskFlow.Core.Analysis;

namespace DeskFlow.Infrastructure.Analysis;

/// <summary>
/// 内置的"按内容分类"：把文档内容抽成文本，再用加权关键词打分选类别。
/// 完全离线，不联网、不依赖模型文件；判不准时返回 null，交给扩展名规则。
/// </summary>
public sealed partial class KeywordContentClassifier : IContentClassifier
{
    /// <summary>低于这个分数就认为证据不足，不硬猜。</summary>
    private const int MinimumScore = 3;

    /// <summary>最多读多少字节，避免大文件拖慢归档。</summary>
    private const int MaxTextBytes = 256 * 1024;

    private static readonly (string Category, string[] Keywords)[] Rules =
    [
        ("发票", ["发票", "价税合计", "开票日期", "纳税人识别号", "增值税", "invoice", "税额", "销售方", "购买方"]),
        ("合同", ["合同", "协议", "甲方", "乙方", "签署", "违约责任", "条款", "agreement", "contract", "party a", "party b"]),
        ("简历", ["简历", "求职", "应聘", "教育经历", "工作经历", "项目经验", "resume", "curriculum vitae", "自我评价"]),
        ("会议纪要", ["会议纪要", "参会人员", "议程", "会议时间", "决议", "纪要", "minutes of meeting", "attendees"]),
        ("财务", ["预算", "决算", "资产负债表", "利润表", "应收", "应付", "成本", "费用", "budget", "cash flow"]),
        ("报告", ["报告", "方案", "总结", "汇报", "计划", "调研", "分析报告", "report", "proposal", "summary"]),
        ("代码", ["namespace ", "public class", "using ", "#include", "function ", "def ", "import ", "select * from", "const ", "=>"]),
        ("演示文稿", ["presentation", "幻灯片", "议程", "slide", "演讲", "汇报材料"]),
        ("表格数据", ["sheet", "行号", "合计", "小计", "统计", "pivot"]),
        ("说明书", ["用户手册", "安装说明", "快速开始", "注意事项", "manual", "user guide", "getting started"]),
    ];

    public ContentClassification? Classify(string path, string fileName)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        string text;
        try
        {
            text = ExtractText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }

        if (text.Length < 16)
        {
            return null;
        }

        // 文件名也参与打分（权重较低），避免正文抽取失败时完全靠猜
        var nameBonus = fileName.ToLowerInvariant();
        ContentClassification? best = null;

        foreach (var (category, keywords) in Rules)
        {
            var score = 0;
            var hits = new List<string>();

            foreach (var keyword in keywords)
            {
                var count = CountOccurrences(text, keyword);
                if (count > 0)
                {
                    score += Math.Min(count, 4) * 2;
                    hits.Add(keyword);
                }

                if (nameBonus.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    score += 3;
                    if (!hits.Contains(keyword))
                    {
                        hits.Add(keyword);
                    }
                }
            }

            if (score < MinimumScore)
            {
                continue;
            }

            var confidence = Math.Min(0.99, score / 20.0);
            if (best is null || score > best.Score)
            {
                best = new ContentClassification(category, confidence, string.Join("、", hits.Take(4)), score);
            }
        }

        return best;
    }

    private static int CountOccurrences(string haystack, string needle) =>
        string.IsNullOrEmpty(needle) ? 0 : haystack.Split(needle, StringSplitOptions.None).Length - 1;

    /// <summary>把不同格式抽成纯文本；抽不出来就返回空串。</summary>
    private static string ExtractText(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();

        return extension switch
        {
            ".docx" => ReadZipText(path, "word/document.xml"),
            ".xlsx" => ReadZipText(path, "xl/sharedStrings.xml"),
            ".pptx" => ReadZipText(path, entries: ["ppt/slides/slide1.xml", "ppt/slides/slide2.xml"]),
            ".pdf" => ReadPdfText(path),
            ".txt" or ".md" or ".log" or ".json" or ".xml" or ".csv" or ".yml" or ".yaml" or ".ini"
                or ".cs" or ".xaml" or ".js" or ".ts" or ".py" or ".java" or ".html" or ".css" or ".sql" => ReadPlainText(path),
            _ => string.Empty,
        };
    }

    private static string ReadPlainText(string path)
    {
        using var stream = File.OpenRead(path);
        var length = (int)Math.Min(stream.Length, MaxTextBytes);
        var buffer = new byte[length];
        stream.ReadExactly(buffer, 0, length);
        return Encoding.UTF8.GetString(buffer);
    }

    private static string ReadZipText(string path, string? entryName = null, string[]? entries = null)
    {
        using var archive = ZipFile.OpenRead(path);
        var builder = new StringBuilder();

        var targets = entries ?? (entryName is null ? [] : [entryName]);
        foreach (var target in targets)
        {
            var entry = archive.GetEntry(target);
            if (entry is null)
            {
                continue;
            }

            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            var xml = reader.ReadToEnd();
            builder.Append(StripTags(xml));
            if (builder.Length > MaxTextBytes)
            {
                break;
            }
        }

        return builder.Length > MaxTextBytes ? builder.ToString(0, MaxTextBytes) : builder.ToString();
    }

    /// <summary>PDF 没有第三方库时只能粗抽：取 BT/ET 之间的字面量，中文多半抽不到，但英文关键词够用。</summary>
    private static string ReadPdfText(string path)
    {
        var raw = ReadPlainText(path);
        var builder = new StringBuilder();
        foreach (Match match in PdfTextRegex().Matches(raw))
        {
            builder.Append(match.Groups[1].Value).Append(' ');
        }

        return builder.Length > 0 ? builder.ToString() : raw;
    }

    private static string StripTags(string xml) => TagRegex().Replace(xml, " ");

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\((.{2,80}?)\)\s*Tj")]
    private static partial Regex PdfTextRegex();
}
