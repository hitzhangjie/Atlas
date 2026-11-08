using Atlas.Core;
using LibreHardwareMonitor.Hardware;
using System.Text;

Console.OutputEncoding = Encoding.UTF8;

var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "list";

var hardware = new HardwareService();
try
{
    hardware.Open();
    hardware.Update();
}
catch (Exception ex)
{
    Console.WriteLine($"打开硬件失败: {ex.GetType().Name}: {ex.Message}");
    return 1;
}

switch (mode)
{
    case "list":
        PrintList(hardware);
        break;

    case "smoke":
        PrintSmoke(hardware, seconds: args.Length > 1 && int.TryParse(args[1], out var s) ? s : 5);
        break;

    default:
        Console.WriteLine("用法: Atlas.Cli [list|smoke] [秒数]");
        break;
}

hardware.Dispose();
return 0;

static void PrintList(HardwareService hardware)
{
    Console.WriteLine("=== 硬件树（温度 / 风扇 / 控制） ===");
    foreach (var hw in hardware.Hardware)
    {
        var interesting = hw.Sensors
            .Where(s => s.SensorType is SensorType.Temperature or SensorType.Fan or SensorType.Control)
            .Where(s => s.Value.HasValue)
            .ToList();
        if (interesting.Count == 0) continue;

        Console.WriteLine($"[{hw.HardwareType}] {hw.Name}");
        foreach (var s in interesting)
        {
            var ctrl = s.Control != null ? $"  control=[{s.Control.MinSoftwareValue:0}-{s.Control.MaxSoftwareValue:0}]" : "";
            Console.WriteLine($"  {s.SensorType,-11} {s.Name,-38} = {s.Value,8:0.###}{ctrl}");
        }
    }

    Console.WriteLine();
    Console.WriteLine($"=== 风扇通道（{hardware.Fans.Count}） ===");
    foreach (var fan in hardware.Fans)
        Console.WriteLine($"{fan.DisplayName,-28} id={fan.Id,-42} rpm={fan.Rpm,6:0} pwm={fan.Percent,6:0.##} range=[{fan.MinPercent:0}-{fan.MaxPercent:0}]");

    Console.WriteLine();
    Console.WriteLine($"=== 可选温度源（{hardware.GetTemperatureSources().Count}） ===");
    foreach (var source in hardware.GetTemperatureSources())
        Console.WriteLine($"{source.DisplayName,-60} {source.Id}");
}

static void PrintSmoke(HardwareService hardware, int seconds)
{
    var sources = hardware.GetTemperatureSources();
    var cpuPackage = sources.FirstOrDefault(s => s.DisplayName.Contains("CPU Package"))?.Id;
    var gpuCore = sources.FirstOrDefault(s => s.DisplayName.Contains("GPU Core") && s.DisplayName.Contains("NVIDIA"))?.Id;
    var gpuHotspot = sources.FirstOrDefault(s => s.DisplayName.Contains("GPU Hot Spot"))?.Id;

    Console.WriteLine($"监控 {seconds} 秒（Ctrl+C 退出）…");
    for (var i = 0; i < seconds; i++)
    {
        hardware.Update();
        var values = hardware.ReadAll();

        string Temp(string? id) => id != null && values.TryGetValue(id, out var v) && v.HasValue ? $"{v.Value:0.0}°C" : "--";

        var fanText = string.Join("  ", hardware.Fans
            .Where(f => !f.IsGpu)
            .Select(f => $"{f.Name}={f.Rpm:0}rpm/{(f.Percent.HasValue ? f.Percent.Value.ToString("0") : "--")}%"));

        var dimmMax = hardware.Hardware
            .Where(h => h.HardwareType == HardwareType.Memory)
            .SelectMany(h => h.Sensors)
            .Where(s => s.SensorType == SensorType.Temperature && s.Name.StartsWith("DIMM") && s.Value.HasValue)
            .Select(s => s.Value!.Value)
            .DefaultIfEmpty(float.NaN)
            .Max();

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] CPU={Temp(cpuPackage)} GPU={Temp(gpuCore)} Hotspot={Temp(gpuHotspot)} DIMMmax={(float.IsNaN(dimmMax) ? "--" : $"{dimmMax:0.0}°C")}");
        Console.WriteLine($"           {fanText}");
        Thread.Sleep(1000);
    }
}
