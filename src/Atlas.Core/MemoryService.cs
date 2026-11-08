using LibreHardwareMonitor.Hardware;

namespace Atlas.Core;

/// <summary>一根内存条（DIMM）的温度读数。</summary>
public sealed record DimmTemperature(string Name, float Value);

/// <summary>
/// 独立的 DIMM 温度读取服务。
/// DDR5 SPD/SMBus 初始化非常慢（实测约 4.5 秒），所以不放在主初始化路径上，
/// 由后台线程单独打开，避免拖慢风扇控制就绪。
/// </summary>
public sealed class MemoryService : IDisposable
{
    private readonly object _gate = new();
    private Computer? _computer;
    private System.Threading.Timer? _timer;
    private List<DimmTemperature> _dimms = new();
    private float? _maxDimmTemperature;
    private bool _disposed;

    public bool IsReady { get; private set; }
    public string? LastError { get; private set; }

    public float? MaxDimmTemperature
    {
        get { lock (_gate) return _maxDimmTemperature; }
    }

    /// <summary>每根内存条的温度（按名称排序）；未加载完成时为空。</summary>
    public IReadOnlyList<DimmTemperature> Dimms
    {
        get { lock (_gate) return _dimms.ToArray(); }
    }

    /// <summary>打开内存传感器并启动后台刷新；应在后台线程调用（会阻塞约 4–5 秒）。</summary>
    public void Start()
    {
        try
        {
            var computer = new Computer
            {
                IsCpuEnabled = false,
                IsGpuEnabled = false,
                IsMemoryEnabled = true,
                IsMotherboardEnabled = false,
                IsControllerEnabled = false,
                IsStorageEnabled = false,
                IsNetworkEnabled = false,
                IsPsuEnabled = false,
                IsBatteryEnabled = false,
            };

            computer.Open();
            lock (_gate)
            {
                _computer = computer;
                IsReady = true;
            }

            Refresh();
            _timer = new System.Threading.Timer(_ => Refresh(), null, 2000, 2000);
        }
        catch (Exception ex)
        {
            lock (_gate) LastError = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    private void Refresh()
    {
        lock (_gate)
        {
            if (_disposed || _computer is null) return;

            var dimms = new List<DimmTemperature>();
            float? max = null;
            foreach (var hardware in _computer.Hardware)
            {
                try { hardware.Update(); }
                catch { }

                foreach (var sensor in hardware.Sensors)
                {
                    if (sensor.SensorType != SensorType.Temperature) continue;
                    if (!sensor.Name.StartsWith("DIMM") || !sensor.Value.HasValue) continue;

                    dimms.Add(new DimmTemperature(sensor.Name, sensor.Value.Value));
                    max = max is null ? sensor.Value : Math.Max(max.Value, sensor.Value.Value);
                }
            }

            dimms.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            _dimms = dimms;
            _maxDimmTemperature = max;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
            try { _computer?.Close(); } catch { }
            _computer = null;
        }
    }
}
