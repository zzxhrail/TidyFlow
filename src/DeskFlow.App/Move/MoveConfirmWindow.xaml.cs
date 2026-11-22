using System.Windows;

namespace DeskFlow.App.Move;

/// <summary>移动前的确认弹窗：列出「文件 / 从哪 / 到哪」，用户确认才真正移动。</summary>
public sealed partial class MoveConfirmWindow : Window
{
    public MoveConfirmWindow(string fromCategory, string toCategory, IReadOnlyList<string> fileNames)
    {
        InitializeComponent();

        Title = $"确认移动（{fileNames.Count} 个文件）";
        TitleText.Text = $"把 {fileNames.Count} 个文件移动到「{toCategory}」？";
        SummaryText.Text = $"从「{fromCategory}」→「{toCategory}」。同名文件会自动改名（加 (2)），不会覆盖。";
        HintText.Text = "确认后立即移动，可在「归档记录」里看到这条移动记录。";

        PreviewList.ItemsSource = fileNames
            .Select(name => new MovePreviewRow(name, fromCategory, toCategory))
            .ToList();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    public sealed record MovePreviewRow(string Name, string FromCategory, string ToCategory);
}
