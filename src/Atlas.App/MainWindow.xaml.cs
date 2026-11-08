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
    private bool _hideZeroRpm;
    private bool _initialized;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        WindowChromeHelper.Enable(this);
        StartupMark("InitializeComponent");

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

            // DataGrid 正在编辑单元格时不允许 Refresh（会抛 InvalidOperationException），跳过本次即可
            if (_hideZeroRpm && _fanView is not null)
            {
                try { _fanView.Refresh(); }
                catch (InvalidOperationException) { }
            }

            UpdateTempCard(CpuTempText, CpuTempState, CpuTempBar,
                FindTemperature(HardwareType.Cpu, "CPU Package"), 75, 88, AccentBrush, "无数据");
            UpdateTempCard(GpuTempText, GpuTempState, GpuTempBar,
                FindTemperature(HardwareType.GpuNvidia, "GPU Core"), 78, 88, CyanBrush, "无数据");
            UpdateTempCard(HotspotText, HotspotState, HotspotBar,
                FindTemperature(HardwareType.GpuNvidia, "GPU Hot Spot"), 88, 97, AmberBrush, "无数据");
            UpdateTempCard(DimmText, DimmState, DimmBar,
                _memory.MaxDimmTemperature, 55, 65, DimmBrush,
                _memory.IsReady ? "无数据" : "加载中");

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

        try { _fanView.Refresh(); }
        catch (InvalidOperationException) { }

        StatusText.Text = _hideZeroRpm
            ? "已隐藏 0 RPM 通道（风扇转起来后会自动重新出现）"
            : "已显示全部通道";
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
