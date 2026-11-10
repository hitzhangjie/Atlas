using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Atlas.Core;

/// <summary>
/// 只读曲线模板：单扇「快速模板」和全局「内置场景」共用同一套值，
/// 保证「静音 / 均衡 / 性能」在两层里指的是同一条曲线。
/// </summary>
public sealed class CurveTemplate
{
    public CurveTemplate(string id, string name, string description, IEnumerable<CurvePoint> curve)
    {
        Id = id;
        Name = name;
        Description = description;
        Curve = curve
            .Select(p => new CurvePoint { Temp = p.Temp, Percent = p.Percent })
            .OrderBy(p => p.Temp)
            .ToList()
            .AsReadOnly();
    }

    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public IReadOnlyList<CurvePoint> Curve { get; }

    public List<CurvePoint> CloneCurve() =>
        Curve.Select(p => new CurvePoint { Temp = p.Temp, Percent = p.Percent }).ToList();
}

/// <summary>内置曲线模板目录（代码内置、只读，不随用户编辑改变）。</summary>
public static class CurveTemplates
{
    public const string QuietId = "quiet";
    public const string BalancedId = "balanced";
    public const string PerformanceId = "performance";

    private static readonly CurveTemplate QuietTemplate = new(
        QuietId, "静音", "低转速优先，90°C 前都尽量安静", new[]
        {
            new CurvePoint { Temp = 30, Percent = 20 },
            new CurvePoint { Temp = 45, Percent = 25 },
            new CurvePoint { Temp = 60, Percent = 30 },
            new CurvePoint { Temp = 70, Percent = 40 },
            new CurvePoint { Temp = 80, Percent = 55 },
            new CurvePoint { Temp = 88, Percent = 75 },
            new CurvePoint { Temp = 95, Percent = 100 },
        });

    private static readonly CurveTemplate BalancedTemplate = new(
        BalancedId, "均衡", "响应与噪音兼顾，默认曲线", FanCurveConfig.DefaultCurve());

    private static readonly CurveTemplate PerformanceTemplate = new(
        PerformanceId, "性能", "散热优先，温度一升高就拉高转速", new[]
        {
            new CurvePoint { Temp = 30, Percent = 40 },
            new CurvePoint { Temp = 45, Percent = 55 },
            new CurvePoint { Temp = 60, Percent = 75 },
            new CurvePoint { Temp = 70, Percent = 90 },
            new CurvePoint { Temp = 80, Percent = 100 },
        });

    public static IReadOnlyList<CurveTemplate> All { get; } =
        new[] { QuietTemplate, BalancedTemplate, PerformanceTemplate };

    public static CurveTemplate Balanced => BalancedTemplate;

    public static CurveTemplate? Find(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>按 Id 取模板；未知 Id 安全回退到「均衡」。</summary>
    public static CurveTemplate Get(string? id) => Find(id) ?? BalancedTemplate;

    public static List<CurvePoint> CloneCurve(string? id) => Get(id).CloneCurve();

    /// <summary>当前曲线与哪个内置模板一致（容差 0.05）；不一致返回 null。</summary>
    public static string? Match(IEnumerable<CurvePoint>? curve)
    {
        if (curve is null) return null;
        var points = curve.Where(p => p is not null).OrderBy(p => p.Temp).ToList();
        if (points.Count == 0) return null;

        // NaN/Infinity 不能参与容差比较（NaN 会让所有比较返回 false，反而“匹配成功”）
        if (points.Any(p => !float.IsFinite(p.Temp) || !float.IsFinite(p.Percent))) return null;

        foreach (var template in All)
        {
            var expected = template.Curve;
            if (expected.Count != points.Count) continue;

            var same = true;
            for (var i = 0; i < expected.Count; i++)
            {
                if (Math.Abs(expected[i].Temp - points[i].Temp) > 0.05f ||
                    Math.Abs(expected[i].Percent - points[i].Percent) > 0.05f)
                {
                    same = false;
                    break;
                }
            }

            if (same) return template.Id;
        }

        return null;
    }
}

/// <summary>
/// 内置全局场景：一键把所有未锁定通道切换为温度曲线并套用同名模板。
/// 只读，不可覆盖、不可改名；用户调整后的结果只能另存为「我的配置」。
/// </summary>
public sealed class ScenePreset
{
    public ScenePreset(string id, string name, string description, CurveTemplate template)
    {
        Id = id;
        Name = name;
        Description = description;
        Template = template;
    }

    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public CurveTemplate Template { get; }
}

/// <summary>内置场景目录（代码内置、只读）。</summary>
public static class ScenePresets
{
    public const string QuietId = "builtin.quiet";
    public const string BalancedId = "builtin.balanced";
    public const string PerformanceId = "builtin.performance";

    private static readonly ScenePreset QuietScene = new(
        QuietId, "静音", "所有未锁定通道切换为温度曲线，并套用「静音」曲线", CurveTemplates.Get(CurveTemplates.QuietId));

    private static readonly ScenePreset BalancedScene = new(
        BalancedId, "均衡", "所有未锁定通道切换为温度曲线，并套用「均衡」曲线", CurveTemplates.Balanced);

    private static readonly ScenePreset PerformanceScene = new(
        PerformanceId, "性能", "所有未锁定通道切换为温度曲线，并套用「性能」曲线", CurveTemplates.Get(CurveTemplates.PerformanceId));

    public static IReadOnlyList<ScenePreset> All { get; } =
        new[] { QuietScene, BalancedScene, PerformanceScene };

    public static ScenePreset? Find(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : All.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>场景名/恢复项名保留，不允许用户配置重名（避免「内置 vs 我的」混淆）。</summary>
    public static bool IsReservedName(string? name)
    {
        var canonical = Canonicalize(name);
        if (canonical.Length == 0) return false;

        if (All.Any(s => string.Equals(s.Name, canonical, StringComparison.OrdinalIgnoreCase))) return true;
        return string.Equals(canonical, "BIOS 默认", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>折叠空白（含连续空格），避免「BIOS  默认」这类写法绕过保留名。</summary>
    internal static string Canonicalize(string? name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0) return "";
        return string.Join(' ', trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}

/// <summary>
/// 单个通道的热设置快照。故意不包含 CustomName / Protected：
/// 它们是通道身份与安全属性，不随场景或配置切换。
/// </summary>
public sealed class FanSettingsSnapshot
{
    public FanMode Mode { get; set; } = FanMode.Disabled;
    public float FixedPercent { get; set; } = 50f;
    public string SourceId { get; set; } = "";
    public float MinPercent { get; set; } = 20f;
    public float MaxPercent { get; set; } = 100f;
    public List<CurvePoint> Curve { get; set; } = new();

    public static FanSettingsSnapshot Capture(FanCurveConfig config) => new()
    {
        Mode = config.Mode,
        FixedPercent = config.FixedPercent,
        SourceId = config.SourceId ?? "",
        MinPercent = config.MinPercent,
        MaxPercent = config.MaxPercent,
        Curve = (config.Curve ?? new List<CurvePoint>())
            .Select(p => new CurvePoint { Temp = p.Temp, Percent = p.Percent }).ToList(),
    };

    public FanSettingsSnapshot Clone() => new()
    {
        Mode = Mode,
        FixedPercent = FixedPercent,
        SourceId = SourceId,
        MinPercent = MinPercent,
        MaxPercent = MaxPercent,
        Curve = Curve.Select(p => new CurvePoint { Temp = p.Temp, Percent = p.Percent }).ToList(),
    };

    public void ApplyTo(FanCurveConfig config)
    {
        config.Mode = Mode;
        config.FixedPercent = FixedPercent;
        config.SourceId = SourceId ?? "";
        config.MinPercent = MinPercent;
        config.MaxPercent = MaxPercent;
        config.Curve = (Curve ?? new List<CurvePoint>())
            .Select(p => new CurvePoint { Temp = p.Temp, Percent = p.Percent }).ToList();
    }
}

/// <summary>用户命名的整套配置（「我的配置」）：全量快照，可改名/更新/删除。</summary>
public sealed class SavedProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public List<FanProfileEntry> Fans { get; set; } = new();

    /// <summary>仅用于界面展示，不写入配置文件。</summary>
    [JsonIgnore]
    public string UpdatedText => UpdatedAt.ToString("MM-dd HH:mm", CultureInfo.CurrentCulture);

    public SavedProfile Clone() => new()
    {
        Id = Id,
        Name = Name,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        Fans = Fans.Select(f => new FanProfileEntry
        {
            FanId = f.FanId,
            FanName = f.FanName,
            Settings = f.Settings.Clone(),
        }).ToList(),
    };
}

public sealed class FanProfileEntry
{
    public string FanId { get; set; } = "";
    public string FanName { get; set; } = "";
    public FanSettingsSnapshot Settings { get; set; } = new();
}

/// <summary>场景/配置应用结果，用于状态栏反馈。</summary>
public sealed class PresetApplyResult
{
    public int AppliedChannels { get; set; }

    /// <summary>锁定的通道不参与场景/配置应用，保持原样。</summary>
    public int SkippedProtected { get; set; }

    /// <summary>配置里记录、但当前机器没有的通道（换硬件/少接了风扇）。</summary>
    public int MissingChannels { get; set; }

    /// <summary>当前存在、但配置快照里没有的通道（保持不变）。</summary>
    public int UntouchedChannels { get; set; }

    public string Summary
    {
        get
        {
            var text = $"已应用 {AppliedChannels} 个通道";
            if (SkippedProtected > 0) text += $" · 跳过 {SkippedProtected} 个锁定通道";
            if (MissingChannels > 0) text += $" · {MissingChannels} 个配置内通道当前不可用";
            if (UntouchedChannels > 0) text += $" · {UntouchedChannels} 个新通道保持原样";
            return text;
        }
    }
}

public sealed record ProfileNameValidation(bool IsValid, string Message, string Normalized)
{
    public static ProfileNameValidation Ok(string normalized) => new(true, "", normalized);
    public static ProfileNameValidation Fail(string message) => new(false, message, "");
}

/// <summary>
/// 预设 / 配置的纯逻辑层（不依赖 WPF）：快照、应用、指纹、命名校验。
/// 方便 CLI 自测与后续扩展（导入导出、快捷键切换等）。
/// </summary>
public static class FanProfileService
{
    public const int MaxProfileNameLength = 24;

    /// <summary>所有通道热设置的稳定指纹；用于「当前方案是否已被修改」。</summary>
    public static string Fingerprint(IEnumerable<FanCurveConfig> fans)
    {
        var builder = new StringBuilder();
        foreach (var fan in fans.Where(f => f is not null).OrderBy(f => f.FanId, StringComparer.Ordinal))
        {
            builder.Append(fan.FanId).Append('|')
                .Append((int)fan.Mode).Append('|')
                .Append(Format(fan.FixedPercent)).Append('|')
                .Append(fan.SourceId ?? "").Append('|')
                .Append(Format(fan.MinPercent)).Append('|')
                .Append(Format(fan.MaxPercent)).Append('|');

            foreach (var point in (fan.Curve ?? new List<CurvePoint>())
                         .Where(p => p is not null).OrderBy(p => p.Temp))
                builder.Append(Format(point.Temp)).Append(':').Append(Format(point.Percent)).Append(',');

            builder.Append('\n');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string Format(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    public static SavedProfile CaptureProfile(AtlasConfig config, string name, DateTime? now = null)
    {
        var time = now ?? DateTime.Now;
        return new SavedProfile
        {
            Id = "profile-" + Guid.NewGuid().ToString("N"),
            Name = (name ?? "").Trim(),
            CreatedAt = time,
            UpdatedAt = time,
            Fans = config.Fans.Select(fan => new FanProfileEntry
            {
                FanId = fan.FanId,
                FanName = DisplayFanName(fan),
                Settings = FanSettingsSnapshot.Capture(fan),
            }).ToList(),
        };
    }

    public static void UpdateProfile(AtlasConfig config, SavedProfile profile, DateTime? now = null)
    {
        profile.Fans = config.Fans.Select(fan => new FanProfileEntry
        {
            FanId = fan.FanId,
            FanName = DisplayFanName(fan),
            Settings = FanSettingsSnapshot.Capture(fan),
        }).ToList();
        profile.UpdatedAt = now ?? DateTime.Now;
        config.MarkApplied(null, profile.Id);
    }

    /// <summary>一键应用内置场景：所有未锁定通道 → 温度曲线 + 同名模板曲线。</summary>
    public static PresetApplyResult ApplyScene(AtlasConfig config, ScenePreset scene)
    {
        var result = new PresetApplyResult();
        foreach (var fan in config.Fans)
        {
            if (fan.Protected)
            {
                result.SkippedProtected++;
                continue;
            }

            fan.Mode = FanMode.Curve;
            fan.Curve = scene.Template.CloneCurve();
            result.AppliedChannels++;
        }

        // 没有任何通道真正应用（比如全部锁定 / 没有通道）时，不要声称当前方案来自该场景
        if (result.AppliedChannels > 0) config.MarkApplied(scene.Id, null);
        return result;
    }

    /// <summary>应用用户配置：按 FanId 匹配；锁定通道跳过，缺失/新增通道保持原样。</summary>
    public static PresetApplyResult ApplyProfile(AtlasConfig config, SavedProfile profile)
    {
        var result = new PresetApplyResult();
        var entries = profile.Fans
            .Where(e => e is not null && !string.IsNullOrEmpty(e.FanId) && e.Settings is not null)
            .GroupBy(e => e.FanId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var currentIds = new HashSet<string>(config.Fans.Select(f => f.FanId), StringComparer.Ordinal);
        result.MissingChannels = entries.Keys.Count(id => !currentIds.Contains(id));

        foreach (var fan in config.Fans)
        {
            if (fan.Protected)
            {
                result.SkippedProtected++;
                continue;
            }

            if (entries.TryGetValue(fan.FanId, out var entry))
            {
                entry.Settings.ApplyTo(fan);
                result.AppliedChannels++;
            }
            else
            {
                result.UntouchedChannels++;
            }
        }

        // 全锁定 / 全缺失时保持原来源，避免状态栏假装「当前 = 该配置」
        if (result.AppliedChannels > 0) config.MarkApplied(null, profile.Id);
        return result;
    }

    /// <summary>
    /// 「全部交由 BIOS 控制」对当前方案状态的影响：所有通道模式改回 BIOS 默认。
    /// 锁定通道同样改模式——锁定约束的是「永不写硬件」，不是「不能改配置」。
    ///
    /// 来源归属遵循「能编辑的永远是当前方案」：
    ///  - 当前来源是「我的配置」：保留来源，不刷新基线，界面因此显示「已修改」，
    ///    用户点「保存」即可写回该配置（不会新建配置，也不会未经确认覆盖已保存配置）；
    ///  - 未归档草稿：保持未归档，并把当前设置记为基线。
    ///
    /// 注意：内置场景是只读的，UI 在调用本方法前已用 <c>EnsurePresetEditable()</c> 拦截
    /// （与其他编辑动作一致：先引导复制为「我的配置」）。这里的场景兜底分支只是防御性逻辑，
    /// 防止将来有别的调用方绕过 UI 闸门时留下「场景 + 已恢复」的矛盾状态。
    /// </summary>
    /// <returns>恢复前关联着的「我的配置」，没有则为 null（供调用方拼状态栏提示）。</returns>
    public static SavedProfile? RestoreToBiosDefaults(AtlasConfig config)
    {
        var activeProfile = config.FindActiveProfile();

        foreach (var fan in config.Fans)
            fan.Mode = FanMode.Disabled;

        // 兜底：内置场景只读、无法写回，绕过 UI 时退回未归档（正常 UI 路径不会走到这里）
        config.ActiveSceneId = null;
        if (activeProfile is null)
        {
            config.ActiveProfileId = null;
            config.StateFingerprint = config.CurrentFingerprint();
        }

        return activeProfile;
    }

    public static bool DeleteProfile(AtlasConfig config, string profileId)
    {
        var removed = config.Profiles.RemoveAll(p => string.Equals(p.Id, profileId, StringComparison.Ordinal)) > 0;
        if (!removed) return false;

        if (string.Equals(config.ActiveProfileId, profileId, StringComparison.Ordinal))
        {
            config.ActiveProfileId = null;
            // 当前设置保持不动：来源没了，但方案本身没有变，所以基线同步为当前值（干净未归档）
            config.StateFingerprint = config.CurrentFingerprint();
        }

        return true;
    }

    public static ProfileNameValidation ValidateProfileName(
        AtlasConfig config, string? name, string? exceptProfileId = null)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0)
            return ProfileNameValidation.Fail("名称不能为空。");

        // 零宽空格等 Format 字符会让「静音」看起来像另一个名字，造成保留名/重名绕过
        if (trimmed.Any(c => char.IsControl(c) ||
                             CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format))
            return ProfileNameValidation.Fail("名称不能包含不可见字符。");

        // 折叠连续空白："BIOS  默认" 也要被识别为保留名
        var normalized = ScenePresets.Canonicalize(trimmed);
        if (normalized.Length == 0)
            return ProfileNameValidation.Fail("名称不能为空。");

        if (normalized.Length > MaxProfileNameLength)
            return ProfileNameValidation.Fail($"名称最多 {MaxProfileNameLength} 个字符。");

        if (ScenePresets.IsReservedName(normalized))
            return ProfileNameValidation.Fail(
                $"「{normalized}」是内置场景名（只读）。为避免与内置场景混淆，请换一个名字，例如「{normalized} · 我的」。");

        var duplicate = config.Profiles.Any(p =>
            !string.Equals(p.Id, exceptProfileId, StringComparison.Ordinal) &&
            string.Equals(ScenePresets.Canonicalize(p.Name), normalized, StringComparison.OrdinalIgnoreCase));
        if (duplicate)
            return ProfileNameValidation.Fail("已有同名配置，请换一个名字。");

        return ProfileNameValidation.Ok(normalized);
    }

    /// <summary>配置里记录该通道时使用的展示名，方便配置管理界面说明。</summary>
    public static string DisplayFanName(FanCurveConfig fan) =>
        string.IsNullOrWhiteSpace(fan.CustomName) ? fan.FanName : $"{fan.FanName} ({fan.CustomName})";
}
