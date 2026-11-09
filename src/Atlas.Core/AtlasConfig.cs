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

    /// <summary>用户命名并保存的整套配置（「我的配置」），不是内置预设。</summary>
    public List<SavedProfile> Profiles { get; set; } = new();

    /// <summary>当前方案来源：内置场景 Id（只读）或用户配置 Id，二选一；为空表示未归档的自定义方案。</summary>
    public string? ActiveSceneId { get; set; }
    public string? ActiveProfileId { get; set; }

    /// <summary>应用场景/配置（或保存/更新配置）那一刻所有通道热设置的指纹，用于判断「已修改」。</summary>
    public string? StateFingerprint { get; set; }

    public static string ConfigDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Atlas");

    public static string ConfigPath => Path.Combine(ConfigDir, "config.json");

    public static AtlasConfig Load()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                var fresh = new AtlasConfig();
                fresh.Normalize();
                return fresh;
            }

            var config = JsonSerializer.Deserialize<AtlasConfig>(File.ReadAllText(ConfigPath)) ?? new AtlasConfig();
            config.Normalize();

            // v0.1 配置迁移：Enabled=true 等价于 Mode=Curve
            foreach (var fan in config.Fans)
                if (fan.Mode == FanMode.Disabled && fan.Enabled)
                    fan.Mode = FanMode.Curve;

            return config;
        }
        catch
        {
            var fallback = new AtlasConfig();
            fallback.Normalize();
            return fallback;
        }
    }

    /// <summary>修复旧配置 / 手工编辑 JSON 可能造成的空集合与悬空引用。</summary>
    public void Normalize()
    {
        Fans ??= new List<FanCurveConfig>();
        Profiles ??= new List<SavedProfile>();

        Fans.RemoveAll(f => f is null);
        foreach (var fan in Fans)
        {
            fan.FanId ??= "";
            fan.FanName ??= "";
            fan.CustomName ??= "";
            fan.SourceId ??= "";
            fan.Curve ??= FanCurveConfig.DefaultCurve();
            fan.Curve.RemoveAll(p => p is null);
        }

        // 配置 Id 去重（保留第一个），避免同 Id 删除时一次删两条
        var seenProfileIds = new HashSet<string>(StringComparer.Ordinal);
        Profiles.RemoveAll(p => p is null || string.IsNullOrWhiteSpace(p.Id) || !seenProfileIds.Add(p.Id));

        foreach (var profile in Profiles)
        {
            profile.Name ??= "";
            profile.Fans ??= new List<FanProfileEntry>();
            profile.Fans.RemoveAll(e => e is null || string.IsNullOrWhiteSpace(e.FanId));

            var seenFanIds = new HashSet<string>(StringComparer.Ordinal);
            profile.Fans.RemoveAll(e => !seenFanIds.Add(e.FanId));

            foreach (var entry in profile.Fans)
            {
                entry.FanName ??= "";
                entry.Settings ??= new FanSettingsSnapshot();
                entry.Settings.SourceId ??= "";
                entry.Settings.Curve ??= new List<CurvePoint>();
                entry.Settings.Curve.RemoveAll(p => p is null);
            }
        }

        // 活动来源清理：配置被手工删掉时不要把「来源」停留在不存在的名字上；两个来源互斥（优先用户配置）。
        if (FindProfile(ActiveProfileId) is null) ActiveProfileId = null;
        if (ActiveProfileId is not null) ActiveSceneId = null;
        else if (ScenePresets.Find(ActiveSceneId) is null) ActiveSceneId = null;
    }

    /// <summary>
    /// 没有基线时把当前设置记为基线。必须在硬件通道枚举完成、默认温度源补齐之后调用：
    /// 全新安装时 BuildRows 会补 SourceId，若在 Load 里就建基线，启动后会被误判为「已修改」。
    /// 已有基线（上次会话应用/保存/恢复后写入）不会被覆盖，跨重启的真实修改仍能保留。
    /// </summary>
    public void EnsureStateBaseline()
    {
        if (string.IsNullOrEmpty(StateFingerprint))
            StateFingerprint = CurrentFingerprint();
    }

    public void Save()
    {
        Normalize();
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public SavedProfile? FindProfile(string? profileId) =>
        string.IsNullOrWhiteSpace(profileId)
            ? null
            : Profiles.FirstOrDefault(p => string.Equals(p.Id, profileId, StringComparison.Ordinal));

    public ScenePreset? FindActiveScene() => ScenePresets.Find(ActiveSceneId);

    public SavedProfile? FindActiveProfile() => FindProfile(ActiveProfileId);

    public string CurrentFingerprint() => FanProfileService.Fingerprint(Fans);

    /// <summary>当前方案自上次应用/保存后是否被修改过（备注名、锁定等非热设置不算）。</summary>
    public bool IsCurrentStateModified() =>
        !string.IsNullOrEmpty(StateFingerprint) &&
        !string.Equals(StateFingerprint, CurrentFingerprint(), StringComparison.Ordinal);

    /// <summary>记录「当前方案来自哪里」，并刷新基线指纹。</summary>
    public void MarkApplied(string? sceneId, string? profileId)
    {
        ActiveSceneId = sceneId;
        ActiveProfileId = profileId;
        StateFingerprint = CurrentFingerprint();
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
