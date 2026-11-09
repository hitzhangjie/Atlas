using Atlas.Core;
using System.Windows;
using System.Windows.Input;

namespace Atlas.App;

/// <summary>
/// 保存 / 重命名「我的配置」的名称输入对话框。
/// 校验逻辑由调用方传入（复用 Core 的 FanProfileService.ValidateProfileName），
/// 通过后把规范化后的名称放进 <see cref="ResultName"/>。
/// </summary>
public partial class ProfileNameWindow : Window
{
    private readonly Func<string, ProfileNameValidation> _validate;

    public string ResultName { get; private set; } = "";

    public ProfileNameWindow(string title, string subtitle, string initialName,
        Func<string, ProfileNameValidation> validate)
    {
        InitializeComponent();
        WindowChromeHelper.Enable(this);

        _validate = validate;
        Title = title;
        TitleBarControl.Title = title;
        HeaderText.Text = subtitle;
        NameBox.Text = initialName ?? "";

        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            TryAccept();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => TryAccept();

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void TryAccept()
    {
        ProfileNameValidation validation;
        try
        {
            validation = _validate(NameBox.Text);
        }
        catch (Exception ex)
        {
            ShowError($"校验名称时出错：{ex.Message}");
            return;
        }

        if (!validation.IsValid)
        {
            ShowError(validation.Message);
            return;
        }

        ResultName = validation.Normalized;
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        NameBox.Focus();
        NameBox.SelectAll();
    }
}
