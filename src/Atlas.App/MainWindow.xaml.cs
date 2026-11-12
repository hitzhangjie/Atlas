using Atlas.App.Controls;
using Atlas.Core;
using LibreHardwareMonitor.Hardware;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Atlas.App;

public partial class MainWindow : Window
{
    private static readonly DateTime ProcessStartTime = Process.GetCurrentProcess().StartTime;

    private readonly HardwareService _hardware = new();
    private readonly MemoryService _memory = new();
    private readonly ObservableCollection<FanRow> _rows = new();
    private readonly DispatcherTimer _timer;
    private readonly System.Threading.Timer _watchdog;
    private readonly Stopwatch _startup = Stopwatch.StartNew();

    private AtlasConfig _config = new();
    private FanEngine? _engine;
    private ICollectionView? _fanView;
    private ViewRefreshGate? _viewGate;
    private HashSet<FanRow>? _visibleRowsSnapshot;
    private bool _hideZeroRpm;
    private bool _initialized;
    private bool _closing;
    private string _lastSceneBarSignature = "\0";

    /// <summary>场景/配置切换确认框打开期间暂停每秒的 SceneBar 刷新，避免把用户刚选的新项回退掉。</summary>
    private bool _sceneSwitchPromptOpen;

    // 「内置场景只读」编辑拦截：防止滑块拖动/键盘连续修改时反复弹框
    private bool _presetEditPromptOpen;
    private DateTime _presetEditPromptSuppressUntil;

    // Atlas 自身在应用场景/配置、全部交由 BIOS 控制时也会改通道设置，必须绕过只读闸门
    private bool _suppressPresetEditGuard;

    // 撤销 / 重做：历史只记录当前方案的热设置（模式/固定 PWM/温度源/上下限/曲线），
    // 每个「用户操作」提交一步；程序化批量改写（应用场景、恢复快照等）期间用 _suppressHistory 屏蔽。
    private readonly FanEditHistory _history = new();
    private readonly DispatcherTimer _historyCommitTimer;
    private bool _suppressHistory;

    // 底部状态栏：操作反馈优先显示几秒，空闲时只显示稳定的接管状态
    private DateTime _statusHoldUntil;
    private bool _updatingDefaultStatus;

    public MainWindow()
    {
        InitializeComponent();
        WindowChromeHelper.Enable(this);
        StartupMark("InitializeComponent");

        // 撤销 / 重做：滑块拖动会连续触发属性变化，安静 250ms 或鼠标释放后合并成「一步」。
        // 先于鼠标事件订阅创建，避免构造期间事件早到导致空引用。
        _historyCommitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _historyCommitTimer.Tick += (_, _) => FlushHistoryOperation();

        // 下拉列表 / 单元格编辑 / 拖拽期间，把「会重建表格行容器」的过滤刷新延后，避免打断用户操作
        FanGrid.BeginningEdit += (_, _) => _viewGate?.NotifyEditStarted();
        FanGrid.CellEditEnding += (_, _) =>
        {
            _viewGate?.NotifyEditEnded();
            QueueFlushPendingViewRefresh();
        };
        LostMouseCapture += (_, _) =>
        {
            QueueFlushPendingViewRefresh();
            FlushHistoryOperation();
        };
        PreviewMouseUp += (_, _) =>
        {
            QueueFlushPendingViewRefresh();
            FlushHistoryOperation();
        };

        _config = AtlasConfig.Load();
        StartupMark("config loaded");

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(500, _config.PollMs)),
        };
        _timer.Tick += OnTick;

        // 看门狗：UI 线程卡住超过 8 秒就恢复 BIOS 默认，避免风扇停在异常 PWM
        _watchdog = new System.Threading.Timer(WatchdogTick, null, 2000, 2000);

        Closing += OnClosing;
        Loaded += async (_, _) => await InitializeAsync();

        // SceneBar 只负责展示，所有业务动作都回到这里处理
        SceneBarControl.ChoiceSelected += OnSceneBarChoiceSelected;
        SceneBarControl.SaveProfileRequested += OnSaveProfileRequested;
        SceneBarControl.SaveAsProfileRequested += OnSaveAsProfileRequested;
        SceneBarControl.RenameProfileRequested += OnRenameProfileRequested;
        SceneBarControl.DeleteProfileRequested += OnDeleteProfileRequested;

        // 任何地方写的操作反馈都在状态栏停留几秒，之后回到稳定的接管状态
        // （TextBlock 没有 TextChanged 事件，用依赖属性描述符监听 Text 变化）
        DependencyPropertyDescriptor
            .FromProperty(TextBlock.TextProperty, typeof(TextBlock))
            .AddValueChanged(StatusText, (_, _) =>
            {
                if (!_updatingDefaultStatus)
                    _statusHoldUntil = DateTime.UtcNow.AddSeconds(5);
            });

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) LogCrash(ex);
            SafeRestore();
        };
        Dispatcher.UnhandledException += (_, args) =>
        {
            LogCrash(args.Exception);
            args.Handled = true; // UI 线程异常不再直接终止进程
            SafeRestore();
            StatusText.Text = $"已交由 BIOS 控制（UI 异常：{args.Exception.Message}）";
        };

        SetInteractionEnabled(false);
        StatusText.Text = "正在初始化硬件…";
        RefreshSceneBar(); // 先以只读状态把内置场景/我的配置展示出来
        StartupMark("window constructed");
    }

    /// <summary>硬件初始化放到后台线程，窗口先出来，避免 5–8 秒白屏等待。</summary>
    private async Task InitializeAsync()
    {
        try
        {
            var hwWatch = Stopwatch.StartNew();
            await Task.Run(() =>
            {
                _hardware.Open();
                StartupMark($"hardware.Open done ({hwWatch.ElapsedMilliseconds}ms)");

                _hardware.Update();
                StartupMark($"hardware.Update done ({hwWatch.ElapsedMilliseconds}ms)");

                _hardware.RestoreAll();
                StartupMark($"hardware.RestoreAll done ({hwWatch.ElapsedMilliseconds}ms)");
            });

            var rowWatch = Stopwatch.StartNew();
            var normalizedSources = BuildRows();
            // 基线必须在通道枚举 + 默认温度源补齐之后建立：全新安装/新增通道不算用户修改，
            // 否则启动就显示「已修改」，还会在首次套用场景时弹无意义确认。
            _config.EnsureStateBaseline();
            // 升级后温度源收敛（如显卡强制 GPU Core、细节源回落 CPU Package）不是用户修改，
            // 不能因为归一化而让状态显示「已修改」。
            if (normalizedSources > 0) _config.StateFingerprint = _config.CurrentFingerprint();
            _fanView = CollectionViewSource.GetDefaultView(_rows);
            _viewGate = new ViewRefreshGate(_fanView, IsAnyDropDownOpen);
            FanGrid.ItemsSource = _fanView;
            StartupMark($"rows built ({rowWatch.ElapsedMilliseconds}ms)");

            _engine = new FanEngine(_hardware, _config);
            _timer.Start();
            _initialized = true;
            // 撤销历史从「当前方案」开始：启动时没有可撤销的编辑
            _history.Reset(_config.Fans);
            UpdateHistoryUi();
            SetInteractionEnabled(true);
            RefreshSceneBar();
            StartupMark($"init complete, process→ready = {(DateTime.Now - ProcessStartTime).TotalMilliseconds:0}ms");

            StatusText.Text = "就绪";
            OnTick(null, EventArgs.Empty);

            // 内存/DIMM 温度初始化很慢（约 4.5s），后台加载，不阻塞风扇控制
            _ = LoadMemoryAsync();
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            StatusText.Text = $"硬件初始化失败：{ex.Message}";
            MessageBox.Show(
                $"打开硬件失败：{ex.Message}\n\n请确认：\n1) 以管理员身份运行\n2) 已安装 PawnIO（https://pawnio.eu）",
                "Atlas", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task LoadMemoryAsync()
    {
        try
        {
            await Task.Run(_memory.Start);
            StartupMark("memory ready");
        }
        catch (Exception ex)
        {
            LogCrash(ex);
        }
    }

    private void SetInteractionEnabled(bool enabled)
    {
        FanGrid.IsEnabled = enabled;
        HideZeroButton.IsEnabled = enabled;
        CopyCurveButton.IsEnabled = enabled;
        RestoreButton.IsEnabled = enabled;
        SceneBarControl.Interactable = enabled;
    }

    /// <summary>CPU 封装/核心温度（Intel CPU Package / Core Max；AMD Tctl / Tdie）。</summary>
    private static bool IsCpuTemperature(TemperatureSource source)
    {
        var name = source.DisplayName;
        return name.Contains("CPU Package", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Core Max", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Tctl", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Tdie", StringComparison.OrdinalIgnoreCase)
               || name.Contains("CPU Core", StringComparison.OrdinalIgnoreCase);
    }

    private static TemperatureSource? FindCpuTemperature(IReadOnlyList<TemperatureSource> sources) =>
        sources.FirstOrDefault(IsCpuTemperature)
        ?? sources.FirstOrDefault(s => !s.DisplayName.Contains("GPU", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 每个风扇可选的温度源：
    /// - 显卡通道：强制使用该显卡自己的 GPU Core（不允许误选 CPU 温度）；
    /// - 主板通道：只提供 CPU Package / GPU Core 两个实用选项，隐藏 P/E 核、热点、显存等细节传感器。
    /// </summary>
    private static List<TemperatureSource> BuildAllowedSources(
        FanChannel fan, IReadOnlyList<TemperatureSource> all,
        TemperatureSource? cpuPackage, TemperatureSource? gpuCore)
    {
        if (fan.IsGpu)
        {
            var perGpu = all
                .Where(s => s.DisplayName.StartsWith(fan.HardwareName + " · ", StringComparison.Ordinal))
                .ToList();

            var core = perGpu.FirstOrDefault(s => s.DisplayName.Contains("GPU Core"));
            if (core != null) return new List<TemperatureSource> { core };
            if (perGpu.Count > 0) return new List<TemperatureSource> { perGpu[0] };
            return gpuCore is null ? new List<TemperatureSource>() : new List<TemperatureSource> { gpuCore };
        }

        var list = new List<TemperatureSource>(2);
        if (cpuPackage != null) list.Add(cpuPackage);
        if (gpuCore != null && list.All(s => s.Id != gpuCore.Id)) list.Add(gpuCore);
        return list;
    }

    /// <summary>建立通道行，返回因温度源收敛而改写的通道数（用于升级后避免误报「已修改」）。</summary>
    private int BuildRows()
    {
        var sources = _hardware.GetTemperatureSources();
        var cpuPackage = FindCpuTemperature(sources);
        var gpuCore = sources.FirstOrDefault(s => s.DisplayName.Contains("GPU Core"));
        var globalFallback = cpuPackage ?? sources.FirstOrDefault();
        var normalizedSources = 0;

        foreach (var fan in _hardware.Fans)
        {
            var cfg = _config.GetOrCreate(fan);
            var allowed = BuildAllowedSources(fan, sources, cpuPackage, gpuCore);

            // 显卡风扇永久绑定 GPU Core；主板风扇只允许 CPU Package / GPU Core，
            // 历史配置里指向 P/E 核、Hotspot 等细节传感器时回落到 CPU Package。
            var forced = fan.IsGpu
                ? allowed.FirstOrDefault()
                : allowed.FirstOrDefault(s => string.Equals(s.Id, cfg.SourceId, StringComparison.Ordinal)) ?? allowed.FirstOrDefault();
            if (forced is not null && !string.Equals(cfg.SourceId, forced.Id, StringComparison.Ordinal))
            {
                cfg.SourceId = forced.Id;
                normalizedSources++;
            }
            else if (string.IsNullOrWhiteSpace(cfg.SourceId) && globalFallback is not null)
            {
                cfg.SourceId = globalFallback.Id;
                normalizedSources++;
            }

            var row = new FanRow(fan, cfg, allowed, canSelectSource: !fan.IsGpu);
            // 兜底拦截：键盘 ↑↓ / PageUp / 滚轮 等绕过鼠标 Preview 的修改路径也会走 setter。
            // 锁定通道同样拦截：内置场景生效期间任何热设置都不应被改动，否则会出现「预设 + 已修改」的矛盾状态。
            row.EditGuard = PresetEditAllowed;
            row.PropertyChanged += OnRowPropertyChanged;
            _rows.Add(row);
        }

        ChannelCountText.Text = $"{_rows.Count} 个通道";
        return normalizedSources;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_closing || !_initialized || _engine is null) return;

        try
        {
            try
            {
                _engine.Tick();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"控制循环异常：{ex.Message}";
            }

            foreach (var row in _rows) row.Refresh();

            // 当前方案状态（内置场景 / 我的配置 / 是否已修改）——SceneBar 自身会做变化检测
            RefreshSceneBar();

            // 只在可见行集合真的变化时才重建视图；否则每秒 Refresh 会打断用户正在操作的下拉列表 / 滑块
            UpdateRowVisibility();
            _viewGate?.FlushPending();

            var cpuPackage = FindTemperature(HardwareType.Cpu, "CPU Package");
            UpdateTempCard(CpuTempText, CpuTempState, CpuTempBar,
                cpuPackage, 75, 88, AccentBrush, "无数据");
            UpdateCpuCoreDetail(cpuPackage);

            // 主指标用核心温度（和驱动 / 游戏内显示一致），热点 / 显存只作为散热参考放在副标题
            var gpuCore = FindGpuTemperature("GPU Core");
            var gpuHotspot = FindGpuTemperature("GPU Hot Spot", "GPU Hotspot");
            var gpuMemory = FindGpuTemperature("GPU Memory Junction", "GPU Memory");
            UpdateTempCard(GpuTempText, GpuTempState, GpuTempBar,
                gpuCore, 78, 88, CyanBrush, "无数据");
            UpdateGpuDetail(gpuCore, gpuHotspot, gpuMemory);

            UpdateTempCard(DimmText, DimmState, DimmBar,
                _memory.MaxDimmTemperature, 65, 80, DimmBrush,
                _memory.IsReady ? "无数据" : "加载中");
            UpdateDimmDetail();

            var controlling = _engine.ActiveChannels > 0;
            StatusDot.Fill = controlling ? GreenBrush : MutedBrush;

            // 空闲时不显示“最后写了哪个通道 → 多少%”这类秒级噪音，只保留稳定摘要
            if (DateTime.UtcNow >= _statusHoldUntil)
            {
                _updatingDefaultStatus = true;
                try
                {
                    StatusText.Text = controlling
                        ? $"正在控制 {_engine.ActiveChannels} 个通道"
                        : "所有通道由 BIOS 控制";
                }
                finally
                {
                    _updatingDefaultStatus = false;
                }
            }
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            StatusText.Text = $"界面刷新异常：{ex.Message}";
        }
    }

    private void WatchdogTick(object? state)
    {
        if (_closing || _engine is null) return;
        if ((DateTime.UtcNow - _engine.LastTickUtc).TotalSeconds <= 8) return;

        try
        {
            _engine.RestoreAll();
        }
        catch { }
    }

    private float? FindTemperature(HardwareType type, string sensorName)
    {
        foreach (var hw in _hardware.Hardware.Where(h => h.HardwareType == type))
        {
            var sensor = hw.Sensors.FirstOrDefault(s =>
                s.SensorType == SensorType.Temperature && s.Name == sensorName && s.Value.HasValue);
            if (sensor != null) return sensor.Value;
        }
        return null;
    }

    /// <summary>按名称优先级在任意 GPU（NVIDIA / AMD / Intel）上找温度传感器。</summary>
    private float? FindGpuTemperature(params string[] sensorNames)
    {
        foreach (var name in sensorNames)
        {
            foreach (var hw in _hardware.Hardware.Where(h =>
                         h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel))
            {
                var sensor = hw.Sensors.FirstOrDefault(s =>
                    s.SensorType == SensorType.Temperature && s.Name == name && s.Value.HasValue);
                if (sensor != null) return sensor.Value;
            }
        }
        return null;
    }

    /// <summary>某类 CPU 温度传感器（如 P-Core / E-Core）中的最高值。</summary>
    private float? FindCpuCoreMax(string namePrefix)
    {
        float? max = null;
        foreach (var hw in _hardware.Hardware.Where(h => h.HardwareType == HardwareType.Cpu))
        {
            foreach (var sensor in hw.Sensors)
            {
                if (sensor.SensorType != SensorType.Temperature || !sensor.Value.HasValue) continue;
                if (!sensor.Name.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase)) continue;
                max = max is null ? sensor.Value : Math.Max(max.Value, sensor.Value.Value);
            }
        }
        return max;
    }

    /// <summary>
    /// CPU 卡片副标题：主指标 Package 放第一个，再补充 P-core / E-core 最高温。
    /// 非混合架构 CPU 退化成「Core Max」。
    /// </summary>
    private void UpdateCpuCoreDetail(float? package)
    {
        var parts = new List<string>(3);
        if (package.HasValue) parts.Add($"Package {package.Value:0.0}°");

        var pCore = FindCpuCoreMax("P-Core");
        var eCore = FindCpuCoreMax("E-Core");
        if (pCore.HasValue) parts.Add($"P-core {pCore.Value:0.0}°");
        if (eCore.HasValue) parts.Add($"E-core {eCore.Value:0.0}°");

        if (!pCore.HasValue && !eCore.HasValue)
        {
            var coreMax = FindTemperature(HardwareType.Cpu, "CPU Core Max") ?? FindCpuCoreMax("CPU Core #");
            if (coreMax.HasValue) parts.Add($"Core Max {coreMax.Value:0.0}°");
        }

        CpuCoreDetailText.Text = parts.Count > 0 ? string.Join(" · ", parts) : "Package 传感器";
    }

    /// <summary>内存卡片副标题：主指标（最高温）不再重复，只列每根内存条的槽位温度。</summary>
    private void UpdateDimmDetail()
    {
        if (!_memory.IsReady)
        {
            DimmDetailText.Text = "加载中";
            return;
        }

        var dimms = _memory.Dimms;
        if (dimms.Count == 0)
        {
            DimmDetailText.Text = "SPD 传感器";
            return;
        }

        DimmDetailText.Text = string.Join(" · ",
            dimms.Select((d, i) => $"Slot{DimmSlotNumber(d.Name, i)} {d.Value:0.0}°"));
    }

    /// <summary>槽位号：优先用传感器名里的编号（LHM 的 DIMM #0 → Slot1），否则按顺序编号。</summary>
    private static int DimmSlotNumber(string name, int order)
    {
        var digits = new string(name.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var number) ? number + 1 : order + 1;
    }

    /// <summary>
    /// GPU 卡片副标题：主指标（GPU 核心温度）放第一个，再补充 Hotspot / Memory 这些工程师指标。
    /// 正常情况下保持灰色；只有明显异常（热点 / 温差 / 显存过高）才变色提醒。
    /// </summary>
    private void UpdateGpuDetail(float? core, float? hotspot, float? memory)
    {
        var parts = new List<string>(3);

        if (core.HasValue) parts.Add($"GPU {core.Value:0.0}°");

        if (hotspot.HasValue)
        {
            var delta = core.HasValue ? hotspot.Value - core.Value : (float?)null;
            parts.Add(delta.HasValue
                ? $"Hotspot {hotspot.Value:0.0}° ({(delta.Value >= 0 ? "+" : "")}{delta.Value:0.0}°)"
                : $"Hotspot {hotspot.Value:0.0}°");
        }

        if (memory.HasValue) parts.Add($"Memory {memory.Value:0.0}°");

        if (parts.Count == 0)
        {
            GpuDetailText.Text = "GPU 传感器";
            GpuDetailText.Foreground = DimBrush;
            return;
        }

        GpuDetailText.Text = string.Join(" · ", parts);
        GpuDetailText.Foreground = GpuDetailBrush(core, hotspot, memory);
    }

    private Brush GpuDetailBrush(float? core, float? hotspot, float? memory)
    {
        var delta = core.HasValue && hotspot.HasValue ? hotspot.Value - core.Value : (float?)null;

        if (hotspot is >= 97 || memory is >= 100 || delta is >= 35) return RedBrush;
        if (hotspot is >= 88 || memory is >= 90 || delta is >= 25) return AmberBrush;
        return MutedBrush;
    }

    /// <summary>刷新一张温度卡片：数值、状态文字、进度条和颜色。</summary>
    private void UpdateTempCard(TextBlock valueText, TextBlock stateText, ProgressBar bar,
        float? value, float warnAt, float criticalAt, Brush normalBrush, string emptyState)
    {
        if (!value.HasValue)
        {
            valueText.Text = "--";
            stateText.Text = emptyState;
            stateText.Foreground = emptyState == "加载中" ? AmberBrush : DimBrush;
            bar.Value = 0;
            bar.Foreground = normalBrush;
            return;
        }

        var temperature = value.Value;
        valueText.Text = temperature.ToString("0.0");
        bar.Value = Math.Clamp(temperature, 0, 100);

        if (temperature >= criticalAt)
        {
            stateText.Text = "高温";
            stateText.Foreground = RedBrush;
            bar.Foreground = RedBrush;
        }
        else if (temperature >= warnAt)
        {
            stateText.Text = "偏高";
            stateText.Foreground = AmberBrush;
            bar.Foreground = AmberBrush;
        }
        else
        {
            stateText.Text = "正常";
            stateText.Foreground = GreenBrush;
            bar.Foreground = normalBrush;
        }
    }

    private Brush AccentBrush => (Brush)FindResource("AccentBrush");
    private Brush CyanBrush => (Brush)FindResource("CyanBrush");
    private Brush GreenBrush => (Brush)FindResource("GreenBrush");
    private Brush AmberBrush => (Brush)FindResource("AmberBrush");
    private Brush RedBrush => (Brush)FindResource("RedBrush");
    private Brush MutedBrush => (Brush)FindResource("MutedBrush");
    private Brush DimBrush => (Brush)FindResource("DimBrush");
    private Brush DimmBrush { get; } = new SolidColorBrush(Color.FromRgb(0xA6, 0xB8, 0xFF));

    private void HideZero_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _fanView is null) return;

        try { FanGrid.CommitEdit(DataGridEditingUnit.Row, true); } catch { }

        _hideZeroRpm = HideZeroButton.IsChecked == true;
        _fanView.Filter = _hideZeroRpm
            ? new Predicate<object>(o => o is FanRow { IsZeroRpm: false })
            : null;

        _visibleRowsSnapshot = _hideZeroRpm
            ? new HashSet<FanRow>(_rows.Where(r => !r.IsZeroRpm))
            : null;

        // 用户主动切换过滤条件，立即生效（点击开关本身已经关掉了下拉列表）
        _viewGate?.RequestRefresh();
        _viewGate?.FlushPending();

        StatusText.Text = _hideZeroRpm
            ? "已隐藏 0 RPM 通道（风扇转起来后会自动重新出现）"
            : "已显示全部通道";
    }

    /// <summary>
    /// 「隐藏 0 RPM」时只比较可见行集合，变化了才重建视图。
    /// 之前每秒无条件调用 <see cref="ICollectionView.Refresh"/>，会重建所有行容器，
    /// 正在展开的模式 / 温度源下拉列表会被立刻收起。
    /// </summary>
    private void UpdateRowVisibility()
    {
        if (!_hideZeroRpm || _viewGate is null) return;

        var visible = new HashSet<FanRow>(_rows.Where(r => !r.IsZeroRpm));
        if (_visibleRowsSnapshot is not null && visible.SetEquals(_visibleRowsSnapshot)) return;

        _visibleRowsSnapshot = visible;
        _viewGate.RequestRefresh();
    }

    private void QueueFlushPendingViewRefresh()
    {
        if (_viewGate is null) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _viewGate?.FlushPending()));
    }

    /// <summary>表格里是否有下拉列表正展开（有的话本轮不重建视图，否则会把列表收起来）。</summary>
    private bool IsAnyDropDownOpen() => FanGrid.IsLoaded && HasOpenComboBox(FanGrid);

    private static bool HasOpenComboBox(DependencyObject parent)
    {
        if (parent is ComboBox { IsDropDownOpen: true }) return true;

        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            if (HasOpenComboBox(VisualTreeHelper.GetChild(parent, i))) return true;
        }

        return false;
    }

    private void EditCurve_Click(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        if (sender is not FrameworkElement { DataContext: FanRow row }) return;

        // 当前方案来自内置场景时，内置场景只读：先引导复制为可编辑的「我的配置」
        if (!EnsurePresetEditable()) return;

        try { FanGrid.CommitEdit(DataGridEditingUnit.Row, true); } catch { }

        try
        {
            var dialog = new CurveEditorWindow(row.Name, row.GetCurve()) { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                row.SetCurve(dialog.ResultCurve);
                // 一次「确定」= 一步撤销
                CommitHistoryOperation();
            }
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            MessageBox.Show($"打开曲线编辑器失败：{ex.Message}", "Atlas",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CopyCurveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;

        if (FanGrid.SelectedItem is not FanRow source)
        {
            MessageBox.Show("请先在表格里选中作为模板的通道行，再点「复制曲线」。",
                "Atlas", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 当前方案来自内置场景时，内置场景只读：先引导复制为可编辑的「我的配置」。
        // 即使源通道被锁定，复制曲线也会改目标通道，所以同样要拦截。
        if (!EnsurePresetEditable()) return;

        try { FanGrid.CommitEdit(DataGridEditingUnit.Row, true); } catch { }

        try
        {
            var others = _rows.Where(r => !ReferenceEquals(r, source)).Select(r => r.Name).ToList();
            if (others.Count == 0) return;

            var dialog = new CopyCurveWindow(source.Name, others, source.CurveText) { Owner = this };
            if (dialog.ShowDialog() != true) return;

            var selected = dialog.SelectedNames.ToHashSet();
            var applied = 0;
            // 一次「复制曲线」批量改多个通道 = 一步撤销
            _suppressHistory = true;
            try
            {
                foreach (var row in _rows)
                {
                    if (ReferenceEquals(row, source) || !selected.Contains(row.Name)) continue;
                    row.SetCurve(source.GetCurve());
                    row.SelectedMode = row.Modes[2]; // 温度曲线
                    applied++;
                }
            }
            finally
            {
                _suppressHistory = false;
            }

            if (applied > 0) CommitHistoryOperation();

            StatusText.Text = $"已把「{source.Name}」的曲线应用到 {applied} 个通道（模式已切为温度曲线）";
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            MessageBox.Show($"复制曲线失败：{ex.Message}", "Atlas",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ==================================================================
    //  内置场景 / 我的配置
    // ==================================================================

    /// <summary>
    /// 把当前配置来源整理成 SceneBar 需要的一次性视图状态。
    /// 下拉列表 = 内置场景（只读）+ 我的配置；当前来源为空时再追加「未归档」占位项。
    /// SceneBar 内部会按签名去重，MainWindow 这里也记一份签名，避免每秒重复构造。
    /// </summary>
    private void RefreshSceneBar()
    {
        // 切换确认框打开时不要刷新：否则每秒一次的轮询会把用户刚选中的条目改回旧项
        if (_sceneSwitchPromptOpen) return;

        var activeScene = _config.FindActiveScene();
        var activeProfile = _config.FindActiveProfile();
        var dirty = _config.IsCurrentStateModified();

        var choices = new List<SceneBarChoiceItem>(ScenePresets.All.Count + _config.Profiles.Count + 1);

        foreach (var scene in ScenePresets.All)
        {
            choices.Add(new SceneBarChoiceItem
            {
                Id = scene.Id,
                Kind = SceneBarChoiceKind.BuiltInScene,
                Name = scene.Name,
                TypeLabel = "内置 · 只读",
                DisplayName = $"内置场景 · {scene.Name}（只读，选择即切换）",
                IsReadOnly = true,
            });
        }

        foreach (var profile in _config.Profiles)
        {
            choices.Add(new SceneBarChoiceItem
            {
                Id = profile.Id,
                Kind = SceneBarChoiceKind.Profile,
                Name = profile.Name,
                TypeLabel = "我的",
                DisplayName = $"我的配置 · {profile.Name} · 更新于 {profile.UpdatedText}",
                DetailText = profile.UpdatedText,
                IsReadOnly = false,
            });
        }

        string selectedChoiceId;
        if (activeScene != null)
        {
            selectedChoiceId = activeScene.Id;
        }
        else if (activeProfile != null)
        {
            selectedChoiceId = activeProfile.Id;
        }
        else
        {
            // 未归档：只作为「当前方案」的展示占位，不参与主动切换
            selectedChoiceId = SceneBar.DraftChoiceId;
            choices.Add(new SceneBarChoiceItem
            {
                Id = SceneBar.DraftChoiceId,
                Kind = SceneBarChoiceKind.Draft,
                Name = "当前自定义方案",
                TypeLabel = "未归档",
                DisplayName = "当前自定义方案（尚未保存为「我的配置」）",
                IsReadOnly = false,
            });
        }

        var state = new SceneBarViewState
        {
            Choices = choices,
            SelectedChoiceId = selectedChoiceId,
            IsDirty = dirty,
            Interactable = _initialized,
        };

        var signature = BuildSceneBarSignature(state);
        if (string.Equals(signature, _lastSceneBarSignature, StringComparison.Ordinal) &&
            string.Equals(SceneBarControl.SelectedChoiceId, selectedChoiceId, StringComparison.Ordinal))
        {
            return;
        }

        _lastSceneBarSignature = signature;
        SceneBarControl.Render(state);
    }

    /// <summary>当前方案来源对应的下拉选项 Id：内置场景 &gt; 我的配置 &gt; 未归档占位。</summary>
    private string CurrentChoiceId()
    {
        var activeScene = _config.FindActiveScene();
        if (activeScene != null) return activeScene.Id;

        var activeProfile = _config.FindActiveProfile();
        if (activeProfile != null) return activeProfile.Id;

        return SceneBar.DraftChoiceId;
    }

    private static string BuildSceneBarSignature(SceneBarViewState state)
    {
        var builder = new StringBuilder(256);
        builder.Append(state.IsDirty).Append('\u0001')
            .Append(state.Interactable).Append('\u0001')
            .Append(state.SelectedChoiceId ?? "").Append('\u0001');

        foreach (var choice in state.Choices)
        {
            builder.Append(choice.Id).Append('\u0002')
                .Append((int)choice.Kind).Append('\u0002')
                .Append(choice.Name).Append('\u0002')
                .Append(choice.TypeLabel).Append('\u0002')
                .Append(choice.DisplayName).Append('\u0002')
                .Append(choice.DetailText).Append('\u0002')
                .Append(choice.IsReadOnly).Append('\u0003');
        }

        return builder.ToString();
    }

    /// <summary>当前下拉选中的「我的配置」；选中内置场景/未归档时返回 null。</summary>
    private SavedProfile? SelectedChoiceProfile =>
        _config.FindProfile(SceneBarControl.SelectedChoiceId);

    /// <summary>应用场景/配置前先提交表格里正在编辑的单元格，避免用户输入被丢弃。</summary>
    private void CommitPendingEdits()
    {
        try { FanGrid.CommitEdit(DataGridEditingUnit.Row, true); } catch { }
    }

    // ==================================================================
    //  撤销 / 重做（当前方案的热设置）
    // ==================================================================

    /// <summary>
    /// 行内热设置变化。粒度 = 用户操作：
    ///  - 模式 / 温度源是离散选择 → 立即提交一步；
    ///  - 固定 PWM 滑杆拖动会连续触发 → 等手势结束（鼠标释放）或安静 250ms 后合并成一步；
    ///  - 曲线点由曲线编辑器 / 复制曲线显式提交，这里不处理。
    /// </summary>
    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressHistory || !_initialized) return;

        switch (e.PropertyName)
        {
            case nameof(FanRow.SelectedMode):
            case nameof(FanRow.SelectedSource):
                _historyCommitTimer.Stop();
                CommitHistoryOperation();
                break;

            case nameof(FanRow.FixedPercent):
                _historyCommitTimer.Stop();
                _historyCommitTimer.Start();
                break;
        }
    }

    /// <summary>结束当前手势：把尚未提交的变化记为一步（鼠标释放 / 失去捕获时调用）。</summary>
    private void FlushHistoryOperation()
    {
        if (!_historyCommitTimer.IsEnabled) return;
        _historyCommitTimer.Stop();
        CommitHistoryOperation();
    }

    /// <summary>把当前热设置提交为一步；与上一快照相同则忽略。</summary>
    private void CommitHistoryOperation()
    {
        if (!_initialized || _suppressHistory) return;
        if (_history.Record(_config.Fans)) UpdateHistoryUi();
    }

    /// <summary>切换来源 / 保存后重置历史：当前设置成为唯一基线，撤销与重做都不可用。</summary>
    private void ResetHistoryToCurrent()
    {
        _historyCommitTimer.Stop();
        _history.Reset(_config.Fans);
        UpdateHistoryUi();
    }

    private void UpdateHistoryUi() => CommandManager.InvalidateRequerySuggested();

    private void Undo_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = _initialized && _history.CanUndo;
        e.Handled = true;
    }

    private void Redo_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = _initialized && _history.CanRedo;
        e.Handled = true;
    }

    private void Undo_Executed(object sender, ExecutedRoutedEventArgs e) => StepHistory(undo: true);

    private void Redo_Executed(object sender, ExecutedRoutedEventArgs e) => StepHistory(undo: false);

    private void StepHistory(bool undo)
    {
        if (!_initialized) return;

        // 表格里正在输入的内容先落地；未提交的滑杆手势不单独成步（撤销后回到手势前）
        CommitPendingEdits();
        _historyCommitTimer.Stop();

        var changed = false;
        _suppressHistory = true;
        try
        {
            changed = undo ? _history.Undo(_config.Fans) : _history.Redo(_config.Fans);
            if (changed)
            {
                foreach (var row in _rows) row.ReloadFromConfig();
            }
        }
        finally
        {
            _suppressHistory = false;
        }

        if (!changed) return;

        RefreshSceneBar();
        UpdateHistoryUi();

        StatusText.Text = undo
            ? $"已撤销 · 还可撤销 {_history.UndoCount} 步"
            : $"已重做 · 还可重做 {_history.RedoCount} 步";
    }

    /// <summary>
    /// 场景/配置可能带回本机不存在的温度源 Id（换硬件、传感器改名）。
    /// 温度源失效 5 秒后引擎会全速兜底，所以这里把它修回默认 CPU 温度源，返回修复数量。
    /// </summary>
    private int RepairMissingSources()
    {
        var repaired = 0;
        var previousSuppressHistory = _suppressHistory;
        _suppressPresetEditGuard = true;
        _suppressHistory = true;
        try
        {
            foreach (var row in _rows)
            {
                // 温度源只是配置/展示，锁定通道也不参与 PWM 写入；这里同样补齐，
                // 避免锁定行因升级后源被收敛而显示为空。
                if (row.SelectedSource != null) continue;

                var preferred = row.IsGpu
                    ? row.Sources.FirstOrDefault(s => s.DisplayName.Contains("GPU Core")) ?? row.Sources.FirstOrDefault()
                    : FindCpuTemperature(row.Sources) ?? row.Sources.FirstOrDefault();
                if (preferred is null) continue;

                row.SelectedSource = preferred;
                repaired++;
            }
        }
        finally
        {
            _suppressPresetEditGuard = false;
            _suppressHistory = previousSuppressHistory;
        }

        return repaired;
    }

    /// <summary>
    /// 用户在下拉里选择了一项 → 立即切换（不再有独立的「应用」按钮）。
    /// 未保存修改时先确认；用户取消则把下拉回退到当前项（SetSelectedChoice 不发事件）。
    /// </summary>
    private void OnSceneBarChoiceSelected(object? sender, string choiceId)
    {
        var currentChoiceId = CurrentChoiceId();

        if (!_initialized)
        {
            SceneBarControl.SetSelectedChoice(currentChoiceId);
            return;
        }

        if (string.IsNullOrEmpty(choiceId) ||
            string.Equals(choiceId, currentChoiceId, StringComparison.Ordinal))
        {
            SceneBarControl.SetSelectedChoice(currentChoiceId);
            return;
        }

        CommitPendingEdits();

        // 确认框 / 命名框打开期间暂停每秒刷新，避免用户刚选中的项被轮询回退
        _sceneSwitchPromptOpen = true;
        try
        {
            if (ScenePresets.Find(choiceId) is { } scene)
            {
                ApplySceneChoice(scene, currentChoiceId);
            }
            else if (_config.FindProfile(choiceId) is { } profile)
            {
                ApplyProfileChoice(profile, currentChoiceId);
            }
            else
            {
                // 未知条目（例如配置刚好被删除）：回退下拉，不做变更
                StatusText.Text = "所选方案已不存在，已恢复到当前方案";
                SceneBarControl.SetSelectedChoice(currentChoiceId);
            }
        }
        finally
        {
            _sceneSwitchPromptOpen = false;
        }

        // 统一刷新一次：成功时显示新来源，取消/失败时把下拉回退到当前来源
        RefreshSceneBar();
    }

    /// <summary>选中内置场景 → 立即套用；失败/取消时回退下拉选中项。</summary>
    private void ApplySceneChoice(ScenePreset scene, string currentChoiceId)
    {
        if (_config.IsCurrentStateModified())
        {
            var answer = MessageBox.Show(
                "当前方案有未保存的修改，切换会丢弃（已保存的「我的配置」不受影响）。继续吗？",
                "Atlas", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                SceneBarControl.SetSelectedChoice(currentChoiceId);
                return;
            }
        }

        try
        {
            var result = FanProfileService.ApplyScene(_config, scene);
            if (result.AppliedChannels == 0)
            {
                StatusText.Text = result.SkippedProtected > 0
                    ? $"未应用「{scene.Name}」：{result.SkippedProtected} 个通道已锁定，没有可接管的通道"
                    : $"未应用「{scene.Name}」：当前没有可接管的通道";
                SceneBarControl.SetSelectedChoice(currentChoiceId);
                return;
            }

            var previousSuppressHistory = _suppressHistory;
            _suppressHistory = true;
            int repaired;
            try
            {
                foreach (var row in _rows) row.ReloadFromConfig();
                repaired = RepairMissingSources();
                // 温度源自动修复会改 SourceId，基线必须以修复后的设置为准，否则刚应用完就显示「已修改」
                _config.StateFingerprint = _config.CurrentFingerprint();
            }
            finally
            {
                _suppressHistory = previousSuppressHistory;
            }

            _config.Save();
            // 来源已切换：旧方案的编辑历史不再适用
            ResetHistoryToCurrent();
            StatusText.Text = $"{result.Summary}（内置场景「{scene.Name}」）" +
                              (repaired > 0 ? $" · 已修复 {repaired} 个失效温度源" : "");
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            MessageBox.Show($"套用内置场景失败：{ex.Message}", "Atlas",
                MessageBoxButton.OK, MessageBoxImage.Error);
            SceneBarControl.SetSelectedChoice(currentChoiceId);
        }
    }

    /// <summary>选中「我的配置」→ 立即套用；失败/取消时回退下拉选中项。</summary>
    private void ApplyProfileChoice(SavedProfile profile, string currentChoiceId)
    {
        if (_config.IsCurrentStateModified())
        {
            var answer = MessageBox.Show(
                "当前方案有未保存的修改，切换会丢弃（已保存的「我的配置」不受影响）。继续吗？",
                "Atlas", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                SceneBarControl.SetSelectedChoice(currentChoiceId);
                return;
            }
        }

        try
        {
            var result = FanProfileService.ApplyProfile(_config, profile);
            if (result.AppliedChannels == 0)
            {
                StatusText.Text = result.SkippedProtected > 0
                    ? $"未应用「{profile.Name}」：{result.SkippedProtected} 个通道已锁定，没有可接管的通道"
                    : $"未应用「{profile.Name}」：配置里没有可用的通道";
                SceneBarControl.SetSelectedChoice(currentChoiceId);
                return;
            }

            var previousSuppressHistory = _suppressHistory;
            _suppressHistory = true;
            int repaired;
            try
            {
                foreach (var row in _rows) row.ReloadFromConfig();
                repaired = RepairMissingSources();
                // 同场景：温度源修复后刷新基线，避免刚应用完就显示「已修改」
                _config.StateFingerprint = _config.CurrentFingerprint();
            }
            finally
            {
                _suppressHistory = previousSuppressHistory;
            }

            _config.Save();
            // 来源已切换：旧方案的编辑历史不再适用
            ResetHistoryToCurrent();
            StatusText.Text = result.Summary +
                              (repaired > 0 ? $" · 已修复 {repaired} 个失效温度源" : "");
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            MessageBox.Show($"套用配置失败：{ex.Message}", "Atlas",
                MessageBoxButton.OK, MessageBoxImage.Error);
            SceneBarControl.SetSelectedChoice(currentChoiceId);
        }
    }

    /// <summary>
    /// 内置场景是只读的：用户尝试编辑（曲线/模式/温度源/固定 PWM）时先拦截，
    /// 引导「复制为我的配置」，复制成功也返回 false，让用户重新发起这次编辑（操作本身不生效）。
    /// </summary>
    private bool EnsurePresetEditable()
    {
        var scene = _config.FindActiveScene();
        if (scene is null) return true;

        var answer = MessageBox.Show(
            $"内置场景「{scene.Name}」是只读的，不能直接修改。是否复制为「我的配置」后继续编辑？",
            "Atlas", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return false;

        try
        {
            CommitPendingEdits();

            var dialog = new ProfileNameWindow(
                "复制为我的配置",
                $"把场景「{scene.Name}」的当前设置复制为一个可编辑的「我的配置」",
                $"{scene.Name} · 我的",
                name => FanProfileService.ValidateProfileName(_config, name))
            {
                Owner = this,
            };

            if (dialog.ShowDialog() != true) return false;

            var profile = FanProfileService.CaptureProfile(_config, dialog.ResultName);
            _config.Profiles.Add(profile);
            _config.MarkApplied(null, profile.Id);
            _config.Save();
            // 来源已切换：从新的「我的配置」重新开始积累历史
            ResetHistoryToCurrent();

            StatusText.Text = $"已复制为「我的配置 · {profile.Name}」，现在可以修改了";
            RefreshSceneBar();
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            MessageBox.Show($"复制为「我的配置」失败：{ex.Message}", "Atlas",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }

        // 本次编辑操作不生效：当前选中的工具/控件状态已因刷新而重置，请用户重新操作
        return false;
    }

    /// <summary>
    /// 表格里的模式/温度源/固定 PWM 属于「修改当前方案」：当前方案来自内置场景（只读）时拦截本次交互。
    /// 备注名与「锁定」是通道身份/安全属性，不拦截。
    /// </summary>
    private void PresetEditGuard_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_initialized) return;
        if (sender is not FrameworkElement { DataContext: FanRow row }) return;
        if (_config.ActiveSceneId is null) return;

        if (PresetEditAllowed()) return;
        e.Handled = true;
    }

    /// <summary>
    /// 统一的只读拦截入口：内置场景生效时提示复制，并做节流（滑块拖动/键盘连按不重复弹框）。
    /// 返回 false 表示本次修改被拒绝；返回 true 表示可以继续编辑。
    /// </summary>
    private bool PresetEditAllowed()
    {
        if (_suppressPresetEditGuard) return true;
        if (_config.ActiveSceneId is null) return true;
        if (_presetEditPromptOpen || DateTime.UtcNow < _presetEditPromptSuppressUntil) return false;

        _presetEditPromptOpen = true;
        try
        {
            EnsurePresetEditable();
        }
        finally
        {
            _presetEditPromptOpen = false;
            _presetEditPromptSuppressUntil = DateTime.UtcNow.AddSeconds(2);
        }

        return false;
    }

    private void OnSaveAsProfileRequested(object? sender, EventArgs e)
    {
        if (!_initialized) return;

        CommitPendingEdits();

        try
        {
            var dialog = new ProfileNameWindow(
                "另存为配置",
                "把当前所有通道设置另存为一个新的「我的配置」",
                "我的配置 " + DateTime.Now.ToString("MM-dd HH:mm"),
                name => FanProfileService.ValidateProfileName(_config, name))
            {
                Owner = this,
            };

            if (dialog.ShowDialog() != true) return;

            var profile = FanProfileService.CaptureProfile(_config, dialog.ResultName);
            _config.Profiles.Add(profile);
            _config.MarkApplied(null, profile.Id);
            _config.Save();
            // 来源已切换：重新开始积累历史
            ResetHistoryToCurrent();

            StatusText.Text = $"已另存为「{profile.Name}」";
            RefreshSceneBar();
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            MessageBox.Show($"另存为配置失败：{ex.Message}", "Atlas",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnSaveProfileRequested(object? sender, EventArgs e)
    {
        if (!_initialized) return;

        var profile = SelectedChoiceProfile;
        if (profile is null)
        {
            MessageBox.Show("请先在「场景 / 配置」下拉框里选择一个「我的配置」。", "Atlas",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        CommitPendingEdits();

        // 换硬件/少接风扇时，保存会把配置里那些当前不存在的通道永久丢掉，先提醒
        var currentIds = new HashSet<string>(_config.Fans.Select(f => f.FanId), StringComparer.Ordinal);
        var missing = profile.Fans.Count(e => !currentIds.Contains(e.FanId));
        var missingNote = missing > 0
            ? $"\n\n注意：该配置包含 {missing} 个当前不可用的通道，保存后它们会从配置中移除（设置无法恢复）。"
            : "";

        var answer = MessageBox.Show(
            $"把当前修改保存到配置「{profile.Name}」？（配置内容会更新，名称不变）{missingNote}",
            "Atlas", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            FanProfileService.UpdateProfile(_config, profile);
            _config.Save();
            // 设计取舍：保存即清空撤销历史（历史只在两次保存之间有效）
            ResetHistoryToCurrent();
            StatusText.Text = $"已保存配置「{profile.Name}」· 撤销历史已清空";
            RefreshSceneBar();
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            MessageBox.Show($"保存配置失败：{ex.Message}", "Atlas",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnRenameProfileRequested(object? sender, EventArgs e)
    {
        if (!_initialized) return;

        var profile = SelectedChoiceProfile;
        if (profile is null)
        {
            MessageBox.Show("请先在「场景 / 配置」下拉框里选择一个「我的配置」。", "Atlas",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        CommitPendingEdits();

        try
        {
            var dialog = new ProfileNameWindow(
                "重命名配置",
                $"给配置「{profile.Name}」起一个新名字",
                profile.Name,
                name => FanProfileService.ValidateProfileName(_config, name, profile.Id))
            {
                Owner = this,
            };

            if (dialog.ShowDialog() != true) return;

            profile.Name = dialog.ResultName;
            _config.Save();
            StatusText.Text = $"已重命名配置为「{profile.Name}」";
            RefreshSceneBar();
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            MessageBox.Show($"重命名配置失败：{ex.Message}", "Atlas",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnDeleteProfileRequested(object? sender, EventArgs e)
    {
        if (!_initialized) return;

        var profile = SelectedChoiceProfile;
        if (profile is null)
        {
            MessageBox.Show("请先在「场景 / 配置」下拉框里选择一个「我的配置」。", "Atlas",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        CommitPendingEdits();

        var answer = MessageBox.Show(
            $"删除配置「{profile.Name}」？当前方案设置不会改变。",
            "Atlas", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            var name = profile.Name;
            if (!FanProfileService.DeleteProfile(_config, profile.Id)) return;

            _config.Save();
            StatusText.Text = $"已删除配置「{name}」";
            RefreshSceneBar();
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            MessageBox.Show($"删除配置失败：{ex.Message}", "Atlas",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;

        CommitPendingEdits();

        // 「全部交由 BIOS 控制」会把所有通道的模式批量改回 BIOS 默认，本质仍是对当前方案的编辑。
        // 当前方案来自内置场景（只读）时，必须走和其他编辑动作相同的拦截：
        // 先引导复制为「我的配置」，本次操作不生效（用户复制后可再点一次）。
        if (!EnsurePresetEditable()) return;

        // 全部交由 BIOS 控制 = 把所有通道模式批量改回「BIOS 默认」并交还控制权。
        // 若当前来源是「我的配置」，保留来源并显示「已修改」：点「保存」写回该配置，
        // 而不是把当前方案降级成「未归档」、逼用户另存成一个新配置。
        SavedProfile? activeProfile;
        _suppressHistory = true;
        try
        {
            activeProfile = FanProfileService.RestoreToBiosDefaults(_config);
            foreach (var row in _rows) row.ReloadFromConfig();
        }
        finally
        {
            _suppressHistory = false;
        }

        _engine?.RestoreAll();

        _config.Save();
        // 一次「交给 BIOS」= 一步撤销（撤销会把控制权重新交回 Atlas）
        CommitHistoryOperation();

        StatusText.Text = activeProfile is null
            ? "已全部交由 BIOS 控制"
            : $"已交由 BIOS 控制 · 点「保存」可写回配置「{activeProfile.Name}」";
        RefreshSceneBar();
    }

    private void TitleBar_SettingsRequested(object? sender, EventArgs e)
    {
        try
        {
            var dialog = new SettingsWindow { Owner = this };
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            MessageBox.Show($"打开设置失败：{ex.Message}", "Atlas",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SafeRestore()
    {
        try { _engine?.RestoreAll(); }
        catch { }
    }

    private void StartupMark(string message)
    {
        try
        {
            Directory.CreateDirectory(AtlasConfig.ConfigDir);
            File.AppendAllText(Path.Combine(AtlasConfig.ConfigDir, "startup.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {message} (sinceCtor={_startup.ElapsedMilliseconds}ms, sinceProcess={(DateTime.Now - ProcessStartTime).TotalMilliseconds:0}ms){Environment.NewLine}");
        }
        catch { }
    }

    private static void LogCrash(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(AtlasConfig.ConfigDir);
            File.AppendAllText(Path.Combine(AtlasConfig.ConfigDir, "crash.log"),
                $"[{DateTime.Now:o}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _closing = true;
        _timer.Stop();
        _historyCommitTimer.Stop();
        _watchdog.Dispose();
        _config.Save();
        _engine?.Dispose();
        _memory.Dispose();
        _hardware.Dispose();
    }
}
