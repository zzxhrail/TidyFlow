using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using DeskFlow.Core.Analysis;

namespace DeskFlow.Infrastructure.Analysis;

/// <summary>
/// 内置的"按内容分类"。设计原则：**宁可判不出来，也不要判错** ——
/// 判不准就返回 null，交给扩展名规则兜底（那至少不会把合同塞进"报告"）。
///
/// 打分模型：
///   1. 关键词带权重（强证据 5~7 分、中等 3~4、弱 1~2），同一词出现次数封顶 3 次；
///   2. 文件名命中也算分（权重折半），很多人会把类型写进文件名；
///   3. 文件类型与类别"搭不搭"给 ±分（.xlsx 不可能是简历）；
///   4. 必须同时满足：最高分 ≥ 该类别门槛、且 ≥ 第二名 1.5 倍，否则放弃判定。
/// </summary>
public sealed partial class KeywordContentClassifier : IContentClassifier
{
    private const int MaxTextBytes = 256 * 1024;

    private static readonly (string Category, (string Word, int Weight)[] Words, int MinScore, string[]? ForbiddenFor)[] Rules =
    [
        ("发票",
            [
                ("增值税", 6), ("价税合计", 6), ("开票日期", 6), ("纳税人识别号", 6),
                ("发票代码", 5), ("发票号码", 5), ("销售方", 3), ("购买方", 3), ("税额", 3),
                ("invoice", 5), ("报销单", 4), ("电子发票", 5),
            ],
            8,
            [".pptx", ".xlsx"]),

        ("合同",
            [
                ("甲方", 5), ("乙方", 5), ("违约责任", 6), ("本协议", 5), ("合同编号", 4),
                ("签署", 2), ("条款", 2), ("协议", 2), ("出租方", 4), ("承租方", 4),
                ("contract", 5), ("agreement", 4), ("甲方签字", 4), ("盖章", 3),
            ],
            9,
            [".pptx", ".xlsx"]),

        ("简历",
            [
                ("教育经历", 6), ("工作经历", 6), ("项目经验", 5), ("求职意向", 6),
                ("个人简历", 6), ("应聘", 3), ("resume", 6), ("curriculum vitae", 6),
                ("自我评价", 3), ("技能特长", 4), ("毕业院校", 4),
            ],
            9,
            [".pptx", ".xlsx"]),

        ("会议纪要",
            [
                ("会议纪要", 7), ("参会人员", 6), ("会议时间", 5), ("会议记录", 6),
                ("议程", 3), ("决议", 4), ("议题", 3), ("attendees", 5), ("minutes", 3),
            ],
            9,
            [".pptx", ".xlsx"]),

        ("财务",
            [
                ("资产负债表", 7), ("利润表", 7), ("现金流量表", 7), ("预算", 4),
                ("决算", 4), ("应收", 3), ("应付", 3), ("科目", 2), ("成本", 2),
                ("费用", 2), ("budget", 4), ("cash flow", 6), ("毛利率", 4),
            ],
            8,
            null),

        ("报告",
            [
                ("分析报告", 5), ("调研报告", 5), ("工作报告", 5), ("年度报告", 5),
                ("总结", 2), ("方案", 2), ("汇报", 3), ("report", 3), ("summary", 2),
                ("结论与建议", 4),
            ],
            8,
            [".pptx"]),

        ("代码",
            [
                ("namespace", 5), ("public class", 5), ("#include", 5), ("function ", 4),
                ("import ", 3), ("def ", 4), ("select * from", 5), ("console.log", 4),
                ("</html>", 5), ("void main", 5),
            ],
            8,
            [".pptx", ".xlsx", ".docx"]),

        ("演示文稿",
            [
                ("幻灯片", 6), ("presentation", 5), ("slide", 4), ("演讲", 3),
                ("汇报材料", 5), ("演示文稿", 6), ("大纲视图", 4),
            ],
            8,
            null),

        ("学习资料",
            [
                ("课件", 6), ("讲义", 6), ("教学大纲", 6), ("习题", 5), ("试卷", 5),
                ("考试", 3), ("课程", 3), ("实验指导", 5), ("章节练习", 5),
            ],
            10,
            null),

        ("通知公告",
            [
                ("特此通知", 7), ("通知如下", 6), ("关于", 1), ("公告", 5),
                ("全体师生", 5), ("请于", 3), ("报名时间", 4), ("截止日期", 4),
            ],
            10,
            null),

        ("说明书",
            [
                ("用户手册", 6), ("安装说明", 6), ("快速开始", 5), ("getting started", 5),
                ("user guide", 5), ("manual", 4), ("使用说明", 4), ("常见问题", 3),
            ],
            9,
            null),
    ];

    /// <summary>扩展名与类别的"搭不搭"：搭调 +2 分；不搭调在 Rules 里直接跳过。</summary>
    private static readonly Dictionary<string, string[]> ExtensionAffinity = new(StringComparer.OrdinalIgnoreCase)
    {
        [".xlsx"] = ["财务"],
        [".xls"] = ["财务"],
        [".csv"] = ["财务"],
        [".pptx"] = ["演示文稿"],
        [".ppt"] = ["演示文稿"],
    };

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

        // 正文太短（<8 字）就只看文件名；8 字以上正常参与打分（门槛分本身已经很高，不会误判）
        var haystack = text.Length >= 8 ? text : string.Empty;
        var nameLower = fileName.ToLowerInvariant();
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var affinity = ExtensionAffinity.TryGetValue(extension, out var categories) ? categories : [];

        var scored = new List<(string Category, int Score, List<string> Hits)>();

        foreach (var (category, words, minScore, forbiddenFor) in Rules)
        {
            if (forbiddenFor is not null && forbiddenFor.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var score = 0;
            var hits = new List<string>();

            foreach (var (word, weight) in words)
            {
                var count = CountOccurrences(haystack, word);
                if (count > 0)
                {
                    score += weight * Math.Min(count, 3);
                    hits.Add(word);
                }

                if (nameLower.Contains(word, StringComparison.OrdinalIgnoreCase))
                {
                    score += Math.Max(2, weight / 2);
                    if (!hits.Contains(word))
                    {
                        hits.Add(word);
                    }
                }
            }

            if (score == 0)
            {
                continue;
            }

            if (affinity.Contains(category, StringComparer.OrdinalIgnoreCase))
            {
                score += 2;
            }

            if (score < minScore)
            {
                continue;
            }

            scored.Add((category, score, hits));
        }

        if (scored.Count == 0)
        {
            return null;
        }

        var ordered = scored.OrderByDescending(static s => s.Score).ToList();
        var best = ordered[0];
        var runnerUp = ordered.Count > 1 ? ordered[1].Score : 0;

        // 两个类别咬得很紧 → 不硬猜，交回扩展名规则
        if (runnerUp > 0 && best.Score < runnerUp * 1.5)
        {
            return null;
        }

        var confidence = Math.Min(0.99, (double)best.Score / (best.Score + runnerUp + 6));
        return new ContentClassification(best.Category, confidence, string.Join("、", best.Hits.Take(4)), best.Score);
    }

    private static int CountOccurrences(string haystack, string needle) =>
        string.IsNullOrEmpty(needle) || haystack.Length == 0
            ? 0
            : haystack.Split(needle, StringSplitOptions.None).Length - 1;

    // ---------------- 文本抽取 ----------------

    private static string ExtractText(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();

        return extension switch
        {
            ".docx" => ReadOfficeXml(path, "word/"),
            ".xlsx" => ReadOfficeXml(path, "xl/"),
            ".pptx" => ReadOfficeXml(path, "ppt/"),
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

    /// <summary>
    /// 把 Office 文档里所有相关 XML 拼起来再剥标签。
    /// 旧版只读 document.xml / sharedStrings.xml / 前两页幻灯片，
    /// 页眉页脚、脚注、后续幻灯片里的关键信息全漏了 —— 这是准确率低的主因之一。
    /// </summary>
    private static string ReadOfficeXml(string path, string folderPrefix)
    {
        using var archive = ZipFile.OpenRead(path);
        var builder = new StringBuilder();

        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName;
            if (!name.StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase) ||
                !name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            builder.Append(StripTags(reader.ReadToEnd())).Append(' ');

            if (builder.Length > MaxTextBytes)
            {
                break;
            }
        }

        // 文档属性里的标题/主题/关键词往往就是分类线索
        var core = archive.GetEntry("docProps/core.xml");
        if (core is not null)
        {
            using var reader = new StreamReader(core.Open(), Encoding.UTF8);
            builder.Append(' ').Append(StripTags(reader.ReadToEnd()));
        }

        return builder.Length > MaxTextBytes ? builder.ToString(0, MaxTextBytes) : builder.ToString();
    }

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
