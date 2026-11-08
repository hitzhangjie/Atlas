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

    public FanRow(FanChannel fan, FanCurveConfig config, IReadOnlyList<TemperatureSource> sources)
    {
        _fan = fan;
        _config = config;
        Sources = sources;
        _selectedMode = ModeOptionsList.FirstOrDefault(o => o.Value == config.Mode) ?? ModeOptionsList[0];
        _selectedSource = sources.FirstOrDefault(s => s.Id == config.SourceId);
    }

    public string Name => string.IsNullOrWhiteSpace(_config.CustomName)
        ? _fan.DisplayName
        : $"{_fan.DisplayName} ({_config.CustomName})";

    /// <summary>括号里的备注名；编号（Fan #1 等）始终保留。</summary>
    public string CustomName
    {
        get => _config.CustomName;
        set
        {
            var trimmed = (value ?? "").Trim();
            if (_config.CustomName == trimmed) return;
            _config.CustomName = trimmed;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Name));
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
            _selectedMode = value;
            _config.Mode = value.Value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsFixedMode));
            OnPropertyChanged(nameof(IsCurveMode));
        }
    }

    public TemperatureSource? SelectedSource
    {
        get => _selectedSource;
        set
        {
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

    public List<CurvePoint> GetCurve() =>
        _config.Curve.Select(p => new CurvePoint { Temp = p.Temp, Percent = p.Percent }).ToList();

    public void SetCurve(IEnumerable<CurvePoint> points)
    {
        _config.Curve = points.Select(p => new CurvePoint { Temp = p.Temp, Percent = p.Percent }).ToList();
        OnPropertyChanged(nameof(CurveText));
        OnPropertyChanged(nameof(CurvePoints));
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
