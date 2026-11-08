using LibreHardwareMonitor.Hardware;

namespace Atlas.Core;

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
    private float? _maxDimmTemperature;
    private bool _disposed;

    public bool IsReady { get; private set; }
    public string? LastError { get; private set; }

    public float? MaxDimmTemperature
    {
        get { lock (_gate) return _maxDimmTemperature; }
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

            float? max = null;
            foreach (var hardware in _computer.Hardware)
            {
                try { hardware.Update(); }
                catch { }

                foreach (var sensor in hardware.Sensors)
                {
                    if (sensor.SensorType != SensorType.Temperature) continue;
                    if (!sensor.Name.StartsWith("DIMM") || !sensor.Value.HasValue) continue;
                    max = max is null ? sensor.Value : Math.Max(max.Value, sensor.Value.Value);
                }
            }

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
