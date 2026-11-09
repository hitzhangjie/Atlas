using Atlas.Core;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Atlas.App;

public sealed record FanModeOption(FanMode Value, string Display);

/// <summary>DataGrid 的一行：一个风扇通道 + 它的控制配置。</summary>
public sealed class FanRow : INotifyPropertyChanged
{
    private static readonly IReadOnlyList<FanModeOption> ModeOptionsList = new[]
    {
        new FanModeOption(FanMode.Disabled, "BIOS 默认"),
        new FanModeOption(FanMode.Fixed, "固定 PWM"),
        new FanModeOption(FanMode.Curve, "温度曲线"),
    };

    private readonly FanChannel _fan;
    private readonly FanCurveConfig _config;
    private FanModeOption _selectedMode;
    private TemperatureSource? _selectedSource;

    /// <summary>
    /// 热设置编辑闸门：返回 false 表示本次修改被拒绝（例如内置场景只读）。
    /// 供 MainWindow 挂接「请先复制为我的配置」的拦截逻辑，也覆盖键盘/滚轮等
    /// 绕过鼠标 Preview 拦截的修改路径。备注名、锁定不经过这里。
    /// </summary>
    public Func<bool>? EditGuard { get; set; }

    private bool CanEdit() => EditGuard is null || EditGuard();

    public FanRow(FanChannel fan, FanCurveConfig config, IReadOnlyList<TemperatureSource> sources)
    {
        _fan = fan;
        _config = config;
        Sources = sources;
        _selectedMode = ModeOptionsList.FirstOrDefault(o => o.Value == config.Mode) ?? ModeOptionsList[0];
        _selectedSource = sources.FirstOrDefault(s => s.Id == config.SourceId);
    }

    /// <summary>表格里显示的通道名；备注名已从 UI 移除，仅保留硬件通道名。</summary>
    public string Name => _fan.DisplayName;

    /// <summary>历史备注名（v0.2 配置兼容）；UI 不再展示/编辑，但保留字段避免丢旧配置。</summary>
    public string CustomName
    {
        get => _config.CustomName;
        set
        {
            var trimmed = (value ?? "").Trim();
            if (_config.CustomName == trimmed) return;
            _config.CustomName = trimmed;
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<TemperatureSource> Sources { get; }
    public IReadOnlyList<FanModeOption> Modes => ModeOptionsList;

    /// <summary>通道来源（主板 / 显卡），用于表格里的副标题。</summary>
    public string HardwareLabel => _fan.IsGpu ? "显卡 · GPU" : "主板 · Super I/O";

    public string RpmText => _fan.Rpm.HasValue ? $"{_fan.Rpm.Value:0} RPM" : "--";
    public string PercentText => _fan.Percent.HasValue ? $"{_fan.Percent.Value:0}%" : "--";

    /// <summary>0–100 的 PWM 数值，供表格里的迷你进度条使用。</summary>
    public double PwmValue => _fan.Percent.HasValue ? Math.Clamp(_fan.Percent.Value, 0, 100) : 0;

    /// <summary>当前曲线点集合，供表格里的迷你曲线预览使用。</summary>
    public IReadOnlyList<CurvePoint> CurvePoints => _config.Curve;

    /// <summary>转速为 0 或读不到转速（用于「隐藏 0 RPM」过滤）。</summary>
    public bool IsZeroRpm => !_fan.Rpm.HasValue || _fan.Rpm.Value < 1f;

    public FanModeOption SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (value == null || _selectedMode == value) return;
            if (!CanEdit())
            {
                // 拒绝本次修改：通知绑定把下拉/单元格恢复为旧值
                OnPropertyChanged(nameof(SelectedMode));
                return;
            }
            _selectedMode = value;
            _config.Mode = value.Value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsFixedMode));
            OnPropertyChanged(nameof(IsCurveMode));
            OnPropertyChanged(nameof(CurveOriginText));
            OnPropertyChanged(nameof(IsCurveFromTemplate));
        }
    }

    public TemperatureSource? SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (Equals(_selectedSource, value)) return;
            if (!CanEdit())
            {
                OnPropertyChanged(nameof(SelectedSource));
                return;
            }
            _selectedSource = value;
            _config.SourceId = value?.Id ?? "";
            OnPropertyChanged();
        }
    }

    public float FixedPercent
    {
        get => _config.FixedPercent;
        set
        {
            if (Math.Abs(_config.FixedPercent - value) < 0.01f) return;
            if (!CanEdit())
            {
                OnPropertyChanged(nameof(FixedPercent));
                OnPropertyChanged(nameof(FixedPercentText));
                return;
            }
            _config.FixedPercent = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FixedPercentText));
        }
    }

    public string FixedPercentText => $"{_config.FixedPercent:0}%";
    public bool IsFixedMode => _config.Mode == FanMode.Fixed;
    public bool IsCurveMode => _config.Mode == FanMode.Curve;

    public bool Protected
    {
        get => _config.Protected;
        set
        {
            if (_config.Protected == value) return;
            _config.Protected = value;
            OnPropertyChanged();
        }
    }

    public string CurveText => string.Join(" · ",
        _config.Curve.OrderBy(p => p.Temp).Select(p => $"{p.Temp:0}°→{p.Percent:0}%"));

    /// <summary>
    /// 曲线来源说明：与某个内置模板完全一致时显示「模板 · 静音」等，
    /// 否则显示「自定义曲线」；非曲线模式不显示（空串）。
    /// </summary>
    public string CurveOriginText
    {
        get
        {
            if (_config.Mode != FanMode.Curve) return "";
            var templateId = CurveTemplates.Match(_config.Curve);
            return templateId is null
                ? "自定义曲线"
                : "模板 · " + CurveTemplates.Get(templateId).Name;
        }
    }

    /// <summary>曲线列第二行小字的颜色：模板 = 青色，自定义 = 琥珀色。</summary>
    public bool IsCurveFromTemplate =>
        _config.Mode == FanMode.Curve && CurveTemplates.Match(_config.Curve) is not null;

    public List<CurvePoint> GetCurve() =>
        _config.Curve.Select(p => new CurvePoint { Temp = p.Temp, Percent = p.Percent }).ToList();

    public void SetCurve(IEnumerable<CurvePoint> points)
    {
        if (!CanEdit()) return;
        _config.Curve = points.Select(p => new CurvePoint { Temp = p.Temp, Percent = p.Percent }).ToList();
        OnPropertyChanged(nameof(CurveText));
        OnPropertyChanged(nameof(CurvePoints));
        OnPropertyChanged(nameof(CurveOriginText));
        OnPropertyChanged(nameof(IsCurveFromTemplate));
    }

    /// <summary>
    /// 应用内置场景 / 「我的配置」后，_config 已被 FanProfileService 直接改写；
    /// 这里把行内缓存重新对齐，并通知所有派生属性，避免界面显示旧值。
    /// </summary>
    public void ReloadFromConfig()
    {
        _selectedMode = ModeOptionsList.FirstOrDefault(o => o.Value == _config.Mode) ?? ModeOptionsList[0];
        _selectedSource = Sources.FirstOrDefault(s => s.Id == _config.SourceId);

        OnPropertyChanged(nameof(SelectedMode));
        OnPropertyChanged(nameof(SelectedSource));
        OnPropertyChanged(nameof(FixedPercent));
        OnPropertyChanged(nameof(FixedPercentText));
        OnPropertyChanged(nameof(CurveText));
        OnPropertyChanged(nameof(CurvePoints));
        OnPropertyChanged(nameof(CurveOriginText));
        OnPropertyChanged(nameof(IsCurveFromTemplate));
        OnPropertyChanged(nameof(IsFixedMode));
        OnPropertyChanged(nameof(IsCurveMode));
        OnPropertyChanged(nameof(PwmValue));
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(RpmText));
        OnPropertyChanged(nameof(PercentText));
        OnPropertyChanged(nameof(PwmValue));
        OnPropertyChanged(nameof(IsZeroRpm));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
