using LibreHardwareMonitor.Hardware;
using System.Diagnostics;
using System.Text;

Console.OutputEncoding = Encoding.UTF8;

static Computer Make(bool cpu, bool gpu, bool mem, bool mobo, bool ctrl) => new()
{
    IsCpuEnabled = cpu,
    IsGpuEnabled = gpu,
    IsMemoryEnabled = mem,
    IsMotherboardEnabled = mobo,
    IsControllerEnabled = ctrl,
    IsStorageEnabled = false,
    IsNetworkEnabled = false,
    IsPsuEnabled = false,
    IsBatteryEnabled = false,
};

var variants = new (string Name, Computer Computer)[]
{
    ("warmup full", Make(true, true, true, true, true)),
    ("full (cpu+gpu+mem+mobo+ctrl)", Make(true, true, true, true, true)),
    ("no controller", Make(true, true, true, true, false)),
    ("no memory", Make(true, true, false, true, true)),
    ("no memory + no controller", Make(true, true, false, true, false)),
    ("mobo only", Make(false, false, false, true, false)),
    ("cpu + gpu", Make(true, true, false, false, false)),
    ("cpu + mobo", Make(true, false, false, true, false)),
    ("memory only", Make(false, false, true, false, false)),
};

foreach (var (name, computer) in variants)
{
    var watch = Stopwatch.StartNew();
    try
    {
        computer.Open();
        watch.Stop();
        Console.WriteLine($"{name,-30} Open = {watch.ElapsedMilliseconds,6} ms");
    }
    catch (Exception ex)
    {
        watch.Stop();
        Console.WriteLine($"{name,-30} FAILED = {watch.ElapsedMilliseconds,6} ms : {ex.GetType().Name}: {ex.Message}");
    }
    finally
    {
        try { computer.Close(); } catch { }
    }
}
