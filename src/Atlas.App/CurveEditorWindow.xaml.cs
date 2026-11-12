using Atlas.Core;
using System.Windows;
using System.Windows.Input;

namespace Atlas.App;

public partial class CurveEditorWindow : Window
{
    /// <summary>
    /// 曲线编辑窗口自己的撤销/重做历史：只覆盖本次「打开窗口 → 确定/取消」之间的编辑。
    /// 点击确定后这份历史随窗口一起丢弃；主界面把这次曲线修改整体记为一步。
    /// </summary>
    private const int MaxHistorySteps = 1024;

    private readonly List<List<CurvePoint>> _history = new();
    private int _cursor = -1;

    public List<CurvePoint> ResultCurve { get; private set; } = new();

    public CurveEditorWindow(string fanName, IEnumerable<CurvePoint> curve)
    {
        InitializeComponent();
        WindowChromeHelper.Enable(this);
        Title = $"编辑曲线 - {fanName}";
        TitleBarControl.Subtitle = fanName;
        Editor.PointsChanged += (_, _) => UpdateSummary();
        // 一次完整用户编辑（拖拽松开 / 加点 / 删点）结束后才记一步，拖拽过程不产生中间步。
        Editor.EditCompleted += (_, _) => RecordHistory();
        Editor.SetPoints(curve);
        UpdateSummary();
        ResetHistory();
    }

    private void UpdateSummary()
    {
        var pointsText = string.Join(" · ",
            Editor.Points.OrderBy(p => p.Temp).Select(p => $"{p.Temp:0}°→{p.Percent:0}%"));

        var templateId = CurveTemplates.Match(Editor.Points);
        var origin = templateId is null
            ? "当前曲线：自定义（未套用模板）"
            : $"当前曲线：与「{CurveTemplates.Get(templateId).Name}」模板一致";

        SummaryText.Text = $"{origin} · {pointsText}";
    }

    // ==================================================================
    //  本次编辑会话的撤销 / 重做
    // ==================================================================

    private static List<CurvePoint> CloneSorted(IEnumerable<CurvePoint> points) => points
        .OrderBy(p => p.Temp)
        .Select(p => new CurvePoint { Temp = p.Temp, Percent = p.Percent })
        .ToList();

    private static bool SameCurve(IReadOnlyList<CurvePoint> a, IReadOnlyList<CurvePoint> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (Math.Abs(a[i].Temp - b[i].Temp) > 0.001f) return false;
            if (Math.Abs(a[i].Percent - b[i].Percent) > 0.001f) return false;
        }
        return true;
    }

    private void ResetHistory()
    {
        _history.Clear();
        _history.Add(CloneSorted(Editor.Points));
        _cursor = 0;
        UpdateHistoryUi();
    }

    /// <summary>把当前曲线提交为一步；与当前快照一致时不产生空步。</summary>
    private void RecordHistory()
    {
        if (_cursor < 0)
        {
            ResetHistory();
            return;
        }

        var snapshot = CloneSorted(Editor.Points);
        if (SameCurve(_history[_cursor], snapshot)) return;

        if (_cursor < _history.Count - 1)
            _history.RemoveRange(_cursor + 1, _history.Count - 1 - _cursor);

        _history.Add(snapshot);
        _cursor = _history.Count - 1;

        while (_history.Count > MaxHistorySteps)
        {
            _history.RemoveAt(0);
            _cursor--;
        }

        UpdateHistoryUi();
    }

    private bool CanUndo => _cursor > 0;
    private bool CanRedo => _cursor >= 0 && _cursor < _history.Count - 1;

    private void UndoHistory()
    {
        if (!CanUndo) return;
        _cursor--;
        Editor.SetPoints(_history[_cursor]);
        UpdateHistoryUi();
    }

    private void RedoHistory()
    {
        if (!CanRedo) return;
        _cursor++;
        Editor.SetPoints(_history[_cursor]);
        UpdateHistoryUi();
    }

    private void UpdateHistoryUi() => CommandManager.InvalidateRequerySuggested();

    private void Undo_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = CanUndo;
        e.Handled = true;
    }

    private void Redo_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = CanRedo;
        e.Handled = true;
    }

    private void Undo_Executed(object sender, ExecutedRoutedEventArgs e) => UndoHistory();

    private void Redo_Executed(object sender, ExecutedRoutedEventArgs e) => RedoHistory();

    // ==================================================================
    //  模板 / 确定 / 取消
    // ==================================================================

    private void ApplyTemplate(string templateId)
    {
        Editor.SetPoints(CurveTemplates.CloneCurve(templateId));
        RecordHistory();
    }

    private void TemplateQuiet_Click(object sender, RoutedEventArgs e) =>
        ApplyTemplate(CurveTemplates.QuietId);

    private void TemplateBalanced_Click(object sender, RoutedEventArgs e) =>
        ApplyTemplate(CurveTemplates.BalancedId);

    private void TemplatePerformance_Click(object sender, RoutedEventArgs e) =>
        ApplyTemplate(CurveTemplates.PerformanceId);

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var points = Editor.Points.OrderBy(p => p.Temp).ToList();
        if (points.Count < 2)
        {
            MessageBox.Show("至少需要两个控制点。", "Atlas", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ResultCurve = points.Select(p => new CurvePoint { Temp = p.Temp, Percent = p.Percent }).ToList();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
