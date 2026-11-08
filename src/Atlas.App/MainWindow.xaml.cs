using Atlas.Core;
using LibreHardwareMonitor.Hardware;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace Atlas.App;

public partial class MainWindow : Window
{
    private const string AutoStartTaskName = "AtlasFan";
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

    public MainWindow()
    {
        InitializeComponent();
        WindowChromeHelper.Enable(this);
        StartupMark("InitializeComponent");

        // 下拉列表 / 单元格编辑 / 拖拽期间，把「会重建表格行容器」的过滤刷新延后，避免打断用户操作
        FanGrid.BeginningEdit += (_, _) => _viewGate?.NotifyEditStarted();
        FanGrid.CellEditEnding += (_, _) =>
        {
            _viewGate?.NotifyEditEnded();
            QueueFlushPendingViewRefresh();
        };
        LostMouseCapture += (_, _) => QueueFlushPendingViewRefresh();
        PreviewMouseUp += (_, _) => QueueFlushPendingViewRefresh();

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
            StatusText.Text = $"已恢复 BIOS 默认（UI 异常：{args.Exception.Message}）";
        };

        SetInteractionEnabled(false);
        StatusText.Text = "正在初始化硬件…";
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
            BuildRows();
            _fanView = CollectionViewSource.GetDefaultView(_rows);
            _viewGate = new ViewRefreshGate(_fanView, IsAnyDropDownOpen);
            FanGrid.ItemsSource = _fanView;
            StartupMark($"rows built ({rowWatch.ElapsedMilliseconds}ms)");

            _engine = new FanEngine(_hardware, _config);
            _timer.Start();
            _initialized = true;
            SetInteractionEnabled(true);
            StartupMark($"init complete, process→ready = {(DateTime.Now - ProcessStartTime).TotalMilliseconds:0}ms");

            StatusText.Text = "就绪";
            OnTick(null, EventArgs.Empty);

            // 内存/DIMM 温度初始化很慢（约 4.5s），后台加载，不阻塞风扇控制
            _ = LoadMemoryAsync();

            // 计划任务查询会起进程，放后台，别拖慢界面
            await Task.Run(UpdateAutoStartButton);
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
        AutoStartButton.IsEnabled = enabled;
        CopyCurveButton.IsEnabled = enabled;
        SaveButton.IsEnabled = enabled;
        RestoreButton.IsEnabled = enabled;
    }

    private void BuildRows()
    {
        var sources = _hardware.GetTemperatureSources();
        var preferred = sources.FirstOrDefault(s => s.DisplayName.Contains("CPU Package"))
                        ?? sources.FirstOrDefault(s => s.DisplayName.Contains("Core Max"))
                        ?? sources.FirstOrDefault();

        foreach (var fan in _hardware.Fans)
        {
            var cfg = _config.GetOrCreate(fan);
            if (string.IsNullOrWhiteSpace(cfg.SourceId))
                cfg.SourceId = preferred?.Id ?? "";
            _rows.Add(new FanRow(fan, cfg, sources));
        }

        ChannelCountText.Text = $"{_rows.Count} 个通道";
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
            StatusText.Text = !controlling
                ? $"{_engine.LastStatus}（所有通道由 BIOS 控制）"
                : $"{_engine.LastStatus} · 正在控制 {_engine.ActiveChannels} 个通道";
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

        try { FanGrid.CommitEdit(DataGridEditingUnit.Row, true); } catch { }

        try
        {
            var dialog = new CurveEditorWindow(row.Name, row.GetCurve()) { Owner = this };
            if (dialog.ShowDialog() == true)
                row.SetCurve(dialog.ResultCurve);
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

        try { FanGrid.CommitEdit(DataGridEditingUnit.Row, true); } catch { }

        if (FanGrid.SelectedItem is not FanRow source)
        {
            MessageBox.Show("请先在表格里选中作为模板的通道行，再点「复制曲线…」。",
                "Atlas", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var others = _rows.Where(r => !ReferenceEquals(r, source)).Select(r => r.Name).ToList();
            if (others.Count == 0) return;

            var dialog = new CopyCurveWindow(source.Name, others, source.CurveText) { Owner = this };
            if (dialog.ShowDialog() != true) return;

            var selected = dialog.SelectedNames.ToHashSet();
            var applied = 0;
            foreach (var row in _rows)
            {
                if (ReferenceEquals(row, source) || !selected.Contains(row.Name)) continue;
                row.SetCurve(source.GetCurve());
                row.SelectedMode = row.Modes[2]; // 温度曲线
                applied++;
            }

            StatusText.Text = $"已把「{source.Name}」的曲线应用到 {applied} 个通道（模式已切为温度曲线）";
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            MessageBox.Show($"复制曲线失败：{ex.Message}", "Atlas",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        _config.Save();
        StatusText.Text = $"配置已保存：{AtlasConfig.ConfigPath}";
    }

    private void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;

        foreach (var row in _rows) row.SelectedMode = row.Modes[0]; // BIOS 默认
        _engine?.RestoreAll();
        _config.Save();
        StatusText.Text = "已关闭所有控制并恢复 BIOS 默认";
    }

    private void AutoStartButton_Click(object sender, RoutedEventArgs e)
    {
        var installed = IsAutoStartInstalled();
        if (installed)
        {
            RunSchtasks($"/delete /tn \"{AutoStartTaskName}\" /f");
            StatusText.Text = "已移除开机自启";
        }
        else
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                StatusText.Text = "无法确定程序路径，开机自启安装失败";
                return;
            }

            var command = $"/create /tn \"{AutoStartTaskName}\" /tr \"\\\"{exe}\\\"\" /sc onlogon /rl highest /f";
            var exitCode = RunSchtasks(command);
            StatusText.Text = exitCode == 0
                ? $"已安装开机自启（登录时以管理员权限启动 {exe}）"
                : $"安装开机自启失败（schtasks 退出码 {exitCode}）";
        }

        UpdateAutoStartButton();
    }

    private void UpdateAutoStartButton()
    {
        try
        {
            var installed = IsAutoStartInstalled();
            Dispatcher.Invoke(() =>
                AutoStartTextBlock.Text = installed ? "移除开机自启" : "安装开机自启");
        }
        catch { }
    }

    private static bool IsAutoStartInstalled() =>
        RunSchtasks($"/query /tn \"{AutoStartTaskName}\"") == 0;

    private static int RunSchtasks(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("schtasks.exe", arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process == null) return -1;
            process.WaitForExit();
            return process.ExitCode;
        }
        catch
        {
            return -1;
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
        _watchdog.Dispose();
        _config.Save();
        _engine?.Dispose();
        _memory.Dispose();
        _hardware.Dispose();
    }
}
