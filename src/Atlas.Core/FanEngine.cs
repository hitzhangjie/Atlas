namespace Atlas.Core;

/// <summary>
/// 风扇控制引擎：每个 tick 按每个通道的模式（BIOS 默认 / 固定 PWM / 温度曲线）计算目标值，
/// 做斜坡平滑后写入 PWM。
/// 安全性：锁定通道永不写入；温度源失效 5 秒后全速；退出/看门狗触发时恢复 BIOS 默认。
/// </summary>
public sealed class FanEngine : IDisposable
{
    private readonly object _gate = new();
    private readonly HardwareService _hardware;
    private readonly AtlasConfig _config;
    private readonly Dictionary<string, float> _lastApplied = new();
    private readonly Dictionary<string, DateTime> _sourceMissingSince = new();
    private bool _disposed;

    public string LastStatus { get; private set; } = "空闲";
    public DateTime LastTickUtc { get; private set; } = DateTime.UtcNow;
    public int ActiveChannels
    {
        get { lock (_gate) return _lastApplied.Count; }
    }

    public FanEngine(HardwareService hardware, AtlasConfig config)
    {
        _hardware = hardware;
        _config = config;
    }

    public void Tick()
    {
        lock (_gate)
        {
            if (_disposed || !_hardware.IsOpen) return;

            _hardware.Update();
            var values = _hardware.ReadAll();

            foreach (var fan in _hardware.Fans)
            {
                var cfg = _config.GetOrCreate(fan);

                // 锁定 / BIOS 默认：不写入；如果之前接管过则交还 BIOS
                if (cfg.Protected || cfg.Mode == FanMode.Disabled)
                {
                    RestoreIfApplied(fan);
                    continue;
                }

                float target;
                if (cfg.Mode == FanMode.Fixed)
                {
                    target = cfg.FixedPercent;
                }
                else
                {
                    if (!TryGetCurveTarget(fan, cfg, values, out target))
                        continue;
                }

                target = Math.Clamp(target, cfg.MinPercent, Math.Min(cfg.MaxPercent, fan.MaxPercent));

                if (_lastApplied.TryGetValue(fan.Id, out var last))
                {
                    const float maxStep = 8f; // 每秒最多变化 8%，避免转速突变
                    target = Math.Clamp(target, last - maxStep, last + maxStep);
                    if (Math.Abs(target - last) < 1f) continue;
                }

                try
                {
                    _hardware.SetPercent(fan, target);
                    _lastApplied[fan.Id] = target;
                    LastStatus = $"{fan.DisplayName} → {target:0}%";
                }
                catch (Exception ex)
                {
                    LastStatus = $"{fan.DisplayName} 控制失败: {ex.Message}";
                }
            }

            LastTickUtc = DateTime.UtcNow;
        }
    }

    private bool TryGetCurveTarget(FanChannel fan, FanCurveConfig cfg, Dictionary<string, float?> values, out float target)
    {
        target = 0;

        if (values.TryGetValue(cfg.SourceId, out var value) && value.HasValue)
        {
            _sourceMissingSince.Remove(fan.Id);
            target = Interpolate(cfg.Curve, value.Value);
            return true;
        }

        if (!_sourceMissingSince.TryGetValue(fan.Id, out var since))
        {
            _sourceMissingSince[fan.Id] = DateTime.UtcNow;
            return false;
        }

        if ((DateTime.UtcNow - since).TotalSeconds < 5) return false;

        target = fan.MaxPercent; // 温度源失效 → 全速兜底
        return true;
    }

    private void RestoreIfApplied(FanChannel fan)
    {
        if (!_lastApplied.Remove(fan.Id)) return;
        _hardware.RestoreDefault(fan);
    }

    public static float Interpolate(List<CurvePoint> curve, float temp)
    {
        if (curve.Count == 0) return 50f;
        var points = curve.OrderBy(p => p.Temp).ToList();
        if (temp <= points[0].Temp) return points[0].Percent;
        if (temp >= points[^1].Temp) return points[^1].Percent;

        for (var i = 1; i < points.Count; i++)
        {
            if (temp > points[i].Temp) continue;
            var a = points[i - 1];
            var b = points[i];
            var span = b.Temp - a.Temp;
            if (span <= 0.001f) return b.Percent;
            var ratio = (temp - a.Temp) / span;
            return a.Percent + (b.Percent - a.Percent) * ratio;
        }
        return points[^1].Percent;
    }

    public void RestoreAll()
    {
        lock (_gate)
        {
            _hardware.RestoreAll();
            _lastApplied.Clear();
            _sourceMissingSince.Clear();
            LastStatus = "已交由 BIOS 控制";
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _hardware.RestoreAll();
            _lastApplied.Clear();
        }
    }
}
