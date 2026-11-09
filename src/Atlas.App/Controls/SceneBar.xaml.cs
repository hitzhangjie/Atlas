using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace Atlas.App.Controls;

/// <summary>下拉列表里的条目类型：内置场景（只读）/「我的配置」/ 未归档占位。</summary>
public enum SceneBarChoiceKind
{
    BuiltInScene,
    Profile,
    Draft,
}

/// <summary>「场景 / 配置」下拉框里的一个条目。</summary>
public sealed class SceneBarChoiceItem
{
    public string Id { get; init; } = "";
    public SceneBarChoiceKind Kind { get; init; }
    public string Name { get; init; } = "";

    /// <summary>类型小徽标文案：内置 · 只读 / 我的 / 未归档。</summary>
    public string TypeLabel { get; init; } = "";

    /// <summary>完整展示名（含来源/更新时间），用于 tooltip。</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>名称后的灰色补充信息（目前用于「我的配置」的更新时间），为空则不显示。</summary>
    public string DetailText { get; init; } = "";

    public bool IsReadOnly { get; init; }
}

/// <summary>
/// SceneBar 的一次完整渲染状态。MainWindow 每次轮询都可能构造它，
/// Render 内部会做签名比较：状态没变就完全不碰可视化树（避免收起用户正在展开的下拉框）。
/// </summary>
public sealed class SceneBarViewState
{
    public IReadOnlyList<SceneBarChoiceItem> Choices { get; init; } = Array.Empty<SceneBarChoiceItem>();

    /// <summary>当前选中项 Id；<see cref="SceneBar.DraftChoiceId"/> 表示「未归档」占位。</summary>
    public string? SelectedChoiceId { get; init; }

    /// <summary>当前方案是否有未保存的修改（只影响小琥珀徽标，不改变选中项）。</summary>
    public bool IsDirty { get; init; }

    public bool Interactable { get; init; } = true;
}

/// <summary>
/// 主窗口里「场景 / 配置」的紧凑单行卡片（v2）。
///
/// 设计要点：
///  - 只有一个下拉列表，内置场景 + 我的配置 + 未归档占位都放在里面，类型用徽标区分；
///  - 用户选择下拉项即视为「立即切换」，控件只上抛 <see cref="ChoiceSelected"/>，由 MainWindow 决定是否确认/回退；
///  - 用户取消切换时，MainWindow 调 <see cref="SetSelectedChoice"/> 把下拉框回退到当前项（不发事件）；
///  - 所有业务动作都通过事件上抛给 MainWindow，控件本身只负责展示状态。
/// 公开 <see cref="Render(SceneBarViewState)"/> 便于离屏渲染测试。
/// </summary>
public partial class SceneBar : UserControl
{
    /// <summary>当前方案没有来源（未归档）时，下拉里占位项的固定 Id。</summary>
    public const string DraftChoiceId = "draft";

    private readonly List<SceneBarChoiceItem> _choiceItems = new();
    private bool _suppressEvents;
    private bool _interactable = true;
    private string? _selectedChoiceId;
    private string _lastSignature = "\0";
    private string _choicesSignature = "\0";

    public SceneBar()
    {
        InitializeComponent();
        ApplyEnabledState();
    }

    /// <summary>用户在下拉列表里选择了某一项（已过滤程序化选择），参数是 <see cref="SceneBarChoiceItem.Id"/>。</summary>
    public event EventHandler<string>? ChoiceSelected;

    public event EventHandler? SaveProfileRequested;
    public event EventHandler? UpdateProfileRequested;
    public event EventHandler? RenameProfileRequested;
    public event EventHandler? DeleteProfileRequested;

    /// <summary>当前下拉框里选中的条目 Id；无选中项时为 null。</summary>
    public string? SelectedChoiceId => _selectedChoiceId;

    /// <summary>false = 初始化期间禁用所有交互（状态仍然显示）。</summary>
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
        if (string.Equals(signature, _lastSignature, StringComparison.Ordinal) &&
            IsSelectionInSync(state.SelectedChoiceId))
        {
            return;
        }

        _lastSignature = signature;

        _suppressEvents = true;
        try
        {
            UpdateChoices(state);
            SelectChoice(state.SelectedChoiceId);
            UpdateDirtyBadge(state);
            _interactable = state.Interactable;
            ApplyEnabledState();
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    /// <summary>
    /// 程序化设置下拉选中项（用于用户取消切换时回退），不会触发 <see cref="ChoiceSelected"/>。
    /// </summary>
    public void SetSelectedChoice(string? id)
    {
        _suppressEvents = true;
        try
        {
            SelectChoice(id);
            ApplyEnabledState();
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private static string BuildSignature(SceneBarViewState state)
    {
        var builder = new StringBuilder(256);
        builder.Append(state.IsDirty).Append('\u0001')
            .Append(state.Interactable).Append('\u0001')
            .Append(state.SelectedChoiceId ?? "").Append('\u0001');

        foreach (var choice in state.Choices)
        {
            builder.Append(choice.Id).Append('\u0002')
                .Append((int)choice.Kind).Append('\u0002')
                .Append(choice.Name).Append('\u0002')
                .Append(choice.TypeLabel).Append('\u0002')
                .Append(choice.DisplayName).Append('\u0002')
                .Append(choice.DetailText).Append('\u0002')
                .Append(choice.IsReadOnly).Append('\u0003');
        }

        return builder.ToString();
    }

    private static string BuildChoicesSignature(IReadOnlyList<SceneBarChoiceItem> choices)
    {
        var builder = new StringBuilder(256);
        foreach (var choice in choices)
        {
            builder.Append(choice.Id).Append('\u0002')
                .Append((int)choice.Kind).Append('\u0002')
                .Append(choice.Name).Append('\u0002')
                .Append(choice.TypeLabel).Append('\u0002')
                .Append(choice.DisplayName).Append('\u0002')
                .Append(choice.DetailText).Append('\u0002')
                .Append(choice.IsReadOnly).Append('\u0003');
        }

        return builder.ToString();
    }

    /// <summary>只有条目真的变化时才换 ItemsSource；仅仅选中项/已修改变化不会重建下拉。</summary>
    private void UpdateChoices(SceneBarViewState state)
    {
        var choices = state.Choices ?? Array.Empty<SceneBarChoiceItem>();
        var signature = BuildChoicesSignature(choices);
        if (string.Equals(signature, _choicesSignature, StringComparison.Ordinal)) return;

        _choicesSignature = signature;
        _choiceItems.Clear();
        _choiceItems.AddRange(choices);
        ChoiceCombo.ItemsSource = _choiceItems.ToList();
    }

    private void SelectChoice(string? id)
    {
        SceneBarChoiceItem? match = null;
        if (!string.IsNullOrEmpty(id))
        {
            match = _choiceItems.FirstOrDefault(c =>
                        string.Equals(c.Id, id, StringComparison.Ordinal))
                    ?? _choiceItems.FirstOrDefault(c =>
                        string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        ChoiceCombo.SelectedItem = match;
        _selectedChoiceId = match?.Id;
    }

    private bool IsSelectionInSync(string? id)
    {
        var expected = id ?? "";
        var actual = _selectedChoiceId ?? "";
        if (!string.Equals(expected, actual, StringComparison.Ordinal)) return false;

        return expected.Length == 0
            ? ChoiceCombo.SelectedIndex < 0
            : ChoiceCombo.SelectedItem is SceneBarChoiceItem item &&
              string.Equals(item.Id, expected, StringComparison.Ordinal);
    }

    private SceneBarChoiceKind? SelectedKind
    {
        get
        {
            if (string.IsNullOrEmpty(_selectedChoiceId)) return null;
            var item = _choiceItems.FirstOrDefault(c =>
                string.Equals(c.Id, _selectedChoiceId, StringComparison.Ordinal));
            return item?.Kind;
        }
    }

    private void UpdateDirtyBadge(SceneBarViewState state)
    {
        DirtyBadge.Visibility = state.IsDirty ? Visibility.Visible : Visibility.Collapsed;
        DirtyBadge.ToolTip = SelectedKind switch
        {
            SceneBarChoiceKind.BuiltInScene =>
                "内置场景只读：当前改动需要先复制为「我的配置」才能保存。",
            SceneBarChoiceKind.Profile =>
                "当前「我的配置」有未保存的修改，点「保存」保存到该配置。",
            _ => "当前方案有未保存的修改，点「另存为…」保存为「我的配置」。",
        };
    }

    private void ApplyEnabledState()
    {
        ChoiceCombo.IsEnabled = _interactable;
        SaveProfileButton.IsEnabled = _interactable;

        var isProfile = SelectedKind == SceneBarChoiceKind.Profile;
        UpdateProfileButton.IsEnabled = _interactable && isProfile;
        RenameProfileButton.IsEnabled = _interactable && isProfile;
        DeleteProfileButton.IsEnabled = _interactable && isProfile;
    }

    private void ChoiceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (ChoiceCombo.SelectedItem is not SceneBarChoiceItem item) return;
        if (string.IsNullOrEmpty(item.Id)) return;
        if (string.Equals(_selectedChoiceId, item.Id, StringComparison.Ordinal)) return;

        _selectedChoiceId = item.Id;
        ApplyEnabledState();

        // 先收起下拉再弹确认框，避免 MessageBox 打开在展开的 Popup 之上
        ChoiceCombo.IsDropDownOpen = false;
        ChoiceSelected?.Invoke(this, item.Id);
    }

    private void SaveProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        SaveProfileRequested?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        if (SelectedKind != SceneBarChoiceKind.Profile) return;
        UpdateProfileRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RenameProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        if (SelectedKind != SceneBarChoiceKind.Profile) return;
        RenameProfileRequested?.Invoke(this, EventArgs.Empty);
    }

    private void DeleteProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        if (SelectedKind != SceneBarChoiceKind.Profile) return;
        DeleteProfileRequested?.Invoke(this, EventArgs.Empty);
    }
}
