using Atlas.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace Atlas.App;

/// <summary>
/// 表格里的小型曲线预览：把 20–100°C / 0–100% 的风扇曲线画成一条迷你折线，带渐变填充。
/// </summary>
public sealed class CurveSparkline : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points),
        typeof(IReadOnlyList<CurvePoint>),
        typeof(CurveSparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush StrokeBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x5F, 0xB3, 0xFF)));
    private static readonly Brush DotBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x46, 0xD6, 0xE6)));
    private static readonly Pen LinePen = FreezePen(new Pen(StrokeBrush, 1.6));
    private static readonly Pen DotPen = FreezePen(new Pen(Brushes.White, 1.2));

    public IReadOnlyList<CurvePoint>? Points
    {
        get => (IReadOnlyList<CurvePoint>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var points = Points?.Where(p => p is not null).OrderBy(p => p.Temp).ToList();
        var width = ActualWidth;
        var height = ActualHeight;
        if (points is null || points.Count < 2 || width <= 2 || height <= 2) return;

        const double padY = 3;
        double X(float temp) => (Math.Clamp(temp, 20f, 100f) - 20f) / 80f * width;
        double Y(float percent) => height - padY - Math.Clamp(percent, 0f, 100f) / 100f * (height - padY * 2);

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(new Point(X(points[0].Temp), Y(points[0].Percent)), false, false);
            foreach (var point in points.Skip(1))
                ctx.LineTo(new Point(X(point.Temp), Y(point.Percent)), true, false);
        }
        line.Freeze();

        var area = new StreamGeometry();
        using (var ctx = area.Open())
        {
            ctx.BeginFigure(new Point(X(points[0].Temp), height), true, true);
            ctx.LineTo(new Point(X(points[0].Temp), Y(points[0].Percent)), true, false);
            foreach (var point in points.Skip(1))
                ctx.LineTo(new Point(X(point.Temp), Y(point.Percent)), true, false);
            ctx.LineTo(new Point(X(points[^1].Temp), height), true, false);
        }
        area.Freeze();

        var fill = new LinearGradientBrush(
            Color.FromArgb(0x46, 0x4D, 0x9E, 0xFF),
            Color.FromArgb(0x00, 0x4D, 0x9E, 0xFF),
            90);
        fill.Freeze();

        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, LinePen, line);

        var last = points[^1];
        dc.DrawEllipse(DotBrush, DotPen, new Point(X(last.Temp), Y(last.Percent)), 2.6, 2.6);
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    private static Pen FreezePen(Pen pen)
    {
        pen.Freeze();
        return pen;
    }
}
