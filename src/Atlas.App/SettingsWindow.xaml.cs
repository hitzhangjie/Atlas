using System.Windows;

namespace Atlas.App;

/// <summary>设置窗口：目前只有开机启动，后续可以放轮询间隔、看门狗等选项。</summary>
public partial class SettingsWindow : Window
{
    private bool _updatingCheck;

    public SettingsWindow()
    {
        InitializeComponent();
        WindowChromeHelper.Enable(this);

        _updatingCheck = true;
        try
        {
            AutoStartCheck.IsChecked = AutoStartService.IsInstalled();
        }
        finally
        {
            _updatingCheck = false;
        }

        StatusTextBlock.Text = AutoStartCheck.IsChecked == true
            ? "当前已启用开机启动"
            : "当前未启用开机启动";
    }

    private void AutoStartCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingCheck) return;

        var enabled = AutoStartCheck.IsChecked == true;
        AutoStartCheck.IsEnabled = false;
        try
        {
            if (AutoStartService.SetEnabled(enabled, out var message))
            {
                StatusTextBlock.Text = message;
            }
            else
            {
                StatusTextBlock.Text = message;
                // 操作失败：把勾选状态回退到实际状态
                _updatingCheck = true;
                try { AutoStartCheck.IsChecked = AutoStartService.IsInstalled(); }
                finally { _updatingCheck = false; }
            }
        }
        finally
        {
            AutoStartCheck.IsEnabled = true;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
