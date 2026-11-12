using Atlas.Core;
using System.Text.Json;

namespace Atlas.Cli;

/// <summary>
/// 预设 / 配置纯逻辑自测：不打开硬件、不需要管理员权限。
/// 运行：dotnet run --project src\Atlas.Cli -- preset-selftest
/// </summary>
internal static class PresetSelfTest
{
    private static int _passed;
    private static int _failed;

    public static int Run()
    {
        Console.WriteLine("=== Atlas 预设/配置自测 ===");
        TestTemplates();
        TestScenes();
        TestCaptureAndApplyProfile();
        TestApplySceneRespectsProtected();
        TestFingerprint();
        TestJsonRoundTrip();
        TestLegacyJsonNormalization();
        TestNullTolerantNormalization();
        TestBaselineAndEmptyApply();
        TestNameValidation();
        TestDeleteActiveProfile();
        TestUpdateProfile();
        TestMissingAndUntouchedChannels();
        TestRestoreToBiosDefaults();
        TestEditHistory();

        Console.WriteLine();
        Console.WriteLine($"结果：{_passed} 通过，{_failed} 失败");
        return _failed == 0 ? 0 : 1;
    }

    private static void TestTemplates()
    {
        Section("曲线模板");
        Check("模板数量 = 3", CurveTemplates.All.Count == 3, $"{CurveTemplates.All.Count}");
        Check("模板名称为 静音/均衡/性能",
            CurveTemplates.All.Select(t => t.Name).SequenceEqual(new[] { "静音", "均衡", "性能" }));
        Check("均衡模板 == 默认曲线",
            SameCurve(CurveTemplates.Balanced.Curve, FanCurveConfig.DefaultCurve()));
        Check("Match(静音) = quiet", CurveTemplates.Match(CurveTemplates.Get("quiet").Curve) == "quiet");
        Check("Match(均衡) = balanced", CurveTemplates.Match(CurveTemplates.Balanced.Curve) == "balanced");
        Check("Match(性能) = performance", CurveTemplates.Match(CurveTemplates.Get("performance").Curve) == "performance");

        var modified = CurveTemplates.Balanced.CloneCurve();
        modified[2].Percent += 5;
        Check("修改后的曲线 Match = null", CurveTemplates.Match(modified) is null);

        var nanCurve = CurveTemplates.Balanced.CloneCurve();
        nanCurve[0].Percent = float.NaN;
        Check("含 NaN 的曲线不匹配模板", CurveTemplates.Match(nanCurve) is null);

        Check("未知模板 Id 回退到均衡", CurveTemplates.Get("no-such-template").Id == "balanced");
    }

    private static void TestScenes()
    {
        Section("内置场景");
        Check("场景数量 = 3", ScenePresets.All.Count == 3, $"{ScenePresets.All.Count}");
        Check("场景 Id 唯一", ScenePresets.All.Select(s => s.Id).Distinct().Count() == 3);
        Check("场景与模板同名（感知一一对应）",
            ScenePresets.All.All(s => s.Template.Name == s.Name));
        Check("保留名 静音", ScenePresets.IsReservedName("静音"));
        Check("保留名 BIOS 默认", ScenePresets.IsReservedName("BIOS 默认"));
        Check("普通名字不是保留名", !ScenePresets.IsReservedName("夜间安静"));
    }

    private static void TestCaptureAndApplyProfile()
    {
        Section("配置快照 / 应用");

        var config = new AtlasConfig();
        config.Fans.Add(new FanCurveConfig
        {
            FanId = "fan-1",
            FanName = "Fan #1",
            CustomName = "前风扇",
            Mode = FanMode.Curve,
            SourceId = "cpu-package",
            Curve = CurveTemplates.CloneCurve("quiet"),
        });
        config.Fans.Add(new FanCurveConfig
        {
            FanId = "fan-2",
            FanName = "Fan #2",
            Mode = FanMode.Fixed,
            FixedPercent = 35,
            SourceId = "gpu-core",
            Curve = CurveTemplates.CloneCurve("performance"),
        });

        var profile = FanProfileService.CaptureProfile(config, "  夜间安静  ");
        Check("保存时名称被 trim", profile.Name == "夜间安静");
        Check("快照包含 2 个通道", profile.Fans.Count == 2);
        Check("配置快照不含 CustomName/Protected（仅热设置）",
            profile.Fans.All(f => f.Settings is not null));

        // 模拟用户改动
        config.Fans[0].Mode = FanMode.Fixed;
        config.Fans[0].FixedPercent = 77;
        config.Fans[1].SourceId = "changed";
        config.ActiveProfileId = profile.Id;
        config.StateFingerprint = "stale";

        var result = FanProfileService.ApplyProfile(config, profile);
        Check("应用配置：通道数 2", result.AppliedChannels == 2, result.Summary);
        Check("应用后 fan1 恢复曲线模式", config.Fans[0].Mode == FanMode.Curve);
        Check("应用后 fan1 恢复静音曲线", SameCurve(config.Fans[0].Curve, CurveTemplates.Get("quiet").Curve));
        Check("应用后 fan1 备注名保留", config.Fans[0].CustomName == "前风扇");
        Check("应用后 fan2 恢复固定 PWM", config.Fans[1].Mode == FanMode.Fixed && Math.Abs(config.Fans[1].FixedPercent - 35) < 0.01f);
        Check("应用后活动来源 = 用户配置", config.ActiveProfileId == profile.Id && config.ActiveSceneId is null);
        Check("应用后指纹基线刷新", config.StateFingerprint == config.CurrentFingerprint());
        Check("应用后不再显示已修改", !config.IsCurrentStateModified());
    }

    private static void TestApplySceneRespectsProtected()
    {
        Section("场景应用与锁定通道");

        var config = new AtlasConfig();
        config.Fans.Add(new FanCurveConfig { FanId = "case-1", FanName = "Fan #1", Mode = FanMode.Fixed, FixedPercent = 60 });
        config.Fans.Add(new FanCurveConfig
        {
            FanId = "pump",
            FanName = "Fan #7",
            Protected = true,
            Mode = FanMode.Fixed,
            FixedPercent = 90,
            Curve = CurveTemplates.CloneCurve("performance"),
        });

        var result = FanProfileService.ApplyScene(config, ScenePresets.Find(ScenePresets.QuietId)!);
        Check("场景应用：1 个通道", result.AppliedChannels == 1, result.Summary);
        Check("场景应用：跳过 1 个锁定通道", result.SkippedProtected == 1);
        Check("普通通道切换为曲线", config.Fans[0].Mode == FanMode.Curve);
        Check("普通通道套用同名模板", SameCurve(config.Fans[0].Curve, CurveTemplates.Get("quiet").Curve));
        Check("锁定通道保持固定 PWM 90%",
            config.Fans[1].Mode == FanMode.Fixed && Math.Abs(config.Fans[1].FixedPercent - 90) < 0.01f);
        Check("锁定通道保持原曲线",
            SameCurve(config.Fans[1].Curve, CurveTemplates.Get("performance").Curve));
        Check("场景来源指向内置场景",
            config.ActiveSceneId == ScenePresets.QuietId && config.ActiveProfileId is null);

        // 用户改了一个通道 → 整体显示「已修改」
        config.Fans[0].Curve[0].Percent = 25;
        Check("场景后手工修改 → 已修改", config.IsCurrentStateModified());
    }

    private static void TestFingerprint()
    {
        Section("状态指纹");

        var a = new AtlasConfig();
        a.Fans.Add(new FanCurveConfig { FanId = "a", Mode = FanMode.Curve, SourceId = "cpu", Curve = CurveTemplates.CloneCurve("balanced") });
        a.Fans.Add(new FanCurveConfig { FanId = "b", Mode = FanMode.Fixed, FixedPercent = 40 });

        var b = new AtlasConfig();
        b.Fans.Add(new FanCurveConfig { FanId = "b", Mode = FanMode.Fixed, FixedPercent = 40 });
        b.Fans.Add(new FanCurveConfig { FanId = "a", Mode = FanMode.Curve, SourceId = "cpu", Curve = CurveTemplates.CloneCurve("balanced") });

        Check("通道顺序不影响指纹", a.CurrentFingerprint() == b.CurrentFingerprint());

        b.Fans[0].FixedPercent = 41;
        Check("固定 PWM 变化会改变指纹", a.CurrentFingerprint() != b.CurrentFingerprint());

        b.Fans[0].FixedPercent = 40;
        b.Fans[0].CustomName = "改个备注";
        b.Fans[1].Protected = true;
        Check("备注名/锁定不改变热设置指纹", a.CurrentFingerprint() == b.CurrentFingerprint());
    }

    private static void TestJsonRoundTrip()
    {
        Section("JSON 持久化");

        var config = new AtlasConfig();
        config.Fans.Add(new FanCurveConfig
        {
            FanId = "fan-1", FanName = "Fan #1", Mode = FanMode.Curve,
            SourceId = "cpu", Curve = CurveTemplates.CloneCurve("performance"),
        });
        var profile = FanProfileService.CaptureProfile(config, "烤机");
        config.Profiles.Add(profile);
        config.MarkApplied(null, profile.Id);

        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        var loaded = JsonSerializer.Deserialize<AtlasConfig>(json)!;
        loaded.Normalize();

        Check("配置数量回读", loaded.Profiles.Count == 1);
        Check("配置名称回读", loaded.Profiles[0].Name == "烤机");
        Check("活动配置回读", loaded.ActiveProfileId == profile.Id);
        Check("快照曲线回读一致",
            SameCurve(loaded.Profiles[0].Fans[0].Settings.Curve, CurveTemplates.Get("performance").Curve));
        Check("指纹回读一致", loaded.StateFingerprint == loaded.CurrentFingerprint());
        Check("回读后可正常再次应用", FanProfileService.ApplyProfile(loaded, loaded.Profiles[0]).AppliedChannels == 1);
    }

    private static void TestLegacyJsonNormalization()
    {
        Section("旧配置兼容");

        const string legacy = """
        {
          "PollMs": 1000,
          "Fans": [
            { "FanId": "fan-1", "FanName": "Fan #1", "Enabled": true }
          ]
        }
        """;

        var config = JsonSerializer.Deserialize<AtlasConfig>(legacy)!;
        foreach (var fan in config.Fans)
            if (fan.Mode == FanMode.Disabled && fan.Enabled) fan.Mode = FanMode.Curve;
        config.Normalize();

        Check("旧配置 Enabled → Curve", config.Fans[0].Mode == FanMode.Curve);
        Check("旧配置没有 Profiles 也不报错", config.Profiles.Count == 0);
        Check("旧配置曲线为空时回填默认", config.Fans[0].Curve.Count > 0);
        Check("旧配置无来源时不显示已修改", !config.IsCurrentStateModified());
    }

    private static void TestNullTolerantNormalization()
    {
        Section("手工 JSON / 空值容错");

        const string hostile = """
        {
          "Fans": [ null, { "FanId": "fan-1", "FanName": "Fan #1", "Curve": [ null, { "Temp": 30, "Percent": 30 } ] } ],
          "Profiles": [
            { "Id": "p1", "Name": "A", "Fans": [ null, { "FanId": "fan-1", "Settings": null } ] },
            { "Id": "p1", "Name": "重复 Id" },
            { "Id": "", "Name": "空 Id" }
          ],
          "ActiveProfileId": "missing-profile"
        }
        """;

        var config = JsonSerializer.Deserialize<AtlasConfig>(hostile)!;
        var normalizedWithoutThrow = true;
        try { config.Normalize(); } catch { normalizedWithoutThrow = false; }

        Check("Normalize 不因 null 项抛异常", normalizedWithoutThrow);
        Check("Fans 里的 null 项被清理", config.Fans.Count == 1);
        Check("Curve 里的 null 点被清理", config.Fans[0].Curve.Count == 1);
        Check("重复/空 Id 配置被清理", config.Profiles.Count == 1);
        Check("配置内 null 风扇项被清理", config.Profiles[0].Fans.Count == 1);
        Check("null Settings 被补齐", config.Profiles[0].Fans[0].Settings is not null);
        Check("悬空活动配置被清空", config.ActiveProfileId is null);
        Check("指纹在清理后可用", !string.IsNullOrEmpty(config.CurrentFingerprint()));

        // 两个来源同时存在时，优先有效的「我的配置」
        config.ActiveSceneId = ScenePresets.QuietId;
        config.ActiveProfileId = config.Profiles[0].Id;
        config.Normalize();
        Check("两个来源互斥（优先用户配置）",
            config.ActiveProfileId == config.Profiles[0].Id && config.ActiveSceneId is null);
    }

    private static void TestBaselineAndEmptyApply()
    {
        Section("基线指纹与空应用");

        // 全新安装：Load 先 Normalize，BuildRows 之后才补默认温度源并建立基线。
        // 此时不能误报「已修改」。
        var fresh = new AtlasConfig();
        fresh.Normalize();
        fresh.Fans.Add(new FanCurveConfig
        {
            FanId = "new-channel",
            FanName = "Fan #1",
            SourceId = "cpu-package", // BuildRows 补的默认温度源
        });
        fresh.EnsureStateBaseline();
        Check("全新安装补默认温度源后建立基线不误报已修改", !fresh.IsCurrentStateModified());

        var config = new AtlasConfig();
        config.Fans.Add(new FanCurveConfig { FanId = "fan-1", Mode = FanMode.Disabled });
        config.EnsureStateBaseline();
        Check("建立基线后不算已修改", !config.IsCurrentStateModified());

        config.Fans[0].Mode = FanMode.Fixed;
        config.Fans[0].FixedPercent = 45;
        Check("未归档方案的手工修改可被识别", config.IsCurrentStateModified());

        // 全部锁定：应用配置不能假装成功
        var locked = new AtlasConfig();
        locked.Fans.Add(new FanCurveConfig { FanId = "pump", Protected = true, Mode = FanMode.Fixed, FixedPercent = 90 });
        var profile = FanProfileService.CaptureProfile(locked, "水泵专用");
        locked.Profiles.Add(profile);
        locked.EnsureStateBaseline();
        var lockedResult = FanProfileService.ApplyProfile(locked, profile);
        Check("全锁定配置只跳过不应用", lockedResult.AppliedChannels == 0 && lockedResult.SkippedProtected == 1);
        Check("全锁定时不改变当前来源", locked.ActiveProfileId is null);

        // 没有任何通道：应用场景不能假装成功
        var empty = new AtlasConfig();
        empty.EnsureStateBaseline();
        var emptyResult = FanProfileService.ApplyScene(empty, ScenePresets.Find(ScenePresets.QuietId)!);
        Check("无通道时场景应用 0 个", emptyResult.AppliedChannels == 0);
        Check("无通道时不改变当前来源", empty.ActiveSceneId is null);
    }

    private static void TestNameValidation()
    {
        Section("配置命名校验");

        var config = new AtlasConfig();
        var existing = FanProfileService.CaptureProfile(config, "夜间安静");
        config.Profiles.Add(existing);

        Check("空名称拒绝", !FanProfileService.ValidateProfileName(config, " ").IsValid);
        Check("超长名称拒绝",
            !FanProfileService.ValidateProfileName(config, new string('a', FanProfileService.MaxProfileNameLength + 1)).IsValid);
        var reserved = FanProfileService.ValidateProfileName(config, "静音");
        Check("内置场景名作为配置名被拒绝", !reserved.IsValid);
        Check("拒绝提示说明原因", reserved.Message.Contains("内置场景"), reserved.Message);
        Check("同名配置拒绝", !FanProfileService.ValidateProfileName(config, "夜间安静").IsValid);
        Check("改名时排除自身", FanProfileService.ValidateProfileName(config, "夜间安静", existing.Id).IsValid);
        var ok = FanProfileService.ValidateProfileName(config, "  游戏 · 夜  ");
        Check("合法名称 trim 后通过", ok.IsValid && ok.Normalized == "游戏 · 夜", ok.Message);

        var zeroWidth = FanProfileService.ValidateProfileName(config, "静音\u200B");
        Check("零宽字符不能绕过保留名", !zeroWidth.IsValid, zeroWidth.Message);
        Check("不可见字符提示明确", zeroWidth.Message.Contains("不可见"), zeroWidth.Message);
        Check("BIOS 双空格仍识别为保留名",
            !FanProfileService.ValidateProfileName(config, "BIOS  默认").IsValid);
        Check("连续空格在保存时被折叠",
            FanProfileService.ValidateProfileName(config, "夜间  安静").Normalized == "夜间 安静");
        Check("24 个中文字符可保存",
            FanProfileService.ValidateProfileName(config, new string('风', 24)).IsValid);
    }

    private static void TestDeleteActiveProfile()
    {
        Section("删除配置");

        var config = new AtlasConfig();
        var profile = FanProfileService.CaptureProfile(config, "临时");
        config.Profiles.Add(profile);
        config.MarkApplied(null, profile.Id);

        Check("删除成功", FanProfileService.DeleteProfile(config, profile.Id));
        Check("删除后配置列表为空", config.Profiles.Count == 0);
        Check("删除活动配置后清空来源", config.ActiveProfileId is null);
        Check("删除活动配置后当前设置仍为干净未归档",
            !config.IsCurrentStateModified() && config.StateFingerprint == config.CurrentFingerprint());
        Check("重复删除返回 false", !FanProfileService.DeleteProfile(config, profile.Id));
    }

    private static void TestUpdateProfile()
    {
        Section("更新配置");

        var config = new AtlasConfig();
        config.Fans.Add(new FanCurveConfig { FanId = "fan-1", Mode = FanMode.Fixed, FixedPercent = 30 });
        var profile = FanProfileService.CaptureProfile(config, "白天");
        config.Profiles.Add(profile);

        config.Fans[0].FixedPercent = 70;
        FanProfileService.UpdateProfile(config, profile);

        Check("更新后快照保存新值",
            Math.Abs(profile.Fans[0].Settings.FixedPercent - 70) < 0.01f);
        Check("更新后来源指向该配置", config.ActiveProfileId == profile.Id);
        Check("更新后指纹基线刷新", !config.IsCurrentStateModified());
    }

    private static void TestMissingAndUntouchedChannels()
    {
        Section("通道增删匹配");

        var config = new AtlasConfig();
        config.Fans.Add(new FanCurveConfig { FanId = "fan-1", FanName = "Fan #1" });
        config.Fans.Add(new FanCurveConfig { FanId = "fan-2", FanName = "Fan #2" });

        var profile = new SavedProfile
        {
            Id = "profile-x",
            Name = "旧机器",
            Fans =
            {
                new FanProfileEntry
                {
                    FanId = "fan-1",
                    FanName = "Fan #1",
                    Settings = new FanSettingsSnapshot { Mode = FanMode.Fixed, FixedPercent = 55 },
                },
                new FanProfileEntry
                {
                    FanId = "ghost",
                    FanName = "已移除的通道",
                    Settings = new FanSettingsSnapshot { Mode = FanMode.Curve },
                },
            },
        };
        config.Profiles.Add(profile);

        var result = FanProfileService.ApplyProfile(config, profile);
        Check("匹配上的通道被应用", result.AppliedChannels == 1);
        Check("配置里多出的通道计数", result.MissingChannels == 1);
        Check("当前机器多出的通道保持原样", result.UntouchedChannels == 1);
        Check("新通道设置未被改动", config.Fans[1].Mode == FanMode.Disabled);
    }

    private static void TestRestoreToBiosDefaults()
    {
        Section("全部交由 BIOS 控制");

        var config = new AtlasConfig();
        config.Fans.Add(new FanCurveConfig
        {
            FanId = "fan-1", FanName = "Fan #1", Mode = FanMode.Curve,
            SourceId = "cpu", Curve = CurveTemplates.CloneCurve("quiet"),
        });
        config.Fans.Add(new FanCurveConfig
        {
            FanId = "pump", FanName = "Fan #7", Protected = true,
            Mode = FanMode.Fixed, FixedPercent = 90,
        });
        var profile = FanProfileService.CaptureProfile(config, "夜间安静");
        config.Profiles.Add(profile);
        FanProfileService.ApplyProfile(config, profile);

        var active = FanProfileService.RestoreToBiosDefaults(config);
        Check("恢复后返回当前「我的配置」", active?.Id == profile.Id);
        Check("恢复后所有通道 = BIOS 默认",
            config.Fans.All(f => f.Mode == FanMode.Disabled));
        Check("恢复后仍关联原配置（不新建、不降级为未归档）",
            config.ActiveProfileId == profile.Id && config.ActiveSceneId is null);
        Check("恢复后显示「已修改」", config.IsCurrentStateModified());
        Check("恢复不直接改写配置快照", profile.Fans[0].Settings.Mode == FanMode.Curve);

        // 用户点「保存」→ 写回同一份配置，不新建
        FanProfileService.UpdateProfile(config, profile);
        Check("保存后仍只有 1 份配置", config.Profiles.Count == 1);
        Check("保存后快照 = BIOS 默认",
            profile.Fans.All(e => e.Settings.Mode == FanMode.Disabled));
        Check("保存后不再显示「已修改」", !config.IsCurrentStateModified());

        // 内置场景只读：恢复后回到未归档，且不算用户修改
        var sceneConfig = new AtlasConfig();
        sceneConfig.Fans.Add(new FanCurveConfig { FanId = "fan-1", FanName = "Fan #1" });
        FanProfileService.ApplyScene(sceneConfig, ScenePresets.Find(ScenePresets.QuietId)!);

        var noProfile = FanProfileService.RestoreToBiosDefaults(sceneConfig);
        Check("场景来源恢复后清空（只读场景无法写回）",
            noProfile is null && sceneConfig.ActiveSceneId is null && sceneConfig.ActiveProfileId is null);
        Check("场景恢复后不算「已修改」", !sceneConfig.IsCurrentStateModified());
    }

    private static void TestEditHistory()
    {
        Section("撤销 / 重做历史");

        var config = new AtlasConfig();
        config.Fans.Add(new FanCurveConfig
        {
            FanId = "fan-1", FanName = "Fan #1", Mode = FanMode.Fixed, FixedPercent = 40,
            Protected = true, CustomName = "前风扇",
        });
        config.Fans.Add(new FanCurveConfig
        {
            FanId = "fan-2", FanName = "Fan #2", Mode = FanMode.Curve,
            Curve = CurveTemplates.CloneCurve("quiet"),
        });

        var history = new FanEditHistory();
        history.Reset(config.Fans);
        Check("初始状态不可撤销/重做", !history.CanUndo && !history.CanRedo);

        config.Fans[0].FixedPercent = 70;
        Check("提交一次操作", history.Record(config.Fans));
        Check("提交后可撤销", history.CanUndo && history.UndoCount == 1);

        Check("撤销返回 true", history.Undo(config.Fans));
        Check("撤销恢复旧值", Math.Abs(config.Fans[0].FixedPercent - 40) < 0.01f);
        Check("撤销后可重做", history.CanRedo && history.RedoCount == 1);
        Check("没有更多可撤销", !history.CanUndo);

        Check("重做返回 true", history.Redo(config.Fans));
        Check("重做恢复新值", Math.Abs(config.Fans[0].FixedPercent - 70) < 0.01f);
        Check("重做到底后不可再重做", !history.CanRedo);

        Check("状态未变化时不产生空步", !history.Record(config.Fans));

        // 撤销后做新操作 → 重做分支被截断
        history.Undo(config.Fans);
        config.Fans[1].Curve = CurveTemplates.CloneCurve("performance");
        Check("撤销后新操作被提交", history.Record(config.Fans));
        Check("撤销后新操作截断重做分支", !history.CanRedo);

        // 曲线也能被撤销/重做
        Check("撤销曲线变更", history.Undo(config.Fans));
        Check("撤销后曲线回到静音",
            SameCurve(config.Fans[1].Curve, CurveTemplates.Get("quiet").Curve));

        // 锁定 / 备注名不参与历史
        var guarded = new AtlasConfig();
        guarded.Fans.Add(new FanCurveConfig
        {
            FanId = "pump", FanName = "Fan #7", Mode = FanMode.Fixed, FixedPercent = 80,
            Protected = true, CustomName = "水泵",
        });
        var guardedHistory = new FanEditHistory();
        guardedHistory.Reset(guarded.Fans);
        guarded.Fans[0].Mode = FanMode.Curve;
        guardedHistory.Record(guarded.Fans);
        guarded.Fans[0].Protected = false;
        guarded.Fans[0].CustomName = "改名后";
        guardedHistory.Undo(guarded.Fans);
        Check("撤销恢复模式", guarded.Fans[0].Mode == FanMode.Fixed);
        Check("撤销不改锁定状态", !guarded.Fans[0].Protected);
        Check("撤销不改备注名", guarded.Fans[0].CustomName == "改名后");

        // 上限：超出 MaxSteps 丢最旧
        var capped = new AtlasConfig();
        capped.Fans.Add(new FanCurveConfig { FanId = "f", Mode = FanMode.Fixed, FixedPercent = 0 });
        var cappedHistory = new FanEditHistory();
        cappedHistory.Reset(capped.Fans);
        for (var i = 1; i <= FanEditHistory.MaxSteps + 10; i++)
        {
            capped.Fans[0].FixedPercent = i;
            cappedHistory.Record(capped.Fans);
        }
        Check("历史步数不超过上限", cappedHistory.StepCount == FanEditHistory.MaxSteps,
            $"{cappedHistory.StepCount}");
        Check("上限内仍可撤销", cappedHistory.UndoCount == FanEditHistory.MaxSteps - 1);

        // 保存后清空历史（App 层策略）：只剩基线，撤销/重做都不可用
        cappedHistory.Reset(capped.Fans);
        Check("Reset 后不可撤销/重做", !cappedHistory.CanUndo && !cappedHistory.CanRedo);
    }

    private static void Section(string name) => Console.WriteLine($"\n[{name}]");

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok)
        {
            _passed++;
            Console.WriteLine($"  PASS  {name}");
            return;
        }

        _failed++;
        Console.WriteLine($"  FAIL  {name}{(string.IsNullOrEmpty(detail) ? "" : $"  ({detail})")}");
    }

    private static bool SameCurve(IReadOnlyList<CurvePoint> a, IReadOnlyList<CurvePoint> b)
    {
        if (a.Count != b.Count) return false;
        var left = a.OrderBy(p => p.Temp).ToList();
        var right = b.OrderBy(p => p.Temp).ToList();
        return left.Zip(right).All(pair =>
            Math.Abs(pair.First.Temp - pair.Second.Temp) < 0.05f &&
            Math.Abs(pair.First.Percent - pair.Second.Percent) < 0.05f);
    }
}
