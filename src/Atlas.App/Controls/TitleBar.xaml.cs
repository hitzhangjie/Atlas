using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Atlas.App.Controls;

public partial class TitleBar : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(TitleBar), new PropertyMetadata("Atlas"));

    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(
        nameof(Subtitle), typeof(string), typeof(TitleBar), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty VersionProperty = DependencyProperty.Register(
        nameof(Version), typeof(string), typeof(TitleBar), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty StatusTextProperty = DependencyProperty.Register(
        nameof(StatusText), typeof(string), typeof(TitleBar), new PropertyMetadata(string.Empty));

    /// <summary>主窗口开启：标题栏右侧显示「设置」入口（弹窗不显示）。</summary>
    public static readonly DependencyProperty ShowSettingsProperty = DependencyProperty.Register(
        nameof(ShowSettings), typeof(bool), typeof(TitleBar), new PropertyMetadata(false));

    /// <summary>用户点击标题栏「设置」。</summary>
    public event EventHandler? SettingsRequested;

    public TitleBar()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public string Version
    {
        get => (string)GetValue(VersionProperty);
        set => SetValue(VersionProperty, value);
    }

    public string StatusText
    {
        get => (string)GetValue(StatusTextProperty);
        set => SetValue(StatusTextProperty, value);
    }

    public bool ShowSettings
    {
        get => (bool)GetValue(ShowSettingsProperty);
        set => SetValue(ShowSettingsProperty, value);
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) =>
        SettingsRequested?.Invoke(this, EventArgs.Empty);

    private Window? _window;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _window = Window.GetWindow(this);
        if (_window != null)
        {
            _window.StateChanged += (_, _) => UpdateMaximizeIcon();
            UpdateMaximizeIcon();

            // 固定尺寸窗口（如设置窗口）没有最大化意义，隐藏最大化按钮
            if (_window.ResizeMode is ResizeMode.NoResize or ResizeMode.CanMinimize)
                MaxButton.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateMaximizeIcon()
    {
        var max = _window?.WindowState == WindowState.Maximized;
        MaxIcon.Data = (Geometry)(Application.Current.TryFindResource(max ? "IconRestoreWindow" : "IconMax") ?? Geometry.Empty);
        MaxButton.ToolTip = max ? "还原" : "最大化";
    }

    private void MinButton_Click(object sender, RoutedEventArgs e)
    {
        if (_window != null) _window.WindowState = WindowState.Minimized;
    }

    private void MaxButton_Click(object sender, RoutedEventArgs e)
    {
        if (_window == null) return;
        _window.WindowState = _window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => _window?.Close();
}
