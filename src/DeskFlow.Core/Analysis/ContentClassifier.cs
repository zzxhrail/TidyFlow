namespace DeskFlow.Core.Analysis;

/// <summary>按文件内容判定的分类结果。</summary>
public sealed record ContentClassification(string Category, double Confidence, string Reason, int Score);

/// <summary>
/// 内容分类器：读文件内容（不是只看扩展名）判断该归到哪个类别。
/// 判不准时返回 null，交给扩展名规则兜底。
/// </summary>
public interface IContentClassifier
{
    ContentClassification? Classify(string path, string fileName);
}
