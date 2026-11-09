using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Atlas.App.Controls;

/// <summary>内置场景（只读）在 <see cref="SceneBar"/> 里的一个条目。</summary>
public sealed class SceneBarSceneItem
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public bool IsActive { get; init; }
}

/// <summary>「我的配置」下拉框里的一个条目。</summary>
public sealed class SceneBarProfileItem
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string DisplayName { get; init; } = ""; // 例如 "夜间安静 · 11-09 14:20"
}

/// <summary>
/// SceneBar 的一次完整渲染状态。MainWindow 每秒可能大量构造它，
/// 因此 Render 内部会做签名比较：状态没变就完全不碰可视化树（避免收起用户正在展开的下拉框）。
/// </summary>
public sealed class SceneBarViewState
{
    public IReadOnlyList<SceneBarSceneItem> Scenes { get; init; } = Array.Empty<SceneBarSceneItem>();
    public IReadOnlyList<SceneBarProfileItem> Profiles { get; init; } = Array.Empty<SceneBarProfileItem>();
    public string? SelectedProfileId { get; init; }
    public string StatusText { get; init; } = "";   // 例如 "当前：内置场景 · 均衡"
    public string StatusHint { get; init; } = "";   // 例如 "已修改，尚未保存为配置"
    public bool IsDirty { get; init; }
    public bool Interactable { get; init; } = true;
}

/// <summary>
/// 主窗口里「场景模板 / 我的配置 / 当前方案」的紧凑单行卡片。
/// 所有业务动作都通过事件上抛给 MainWindow，控件本身只负责展示状态。
/// 公开 <see cref="Render(SceneBarViewState)"/> 便于离屏渲染测试。
/// </summary>
public partial class SceneBar : UserControl
{
    private static readonly SceneBarProfileItem EmptyProfileItem = new()
    {
        Id = "",
        Name = "还没有配置",
        DisplayName = "还没有配置",
    };

    private readonly List<Button> _sceneButtons = new();
    private readonly List<SceneBarProfileItem> _profileItems = new();

    private bool _suppressEvents;
    private bool _interactable = true;
    private bool _hasProfiles;
    private string? _selectedProfileId;
    private string _lastSignature = "\0";
    private string _profileItemsSignature = "\0";

    public SceneBar()
    {
        InitializeComponent();
        ApplyEnabledState();
    }

    public event EventHandler<string>? SceneApplyRequested;       // scene.Id
    public event EventHandler<string>? ProfileApplyRequested;     // profile.Id
    public event EventHandler<string?>? ProfileSelectionChanged;  // 用户下拉选择变化
    public event EventHandler? SaveProfileRequested;
    public event EventHandler? UpdateProfileRequested;
    public event EventHandler? RenameProfileRequested;
    public event EventHandler? DeleteProfileRequested;

    /// <summary>当前下拉框里选中的「我的配置」Id；没有配置时为 null。</summary>
    public string? SelectedProfileId => _selectedProfileId;

    /// <summary>false = 初始化期间禁用所有交互按钮（状态仍然显示）。</summary>
    public bool Interactable
    {
        get => _interactable;
        set
        {
            if (_interactable == value) return;
            _interactable = value;
            ApplyEnabledState();
        }
    }

    /// <summary>幂等渲染：状态完全没变时不会重建控件，也不会触发任何事件。</summary>
    public void Render(SceneBarViewState state)
    {
        if (state is null) return;

        var signature = BuildSignature(state);
        if (string.Equals(signature, _lastSignature, StringComparison.Ordinal)) return;
        _lastSignature = signature;

        _suppressEvents = true;
        try
        {
            UpdateSceneButtons(state);
            UpdateProfileCombo(state);
            UpdateStatus(state);
            _interactable = state.Interactable;
            ApplyEnabledState();
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private static string BuildSignature(SceneBarViewState state)
    {
        var builder = new System.Text.StringBuilder(256);
        builder.Append(state.StatusText).Append('\u0001')
            .Append(state.StatusHint).Append('\u0001')
            .Append(state.IsDirty).Append('\u0001')
            .Append(state.Interactable).Append('\u0001')
            .Append(state.SelectedProfileId ?? "").Append('\u0001');

        foreach (var scene in state.Scenes)
        {
            builder.Append(scene.Id).Append('\u0002')
                .Append(scene.Name).Append('\u0002')
                .Append(scene.Description).Append('\u0002')
                .Append(scene.IsActive).Append('\u0003');
        }

        builder.Append('\u0001');
        foreach (var profile in state.Profiles)
        {
            builder.Append(profile.Id).Append('\u0002')
                .Append(profile.Name).Append('\u0002')
                .Append(profile.DisplayName).Append('\u0003');
        }

        return builder.ToString();
    }

    private void UpdateSceneButtons(SceneBarViewState state)
    {
        var scenes = state.Scenes;

        // 只保留需要数量的按钮；场景数量在 Core 里是固定的，这里做通用处理。
        while (_sceneButtons.Count > scenes.Count)
        {
            var last = _sceneButtons[^1];
            SceneButtonsPanel.Children.Remove(last);
            _sceneButtons.RemoveAt(_sceneButtons.Count - 1);
        }

        while (_sceneButtons.Count < scenes.Count)
        {
            var button = new Button
            {
                Style = (Style)FindResource("SmallGhostButton"),
                Padding = new Thickness(11, 4, 11, 4),
                Margin = new Thickness(0, 0, 5, 0),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTipService.SetShowOnDisabled(button, true);
            button.Click += SceneButton_Click;
            SceneButtonsPanel.Children.Add(button);
            _sceneButtons.Add(button);
        }

        for (var i = 0; i < scenes.Count; i++)
        {
            var item = scenes[i];
            var button = _sceneButtons[i];

            button.Content = item.Name;
            button.Tag = item.Id;
            button.ToolTip = $"{item.Name} · 所有未锁定通道套用「{item.Name}」模板（只读）";

            // 激活高亮只在「未修改」时生效：已修改说明当前方案已偏离该场景。
            // 直接设本地属性，避免依赖 UserControl.Resources 里对 App 资源的 BasedOn 解析。
            if (item.IsActive && !state.IsDirty)
            {
                button.Background = (Brush)FindResource("AccentSoftBrush");
                button.BorderBrush = (Brush)FindResource("AccentBrush");
                button.Foreground = (Brush)FindResource("TextBrush");
            }
            else
            {
                button.ClearValue(BackgroundProperty);
                button.ClearValue(BorderBrushProperty);
                button.ClearValue(ForegroundProperty);
            }
        }
    }

    private void UpdateProfileCombo(SceneBarViewState state)
    {
        var profileItemsSignature = string.Join("\u0001",
            state.Profiles.Select(p => p.Id + "\u0002" + p.DisplayName));

        if (!string.Equals(profileItemsSignature, _profileItemsSignature, StringComparison.Ordinal))
        {
            _profileItemsSignature = profileItemsSignature;
            _profileItems.Clear();
            _profileItems.AddRange(state.Profiles);
            _hasProfiles = _profileItems.Count > 0;

            if (_hasProfiles)
            {
                ProfileCombo.ItemsSource = _profileItems;
            }
            else
            {
                ProfileCombo.ItemsSource = new[] { EmptyProfileItem };
                ProfileCombo.SelectedIndex = 0;
            }
        }

        if (_hasProfiles)
        {
            var selected = _profileItems.FirstOrDefault(p =>
                               string.Equals(p.Id, state.SelectedProfileId, StringComparison.Ordinal))
                           ?? _profileItems[0];
            ProfileCombo.SelectedItem = selected;
            _selectedProfileId = selected.Id;
        }
        else
        {
            ProfileCombo.SelectedIndex = 0;
            _selectedProfileId = null;
        }
    }

    private void UpdateStatus(SceneBarViewState state)
    {
        StatusTextBlock.Text = state.StatusText ?? "";
        StatusTextBlock.ToolTip = state.StatusText ?? "";

        // 窄窗口下不再把提示小字挤在状态 chip 里（会被裁切），改为 tooltip 展示完整语义
        StatusChip.ToolTip = string.IsNullOrEmpty(state.StatusHint)
            ? "当前方案 = 正在编辑并实时生效的设置。套用场景/配置后若再改动扇通道，会标记为「已修改」，需要另存为配置才能保留。"
            : state.StatusHint + "\n当前方案 = 正在编辑并实时生效的设置。套用场景/配置后若再改动扇通道，会标记为「已修改」，需要另存为配置才能保留。";

        StatusDot.Fill = state.IsDirty
            ? (Brush)FindResource("AmberBrush")
            : (Brush)FindResource("GreenBrush");
    }

    private void ApplyEnabledState()
    {
        SceneButtonsPanel.IsEnabled = _interactable;
        ProfileCombo.IsEnabled = _interactable && _hasProfiles;
        ApplyProfileButton.IsEnabled = _interactable && _hasProfiles;
        SaveProfileButton.IsEnabled = _interactable;
        UpdateProfileButton.IsEnabled = _interactable && _hasProfiles;
        RenameProfileButton.IsEnabled = _interactable && _hasProfiles;
        DeleteProfileButton.IsEnabled = _interactable && _hasProfiles;
    }

    private void SceneButton_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        if (sender is not Button { Tag: string sceneId } || string.IsNullOrEmpty(sceneId)) return;
        SceneApplyRequested?.Invoke(this, sceneId);
    }

    private void ProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (ProfileCombo.SelectedItem is not SceneBarProfileItem item) return;
        if (string.IsNullOrEmpty(item.Id)) return;
        if (string.Equals(_selectedProfileId, item.Id, StringComparison.Ordinal)) return;

        _selectedProfileId = item.Id;
        ProfileSelectionChanged?.Invoke(this, item.Id);
    }

    private void ApplyProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || !_hasProfiles || string.IsNullOrEmpty(_selectedProfileId)) return;
        ProfileApplyRequested?.Invoke(this, _selectedProfileId);
    }

    private void SaveProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        SaveProfileRequested?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || !_hasProfiles || string.IsNullOrEmpty(_selectedProfileId)) return;
        UpdateProfileRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RenameProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || !_hasProfiles || string.IsNullOrEmpty(_selectedProfileId)) return;
        RenameProfileRequested?.Invoke(this, EventArgs.Empty);
    }

    private void DeleteProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || !_hasProfiles || string.IsNullOrEmpty(_selectedProfileId)) return;
        DeleteProfileRequested?.Invoke(this, EventArgs.Empty);
    }
}
