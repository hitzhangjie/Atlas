using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace Atlas.App;

public sealed class CopyTarget : INotifyPropertyChanged
{
    private bool _checked;

    public string Name { get; init; } = "";

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public partial class CopyCurveWindow : Window
{
    public ObservableCollection<CopyTarget> Targets { get; } = new();

    public IEnumerable<string> SelectedNames => Targets.Where(t => t.Checked).Select(t => t.Name);

    public CopyCurveWindow(string sourceName, IEnumerable<string> targetNames, string curveText)
    {
        InitializeComponent();
        WindowChromeHelper.Enable(this);
        DataContext = this;
        HeaderText.Text = $"把「{sourceName}」的曲线应用到其他通道\n{curveText}";
        foreach (var name in targetNames)
            Targets.Add(new CopyTarget { Name = name });
        TargetList.ItemsSource = Targets;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var target in Targets) target.Checked = true;
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var target in Targets) target.Checked = false;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!Targets.Any(t => t.Checked))
        {
            MessageBox.Show("请至少勾选一个通道。", "Atlas", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
