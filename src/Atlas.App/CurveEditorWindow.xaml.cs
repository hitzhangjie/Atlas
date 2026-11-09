using Atlas.Core;
using System.Windows;

namespace Atlas.App;

public partial class CurveEditorWindow : Window
{
    public List<CurvePoint> ResultCurve { get; private set; } = new();

    public CurveEditorWindow(string fanName, IEnumerable<CurvePoint> curve)
    {
        InitializeComponent();
        WindowChromeHelper.Enable(this);
        Title = $"编辑曲线 - {fanName}";
        TitleBarControl.Subtitle = fanName;
        Editor.PointsChanged += (_, _) => UpdateSummary();
        Editor.SetPoints(curve);
        UpdateSummary();
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

    private void TemplateQuiet_Click(object sender, RoutedEventArgs e) =>
        Editor.SetPoints(CurveTemplates.CloneCurve(CurveTemplates.QuietId));

    private void TemplateBalanced_Click(object sender, RoutedEventArgs e) =>
        Editor.SetPoints(CurveTemplates.CloneCurve(CurveTemplates.BalancedId));

    private void TemplatePerformance_Click(object sender, RoutedEventArgs e) =>
        Editor.SetPoints(CurveTemplates.CloneCurve(CurveTemplates.PerformanceId));

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
