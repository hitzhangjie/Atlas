using System.Globalization;
using System.Text;

namespace Atlas.Core;

/// <summary>
/// 当前方案的撤销 / 重做历史。
///
/// 模型：线性快照列表 + 游标。每个「用户操作」提交一份完整的通道热设置快照
/// （模式 / 固定 PWM / 温度源 / 上下限 / 曲线），撤销与重做只移动游标；
/// 新操作会截断游标之后的重做分支。最多保留 <see cref="MaxSteps"/> 步（丢最旧）。
///
/// 刻意不记录备注名与锁定：它们是通道身份 / 安全属性，不随撤销往返，
/// 也本来就不参与「当前方案是否已修改」的指纹。撤销一次模式修改不应该
/// 悄悄把水泵解锁，也不应该改掉用户起的名字。
///
/// 纯逻辑、无 UI / 无硬件依赖，可自测。只在 UI 线程使用。
/// </summary>
public sealed class FanEditHistory
{
    /// <summary>历史步数上限：正常使用不会命中；超出后丢最旧的一步。</summary>
    public const int MaxSteps = 1024;

    private sealed record FanEditState(string FanId, FanSettingsSnapshot Settings);

    private sealed class Entry
    {
        public string Signature { get; init; } = "";
        public List<FanEditState> Fans { get; init; } = new();
    }

    private readonly List<Entry> _entries = new();
    private int _cursor = -1;

    public bool CanUndo => _cursor > 0;
    public bool CanRedo => _cursor >= 0 && _cursor < _entries.Count - 1;

    /// <summary>还可以撤销的步数（游标之前的快照数量）。</summary>
    public int UndoCount => Math.Max(0, _cursor);

    /// <summary>还可以重做的步数（游标之后的快照数量）。</summary>
    public int RedoCount => _entries.Count == 0 ? 0 : Math.Max(0, _entries.Count - 1 - _cursor);

    /// <summary>当前保留的总快照数（含基线）。</summary>
    public int StepCount => _entries.Count;

    /// <summary>清空历史，把当前设置记为唯一基线（撤销 / 重做都不可用）。</summary>
    public void Reset(IEnumerable<FanCurveConfig> fans)
    {
        _entries.Clear();
        _entries.Add(Capture(fans));
        _cursor = 0;
    }

    /// <summary>
    /// 把当前设置提交为一步。与当前快照一致时返回 false（不产生空步）；
    /// 否则截断游标之后的重做分支并追加，然后按上限淘汰最旧项。
    /// </summary>
    public bool Record(IEnumerable<FanCurveConfig> fans)
    {
        var entry = Capture(fans);

        if (_cursor >= 0 && string.Equals(entry.Signature, _entries[_cursor].Signature, StringComparison.Ordinal))
            return false;

        if (_cursor < 0)
        {
            _entries.Clear();
            _entries.Add(entry);
            _cursor = 0;
            return true;
        }

        if (_cursor < _entries.Count - 1)
            _entries.RemoveRange(_cursor + 1, _entries.Count - 1 - _cursor);

        _entries.Add(entry);
        _cursor = _entries.Count - 1;

        while (_entries.Count > MaxSteps)
        {
            _entries.RemoveAt(0);
            _cursor--;
        }

        return true;
    }

    /// <summary>撤销一步并把快照写回配置；没有可撤销的步骤时返回 false。</summary>
    public bool Undo(ICollection<FanCurveConfig> fans)
    {
        if (_cursor <= 0) return false;

        _cursor--;
        Apply(_entries[_cursor], fans);
        return true;
    }

    /// <summary>重做一步并把快照写回配置；没有可重做的步骤时返回 false。</summary>
    public bool Redo(ICollection<FanCurveConfig> fans)
    {
        if (!CanRedo) return false;

        _cursor++;
        Apply(_entries[_cursor], fans);
        return true;
    }

    private static void Apply(Entry entry, ICollection<FanCurveConfig> fans)
    {
        var byId = fans
            .Where(fan => fan is not null && !string.IsNullOrEmpty(fan.FanId))
            .GroupBy(fan => fan.FanId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        foreach (var state in entry.Fans)
        {
            if (byId.TryGetValue(state.FanId, out var fan))
                state.Settings.ApplyTo(fan);
        }
    }

    private static Entry Capture(IEnumerable<FanCurveConfig> fans)
    {
        var states = new List<FanEditState>();
        foreach (var fan in (fans ?? Enumerable.Empty<FanCurveConfig>())
                 .Where(f => f is not null)
                 .OrderBy(f => f.FanId, StringComparer.Ordinal))
        {
            states.Add(new FanEditState(fan.FanId ?? "", FanSettingsSnapshot.Capture(fan)));
        }

        return new Entry { Signature = BuildSignature(states), Fans = states };
    }

    /// <summary>
    /// 快照的稳定签名：与曲线点顺序无关、固定精度，避免浮点抖动或集合顺序
    /// 让同一次操作被误判成新的一步。
    /// </summary>
    private static string BuildSignature(IReadOnlyList<FanEditState> states)
    {
        var builder = new StringBuilder();

        foreach (var state in states)
        {
            var settings = state.Settings;
            builder.Append(state.FanId).Append('|')
                .Append((int)settings.Mode).Append('|')
                .Append(Format(settings.FixedPercent)).Append('|')
                .Append(settings.SourceId ?? "").Append('|')
                .Append(Format(settings.MinPercent)).Append('|')
                .Append(Format(settings.MaxPercent)).Append('|');

            foreach (var point in (settings.Curve ?? new List<CurvePoint>())
                         .Where(p => p is not null)
                         .OrderBy(p => p.Temp))
            {
                builder.Append(Format(point.Temp)).Append(':').Append(Format(point.Percent)).Append(',');
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static string Format(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
