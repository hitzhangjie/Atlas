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
        SummaryText.Text = "当前曲线：" + string.Join(" · ",
            Editor.Points.OrderBy(p => p.Temp).Select(p => $"{p.Temp:0}°→{p.Percent:0}%"));
    }

    private void PresetQuiet_Click(object sender, RoutedEventArgs e) => Editor.SetPoints(new[]
    {
        new CurvePoint { Temp = 30, Percent = 20 },
        new CurvePoint { Temp = 45, Percent = 25 },
        new CurvePoint { Temp = 60, Percent = 30 },
        new CurvePoint { Temp = 70, Percent = 40 },
        new CurvePoint { Temp = 80, Percent = 55 },
        new CurvePoint { Temp = 88, Percent = 75 },
        new CurvePoint { Temp = 95, Percent = 100 },
    });

    private void PresetBalanced_Click(object sender, RoutedEventArgs e) => Editor.ResetDefault();

    private void PresetPerformance_Click(object sender, RoutedEventArgs e) => Editor.SetPoints(new[]
    {
        new CurvePoint { Temp = 30, Percent = 40 },
        new CurvePoint { Temp = 45, Percent = 55 },
        new CurvePoint { Temp = 60, Percent = 75 },
        new CurvePoint { Temp = 70, Percent = 90 },
        new CurvePoint { Temp = 80, Percent = 100 },
    });

    private void Reset_Click(object sender, RoutedEventArgs e) => Editor.ResetDefault();

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
