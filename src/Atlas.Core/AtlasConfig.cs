using System.Text.Json;

namespace Atlas.Core;

public enum FanMode
{
    Disabled = 0,
    Fixed = 1,
    Curve = 2,
}

public sealed class CurvePoint
{
    public float Temp { get; set; }
    public float Percent { get; set; }
}

public sealed class FanCurveConfig
{
    public string FanId { get; set; } = "";
    public string FanName { get; set; } = "";

    /// <summary>自定义备注名，显示为「Fan #1 (备注名)」，编号部分始终保留。</summary>
    public string CustomName { get; set; } = "";

    /// <summary>BIOS 默认 = Disabled；固定转速 = Fixed；温度曲线 = Curve。</summary>
    public FanMode Mode { get; set; } = FanMode.Disabled;

    /// <summary>锁定后 Atlas 永不写这个通道（用于水泵/不确认的通道）。</summary>
    public bool Protected { get; set; }

    public float FixedPercent { get; set; } = 50f;

    public string SourceId { get; set; } = "";
    public float MinPercent { get; set; } = 20f;
    public float MaxPercent { get; set; } = 100f;
    public List<CurvePoint> Curve { get; set; } = DefaultCurve();

    /// <summary>旧版配置兼容字段（v0.1 的「启用曲线」）。新配置请用 Mode。</summary>
    public bool Enabled { get; set; }

    public static List<CurvePoint> DefaultCurve() => new()
    {
        new CurvePoint { Temp = 30, Percent = 30 },
        new CurvePoint { Temp = 50, Percent = 40 },
        new CurvePoint { Temp = 65, Percent = 55 },
        new CurvePoint { Temp = 75, Percent = 75 },
        new CurvePoint { Temp = 85, Percent = 100 },
    };
}

public sealed class AtlasConfig
{
    public int PollMs { get; set; } = 1000;
    public List<FanCurveConfig> Fans { get; set; } = new();

    public static string ConfigDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Atlas");

    public static string ConfigPath => Path.Combine(ConfigDir, "config.json");

    public static AtlasConfig Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return new AtlasConfig();
            var config = JsonSerializer.Deserialize<AtlasConfig>(File.ReadAllText(ConfigPath)) ?? new AtlasConfig();

            // v0.1 配置迁移：Enabled=true 等价于 Mode=Curve
            foreach (var fan in config.Fans)
                if (fan.Mode == FanMode.Disabled && fan.Enabled)
                    fan.Mode = FanMode.Curve;

            return config;
        }
        catch
        {
            return new AtlasConfig();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public FanCurveConfig GetOrCreate(FanChannel fan)
    {
        var existing = Fans.FirstOrDefault(f => f.FanId == fan.Id);
        if (existing != null) return existing;

        existing = new FanCurveConfig { FanId = fan.Id, FanName = fan.DisplayName };
        Fans.Add(existing);
        return existing;
    }
}
