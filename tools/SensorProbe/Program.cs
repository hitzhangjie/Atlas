using LibreHardwareMonitor.Hardware;
using System.Text;

Console.OutputEncoding = Encoding.UTF8;

var computer = new Computer
{
    IsCpuEnabled = true,
    IsGpuEnabled = true,
    IsMemoryEnabled = true,
    IsMotherboardEnabled = true,
    IsControllerEnabled = true,
    IsStorageEnabled = false,
    IsNetworkEnabled = false,
    IsPsuEnabled = false,
    IsBatteryEnabled = false,
};

Console.WriteLine("Opening Computer (read-only)...");
try
{
    computer.Open();
    Console.WriteLine("Computer opened.");
}
catch (Exception ex)
{
    Console.WriteLine($"Open failed: {ex.GetType().Name}: {ex.Message}");
    Environment.ExitCode = 1;
    return;
}

try
{
    foreach (var hardware in computer.Hardware)
    {
        Dump(hardware, 0);
    }
}
finally
{
    computer.Close();
}

Console.WriteLine();
Console.WriteLine("Done.");

static void Dump(IHardware hardware, int depth)
{
    var indent = new string(' ', depth * 2);
    string updateNote = "";
    try
    {
        hardware.Update();
    }
    catch (Exception ex)
    {
        updateNote = $" (Update failed: {ex.GetType().Name}: {ex.Message})";
    }

    Console.WriteLine($"{indent}[{hardware.HardwareType}] {hardware.Name}{updateNote}");

    foreach (var sensor in hardware.Sensors)
    {
        string controlInfo = "";
        if (sensor.SensorType == SensorType.Control && sensor.Control != null)
        {
            var c = sensor.Control;
            controlInfo = $"  CONTROL[{c.Identifier}] mode={c.ControlMode} sw={c.SoftwareValue:0.###} range=[{c.MinSoftwareValue:0.###},{c.MaxSoftwareValue:0.###}]";
        }

        Console.WriteLine($"{indent}  {sensor.SensorType,-12} {sensor.Name,-40} = {sensor.Value,10:0.###}{controlInfo}");
    }

    foreach (var sub in hardware.SubHardware)
    {
        Dump(sub, depth + 1);
    }
}
