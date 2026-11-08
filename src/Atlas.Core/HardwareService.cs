using LibreHardwareMonitor.Hardware;

namespace Atlas.Core;

/// <summary>一个可控制的风扇通道（主板 SuperIO 或 GPU）。</summary>
public sealed class FanChannel
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string HardwareName { get; init; } = "";
    public bool IsGpu { get; init; }

    internal IHardware Hardware { get; init; } = null!;
    internal ISensor ControlSensor { get; init; } = null!;
    internal ISensor? RpmSensor { get; init; }

    public float? Rpm => RpmSensor?.Value;
    public float? Percent => ControlSensor.Value;
    public float MinPercent => ControlSensor.Control?.MinSoftwareValue ?? 0f;
    public float MaxPercent => ControlSensor.Control?.MaxSoftwareValue ?? 100f;

    public string DisplayName => IsGpu ? $"{HardwareName} · {Name}" : Name;
}

/// <summary>可选的曲线温度源。</summary>
public sealed record TemperatureSource(string Id, string DisplayName);

/// <summary>LibreHardwareMonitor 的薄封装：负责枚举硬件、读取传感器、写风扇。</summary>
public sealed class HardwareService : IDisposable
{
    private readonly object _gate = new();
    private readonly Computer _computer = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        // 内存（DDR5 SPD/SMBus）初始化约 4.5 秒，放到 MemoryService 后台加载，
        // 主初始化只保留 CPU/GPU/主板，让风扇控制在 1 秒内就绪。
        IsMemoryEnabled = false,
        IsMotherboardEnabled = true,
        IsControllerEnabled = false,
        IsStorageEnabled = false,
        IsNetworkEnabled = false,
        IsPsuEnabled = false,
        IsBatteryEnabled = false,
    };

    private readonly List<IHardware> _hardware = new();
    private readonly List<FanChannel> _fans = new();

    public bool IsOpen { get; private set; }
    public IReadOnlyList<FanChannel> Fans => _fans;
    public IReadOnlyList<IHardware> Hardware => _hardware;

    public void Open()
    {
        if (IsOpen) return;
        _computer.Open();
        IsOpen = true;

        // 先 Update 一次，确保 SuperIO/GPU 的子硬件与传感器（含风扇转速）已经创建，
        // 然后再建立风扇通道映射（否则转速传感器可能还没出现）。
        CollectHardware();
        foreach (var hw in _hardware)
        {
            try { hw.Update(); } catch { }
        }
        CollectHardware();
        BuildFans();
    }

    private void CollectHardware()
    {
        _hardware.Clear();
        foreach (var hw in _computer.Hardware) Collect(hw);
    }

    private void Collect(IHardware hw)
    {
        _hardware.Add(hw);
        foreach (var sub in hw.SubHardware) Collect(sub);
    }

    public void Update()
    {
        lock (_gate)
        {
            foreach (var hw in _hardware)
            {
                try { hw.Update(); }
                catch { /* 单个硬件读取失败不影响整体 */ }
            }
        }
    }

    private void BuildFans()
    {
        _fans.Clear();
        foreach (var hw in _hardware)
        {
            var tachometers = hw.Sensors.Where(s => s.SensorType == SensorType.Fan).ToList();
            foreach (var control in hw.Sensors.Where(s => s.SensorType == SensorType.Control && s.Control != null))
            {
                _fans.Add(new FanChannel
                {
                    Id = control.Identifier.ToString(),
                    Name = control.Name,
                    HardwareName = hw.Name,
                    IsGpu = hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel,
                    Hardware = hw,
                    ControlSensor = control,
                    RpmSensor = tachometers.FirstOrDefault(f => f.Name == control.Name),
                });
            }
        }
    }

    /// <summary>所有传感器当前值，key 为稳定 Identifier。</summary>
    public Dictionary<string, float?> ReadAll()
    {
        lock (_gate)
        {
            var values = new Dictionary<string, float?>();
            foreach (var hw in _hardware)
                foreach (var s in hw.Sensors)
                    values[s.Identifier.ToString()] = s.Value;
            return values;
        }
    }

    public IReadOnlyList<TemperatureSource> GetTemperatureSources()
    {
        lock (_gate)
        {
            var list = new List<TemperatureSource>();
            foreach (var hw in _hardware)
                foreach (var s in hw.Sensors.Where(s => s.SensorType == SensorType.Temperature && s.Value.HasValue))
                    list.Add(new TemperatureSource(s.Identifier.ToString(), $"{hw.Name} · {s.Name}"));
            return list;
        }
    }

    public void SetPercent(FanChannel fan, float percent)
    {
        lock (_gate)
        {
            var control = fan.ControlSensor.Control
                          ?? throw new InvalidOperationException($"通道不支持控制: {fan.Id}");
            var clamped = Math.Clamp(percent, Math.Max(0f, fan.MinPercent), fan.MaxPercent);
            control.SetSoftware(clamped);
        }
    }

    public void RestoreDefault(FanChannel fan)
    {
        lock (_gate)
        {
            try { fan.ControlSensor.Control?.SetDefault(); }
            catch { }
        }
    }

    public void RestoreAll()
    {
        lock (_gate)
        {
            foreach (var fan in _fans)
            {
                try { fan.ControlSensor.Control?.SetDefault(); }
                catch { }
            }
        }
    }

    public void Dispose()
    {
        if (!IsOpen) return;
        RestoreAll();
        try { _computer.Close(); } catch { }
        IsOpen = false;
    }
}
