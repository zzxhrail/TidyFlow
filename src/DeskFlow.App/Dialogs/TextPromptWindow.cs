using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DeskFlow.App.Dialogs;

/// <summary>一个极简的"输入一行文字"对话框，给新建/重命名分类用。</summary>
public sealed class TextPromptWindow : Window
{
    private readonly TextBox _input;

    private TextPromptWindow(string title, string label, string initial)
    {
        Title = title;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = (Brush?)Application.Current.TryFindResource("SurfaceStrongBrush") ?? new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2C));
        WindowStyle = WindowStyle.ToolWindow;
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/icon-128.png"));

        var root = new StackPanel { Margin = new Thickness(20) };

        root.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = (Brush?)Application.Current.TryFindResource("TextSecondaryBrush") ?? Brushes.Gray,
            Margin = new Thickness(0, 0, 0, 8),
        });

        _input = new TextBox { Text = initial, MinWidth = 340 };
        _input.SelectAll();
        root.Children.Add(_input);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };

        var ok = new Button
        {
            Content = "确定",
            MinWidth = 88,
            Margin = new Thickness(0, 0, 8, 0),
            Style = Application.Current.TryFindResource("AccentButton") as Style,
            IsDefault = true,
        };
        ok.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_input.Text))
            {
                Result = _input.Text.Trim();
                DialogResult = true;
            }
        };

        var cancel = new Button { Content = "取消", MinWidth = 88, IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        Content = root;
        Loaded += (_, _) =>
        {
            Services.Animations.FadeSlideIn(root, offsetY: 6, milliseconds: 140);
            _input.Focus();
        };
    }

    public string? Result { get; private set; }

    public static string? Ask(Window owner, string title, string label, string initial = "")
    {
        var window = new TextPromptWindow(title, label, initial) { Owner = owner };
        return window.ShowDialog() == true ? window.Result : null;
    }
}
