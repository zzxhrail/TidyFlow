using System.Collections.Specialized;
using System.Windows;
using DeskFlow.App.ViewModels;
using Microsoft.Win32;

namespace DeskFlow.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/icon-128.png"));
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
        _viewModel.Logs.CollectionChanged += OnLogsChanged;
    }

    private void OnLogsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (LogList.Items.Count > 0)
        {
            LogList.ScrollIntoView(LogList.Items[^1]);
        }
    }

    private void BrowseInput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择要处理的文件夹",
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.InputPath = dialog.FolderName;
        }
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择输出目录",
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.OutputDirectory = dialog.FolderName;
        }
    }
}
