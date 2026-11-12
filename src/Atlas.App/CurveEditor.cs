using Atlas.Core;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Atlas.App;

/// <summary>
/// 图形化风扇曲线编辑器：拖动圆点调整 (温度, PWM)，双击空白添加点，右键删除点。
/// 温度轴固定 20–100°C，PWM 轴 0–100%。
/// </summary>
public sealed class CurveEditor : FrameworkElement
{
    public const float MinTemp = 20f;
    public const float MaxTemp = 100f;
    public const float MinPwm = 0f;
    public const float MaxPwm = 100f;

    private const double PlotLeft = 44;
    private const double PlotTop = 30;
    private const double PlotRight = 18;
    private const double PlotBottom = 38;
    private const double HitRadius = 12;

    private static readonly Brush TextBrush = Frozen(Color.FromRgb(0x7C, 0x8B, 0xA3));
    private static readonly Brush TextHiBrush = Frozen(Color.FromRgb(0x9F, 0xB4, 0xCF));
    private static readonly Brush AccentBrush = Frozen(Color.FromRgb(0x4D, 0x9E, 0xFF));
    private static readonly Brush CyanBrush = Frozen(Color.FromRgb(0x46, 0xD6, 0xE6));
    private static readonly Brush WhiteBrush = Brushes.White;
    private static readonly Brush TooltipBg = Frozen(Color.FromArgb(0xEE, 0x11, 0x1A, 0x27));

    private static readonly Pen GridPen = FrozenPen(new Pen(Frozen(Color.FromRgb(0x18, 0x24, 0x35)), 1)
    {
        DashStyle = new DashStyle(new double[] { 2, 4 }, 0),
    });
    private static readonly Pen AxisPen = FrozenPen(new Pen(Frozen(Color.FromRgb(0x2A, 0x3A, 0x52)), 1.2));
    private static readonly Pen CurveGlowPen = FrozenPen(new Pen(Frozen(Color.FromArgb(0x2A, 0x5F, 0xB3, 0xFF)), 7));
    private static readonly Pen CurvePen = FrozenPen(new Pen(Frozen(Color.FromRgb(0x5F, 0xB3, 0xFF)), 2.1)
    {
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round,
        LineJoin = PenLineJoin.Round,
    });
    private static readonly Pen PointPen = FrozenPen(new Pen(Brushes.White, 1.6));
    private static readonly Pen HoverPen = FrozenPen(new Pen(Frozen(Color.FromArgb(0x66, 0x46, 0xD6, 0xE6)), 4));

    /// <summary>曲线两端（无控制点区间）的虚线延长线：引擎在那里沿用最近控制点的值。</summary>
    private static readonly Pen HoldPen = FrozenPen(new Pen(Frozen(Color.FromArgb(0xB0, 0x5F, 0xB3, 0xFF)), 1.6)
    {
        DashStyle = new DashStyle(new double[] { 4, 4 }, 0),
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round,
    });

    /// <summary>「保持」区域的淡填充，和用户定义区间做视觉区分。</summary>
    private static readonly Brush HoldFillBrush = Frozen(Color.FromArgb(0x14, 0x4D, 0x9E, 0xFF));

    private readonly List<CurvePoint> _points = new();
    private int _dragIndex = -1;
    private int _hoverIndex = -1;

    public IReadOnlyList<CurvePoint> Points => _points;

    public event EventHandler? PointsChanged;

    /// <summary>一次完整用户编辑结束（拖拽松开 / 双击加点 / 右键删点），供撤销历史按「操作」记一步。</summary>
    public event EventHandler? EditCompleted;

    public CurveEditor()
    {
        MinHeight = 260;
        MinWidth = 320;
        Focusable = true;
        Cursor = Cursors.Cross;
    }

    public void SetPoints(IEnumerable<CurvePoint> points)
    {
        _points.Clear();
        _points.AddRange(points
            .Select(p => new CurvePoint
            {
                Temp = Math.Clamp(p.Temp, MinTemp, MaxTemp),
                Percent = Math.Clamp(p.Percent, MinPwm, MaxPwm),
            })
            .OrderBy(p => p.Temp));
        _dragIndex = -1;
        _hoverIndex = -1;
        InvalidateVisual();
        PointsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ResetDefault() => SetPoints(FanCurveConfig.DefaultCurve());

    private Rect PlotRect => new(
        PlotLeft,
        PlotTop,
        Math.Max(1, RenderSize.Width - PlotLeft - PlotRight),
        Math.Max(1, RenderSize.Height - PlotTop - PlotBottom));

    private Point ToScreen(float temp, float pwm)
    {
        var plot = PlotRect;
        var x = plot.Left + (temp - MinTemp) / (MaxTemp - MinTemp) * plot.Width;
        var y = plot.Bottom - (pwm - MinPwm) / (MaxPwm - MinPwm) * plot.Height;
        return new Point(x, y);
    }

    private (float Temp, float Pwm) FromScreen(Point point)
    {
        var plot = PlotRect;
        var tx = (float)Math.Clamp((point.X - plot.Left) / plot.Width, 0, 1);
        var ty = (float)Math.Clamp((plot.Bottom - point.Y) / plot.Height, 0, 1);
        return (MinTemp + tx * (MaxTemp - MinTemp), MinPwm + ty * (MaxPwm - MinPwm));
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var size = RenderSize;
        if (size.Width < 80 || size.Height < 60) return;

        var background = new LinearGradientBrush(
            Color.FromRgb(0x11, 0x19, 0x26),
            Color.FromRgb(0x0B, 0x11, 0x1A),
            90);
        background.Freeze();
        dc.DrawRoundedRectangle(background, null, new Rect(0, 0, size.Width, size.Height), 8, 8);

        var plot = PlotRect;
        DrawGridAndLabels(dc, plot);

        if (_points.Count == 0) return;

        var first = ToScreen(_points[0].Temp, _points[0].Percent);
        var last = ToScreen(_points[^1].Temp, _points[^1].Percent);
        var leftEdge = ToScreen(MinTemp, _points[0].Percent);
        var rightEdge = ToScreen(MaxTemp, _points[^1].Percent);

        // 曲线两端没有控制点的区间：引擎按最近控制点的值保持。
        // 这里补上淡填充 + 虚线延长线 + 说明，避免看起来「20–30 °C / 80–100 °C 不受控制」。
        DrawHoldRegion(dc, plot, leftEdge, first, _points[0], isLower: true);
        DrawHoldRegion(dc, plot, last, rightEdge, _points[^1], isLower: false);

        // 控制点之间的曲线下方渐变填充
        var area = new StreamGeometry();
        using (var ctx = area.Open())
        {
            ctx.BeginFigure(new Point(first.X, plot.Bottom), true, true);
            ctx.LineTo(first, true, false);
            foreach (var point in _points.Skip(1))
                ctx.LineTo(ToScreen(point.Temp, point.Percent), true, false);
            ctx.LineTo(new Point(last.X, plot.Bottom), true, false);
        }
        area.Freeze();

        var fill = new LinearGradientBrush(
            Color.FromArgb(0x40, 0x4D, 0x9E, 0xFF),
            Color.FromArgb(0x00, 0x4D, 0x9E, 0xFF),
            90);
        fill.Freeze();
        dc.DrawGeometry(fill, null, area);

        if (_points.Count > 1)
        {
            var line = new StreamGeometry();
            using (var ctx = line.Open())
            {
                ctx.BeginFigure(first, false, false);
                foreach (var point in _points.Skip(1))
                    ctx.LineTo(ToScreen(point.Temp, point.Percent), true, false);
            }
            line.Freeze();
            // 先画一层柔光再画主线
            dc.DrawGeometry(null, CurveGlowPen, line);
            dc.DrawGeometry(null, CurvePen, line);
        }

        for (var i = 0; i < _points.Count; i++)
        {
            var center = ToScreen(_points[i].Temp, _points[i].Percent);
            var active = i == _dragIndex || i == _hoverIndex;
            if (active)
            {
                dc.DrawEllipse(null, HoverPen, center, 9, 9);
            }
            var radius = active ? 7.0 : 5.4;
            dc.DrawEllipse(AccentBrush, PointPen, center, radius, radius);
            if (active)
            {
                dc.DrawEllipse(CyanBrush, null, center, 2.2, 2.2);
            }
        }

        if (_dragIndex >= 0 && _dragIndex < _points.Count)
        {
            DrawTooltip(dc, _points[_dragIndex], ToScreen(_points[_dragIndex].Temp, _points[_dragIndex].Percent));
        }
        else if (_hoverIndex >= 0 && _hoverIndex < _points.Count)
        {
            DrawTooltip(dc, _points[_hoverIndex], ToScreen(_points[_hoverIndex].Temp, _points[_hoverIndex].Percent));
        }
    }

    /// <summary>
    /// 画曲线一端「保持」区域：控制点之外的温度按最近控制点的值输出，
    /// 用淡填充 + 虚线延长线 + 文字说明表示这不是用户定义的控制区间。
    /// </summary>
    private void DrawHoldRegion(DrawingContext dc, Rect plot, Point edge, Point point,
        CurvePoint endpoint, bool isLower)
    {
        if (Math.Abs(edge.X - point.X) < 1) return;

        var leftX = Math.Min(edge.X, point.X);
        var rightX = Math.Max(edge.X, point.X);
        var y = point.Y;

        var region = new StreamGeometry();
        using (var ctx = region.Open())
        {
            ctx.BeginFigure(new Point(leftX, plot.Bottom), true, true);
            ctx.LineTo(new Point(leftX, y), true, false);
            ctx.LineTo(new Point(rightX, y), true, false);
            ctx.LineTo(new Point(rightX, plot.Bottom), true, false);
        }
        region.Freeze();
        dc.DrawGeometry(HoldFillBrush, null, region);
        dc.DrawLine(HoldPen, new Point(leftX, y), new Point(rightX, y));

        // 区间太窄时只画虚线，避免文字和圆点挤在一起
        var label = MakeText(
            $"{(isLower ? "≤" : "≥")}{endpoint.Temp:0}° 保持{endpoint.Percent:0}%",
            10.0,
            TextHiBrush);
        if (rightX - leftX < label.Width + 4) return;

        var textX = (leftX + rightX) / 2 - label.Width / 2;
        var aboveY = y - label.Height - 6;
        var textY = aboveY >= plot.Top + 2 ? aboveY : y + 6;
        dc.DrawText(label, new Point(textX, textY));
    }

    private void DrawTooltip(DrawingContext dc, CurvePoint point, Point anchor)
    {
        var text = MakeText($"{point.Temp:0}°C → {point.Percent:0}%", 11.5, WhiteBrush);
        var padding = new Thickness(8, 4, 8, 4);
        var width = text.Width + padding.Left + padding.Right;
        var height = text.Height + padding.Top + padding.Bottom;
        var x = Math.Min(anchor.X + 12, RenderSize.Width - width - 4);
        var y = Math.Max(4, anchor.Y - height - 12);
        var rect = new Rect(x, y, width, height);
        dc.DrawRoundedRectangle(TooltipBg, FrozenPen(new Pen(Frozen(Color.FromRgb(0x2A, 0x3A, 0x52)), 1)), rect, 6, 6);
        dc.DrawText(text, new Point(x + padding.Left, y + padding.Top));
    }

    private void DrawGridAndLabels(DrawingContext dc, Rect plot)
    {
        // 水平网格线 / PWM 标签（每 20%）
        for (var pwm = 0; pwm <= 100; pwm += 20)
        {
            var y = plot.Bottom - pwm / 100.0 * plot.Height;
            dc.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = MakeText($"{pwm}%", 10.5, TextBrush);
            dc.DrawText(label, new Point(plot.Left - label.Width - 8, y - label.Height / 2));
        }

        // 垂直网格线 / 温度标签（每 20°C）
        for (var temp = (int)MinTemp; temp <= (int)MaxTemp; temp += 20)
        {
            var x = plot.Left + (temp - MinTemp) / (MaxTemp - MinTemp) * plot.Width;
            dc.DrawLine(GridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            var label = MakeText($"{temp}°", 10.5, TextBrush);
            dc.DrawText(label, new Point(x - label.Width / 2, plot.Bottom + 7));
        }

        dc.DrawLine(AxisPen, new Point(plot.Left, plot.Top), new Point(plot.Left, plot.Bottom));
        dc.DrawLine(AxisPen, new Point(plot.Left, plot.Bottom), new Point(plot.Right, plot.Bottom));

        var unit = MakeText("PWM %", 9.5, TextHiBrush);
        dc.DrawText(unit, new Point(4, 6));
        var tempUnit = MakeText("温度 °C", 9.5, TextHiBrush);
        dc.DrawText(tempUnit, new Point(plot.Right - tempUnit.Width, plot.Bottom + 21));
    }

    private FormattedText MakeText(string text, double size, Brush brush) => new(
        text,
        CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight,
        new Typeface("Segoe UI"),
        size,
        brush,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private int HitTestPoint(Point position)
    {
        for (var i = 0; i < _points.Count; i++)
        {
            var center = ToScreen(_points[i].Temp, _points[i].Percent);
            if ((center - position).Length <= HitRadius) return i;
        }
        return -1;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var position = e.GetPosition(this);
        var index = HitTestPoint(position);

        if (index >= 0)
        {
            _dragIndex = index;
            CaptureMouse();
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (e.ClickCount >= 2)
        {
            var (temp, pwm) = FromScreen(position);
            var insertIndex = _points.FindIndex(p => p.Temp > temp);
            if (insertIndex < 0) insertIndex = _points.Count;
            _points.Insert(insertIndex, new CurvePoint { Temp = temp, Percent = pwm });
            _dragIndex = insertIndex;
            CaptureMouse();
            InvalidateVisual();
            PointsChanged?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var position = e.GetPosition(this);

        if (_dragIndex >= 0)
        {
            var (temp, pwm) = FromScreen(position);

            // 保持温度递增：不能越过相邻点
            var lower = _dragIndex == 0 ? MinTemp : _points[_dragIndex - 1].Temp + 1f;
            var upper = _dragIndex == _points.Count - 1 ? MaxTemp : _points[_dragIndex + 1].Temp - 1f;
            if (lower > upper)
            {
                var middle = (lower + upper) / 2f;
                lower = upper = middle;
            }

            _points[_dragIndex].Temp = Math.Clamp(temp, lower, upper);
            _points[_dragIndex].Percent = Math.Clamp(pwm, MinPwm, MaxPwm);
            InvalidateVisual();
            PointsChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var hover = HitTestPoint(position);
        if (hover != _hoverIndex)
        {
            _hoverIndex = hover;
            Cursor = hover >= 0 ? Cursors.SizeAll : Cursors.Cross;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragIndex >= 0)
        {
            _dragIndex = -1;
            ReleaseMouseCapture();
            InvalidateVisual();
            PointsChanged?.Invoke(this, EventArgs.Empty);
            EditCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        var index = HitTestPoint(e.GetPosition(this));
        if (index < 0 || _points.Count <= 2) return;

        _points.RemoveAt(index);
        _dragIndex = -1;
        _hoverIndex = -1;
        InvalidateVisual();
        PointsChanged?.Invoke(this, EventArgs.Empty);
        EditCompleted?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverIndex == -1) return;
        _hoverIndex = -1;
        Cursor = Cursors.Cross;
        InvalidateVisual();
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Pen pen)
    {
        pen.Freeze();
        return pen;
    }
}
