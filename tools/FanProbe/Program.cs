using LibreHardwareMonitor.Hardware;
using System.Text;

// 风扇通道映射测试（需要管理员权限）：
// 逐一把主板每个 Control 通道设为 100%，观察各风扇转速变化，然后恢复默认（BIOS 控制）。
// 用于确认「NCT6798D Fan #1..7」分别对应哪个物理风扇/水泵。
Console.OutputEncoding = Encoding.UTF8;

var computer = new Computer
{
    IsCpuEnabled = true,
    IsGpuEnabled = true,
    IsMemoryEnabled = true,
    IsMotherboardEnabled = true,
    IsControllerEnabled = true,
};

var touched = new List<(IHardware Hardware, ISensor Sensor)>();
var controlHardware = new Dictionary<ISensor, IHardware>();

try
{
    computer.Open();

    var allHardware = new List<IHardware>();
    void Collect(IHardware hw)
    {
        allHardware.Add(hw);
        foreach (var sub in hw.SubHardware) Collect(sub);
    }
    foreach (var hw in computer.Hardware) Collect(hw);

    // 收集控制通道（先跳过 GPU，避免意外抬高显卡风扇）
    var controls = new List<ISensor>();
    foreach (var hw in allHardware)
    {
        hw.Update();
        foreach (var s in hw.Sensors)
        {
            if (s.SensorType == SensorType.Control && s.Control != null)
            {
                if (hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
                    continue;
                controls.Add(s);
                controlHardware[s] = hw;
            }
        }
    }

    Console.WriteLine($"发现 {controls.Count} 个主板/控制器风扇通道");
    if (controls.Count == 0) { Console.WriteLine("没有可测试通道。"); return; }

    foreach (var hw in allHardware) hw.Update();
    var fanSensors = new List<(IHardware Hardware, ISensor Sensor)>();
    foreach (var hw in allHardware)
        foreach (var s in hw.Sensors)
            if (s.SensorType == SensorType.Fan)
                fanSensors.Add((hw, s));

    string Snapshot()
    {
        foreach (var hw in allHardware) hw.Update();
        var parts = fanSensors.Select(f => $"{f.Sensor.Name}={f.Sensor.Value:0}");
        return string.Join(" | ", parts);
    }

    Console.WriteLine("基线: " + Snapshot());
    Console.WriteLine();

    foreach (var control in controls)
    {
        var hw = controlHardware[control];
        touched.Add((hw, control));
        var before = fanSensors.ToDictionary(f => f.Sensor, f => f.Sensor.Value ?? -1);

        try
        {
            control.Control!.SetSoftware(100f);
            Thread.Sleep(2600);
            foreach (var f in fanSensors) f.Hardware.Update();

            var changed = fanSensors
                .Where(f => Math.Abs((f.Sensor.Value ?? -1) - before[f.Sensor]) > 30)
                .Select(f => $"{f.Sensor.Name} {before[f.Sensor]:0} -> {f.Sensor.Value:0}");

            Console.WriteLine($"{control.Name,-10} -> 100%  变化: {(changed.Any() ? string.Join(", ", changed) : "无")}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{control.Name,-10} 设置失败: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { control.Control!.SetDefault(); } catch { }
            Thread.Sleep(900);
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"整体失败: {ex.GetType().Name}: {ex.Message}");
}
finally
{
    foreach (var (hw, s) in touched)
    {
        try { s.Control!.SetDefault(); } catch { }
    }
    Thread.Sleep(800);
    Console.WriteLine();
    Console.WriteLine("已恢复默认控制。");
    try { computer.Close(); } catch { }
}
