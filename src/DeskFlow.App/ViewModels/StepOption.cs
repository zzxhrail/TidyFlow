namespace DeskFlow.App.ViewModels;

/// <summary>界面上的一个步骤开关，来源于 JobCatalog，而不是写死在 XAML 里。</summary>
public sealed class StepOption : ObservableObject
{
    private bool _isEnabled = true;

    public StepOption(string name, string description, bool isEnabled = true)
    {
        Name = name;
        Description = description;
        _isEnabled = isEnabled;
    }

    public string Name { get; }

    public string Description { get; }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }
}
