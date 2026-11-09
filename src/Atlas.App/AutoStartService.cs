using System.Diagnostics;

namespace Atlas.App;

/// <summary>
/// 开机启动：用计划任务 AtlasFan 在登录时以最高权限启动 Atlas（避免每次弹 UAC）。
/// 从主窗口工具栏挪到「设置」窗口后，这层逻辑独立出来方便复用。
/// </summary>
internal static class AutoStartService
{
    public const string TaskName = "AtlasFan";

    public static bool IsInstalled() =>
        RunSchtasks($"/query /tn \"{TaskName}\"") == 0;

    /// <summary>安装或移除开机自启，返回是否操作成功；message 用于界面反馈。</summary>
    public static bool SetEnabled(bool enabled, out string message)
    {
        if (!enabled)
        {
            var exitCode = RunSchtasks($"/delete /tn \"{TaskName}\" /f");
            message = exitCode == 0 ? "已关闭开机启动" : $"关闭开机启动失败（schtasks 退出码 {exitCode}）";
            return exitCode == 0;
        }

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            message = "无法确定程序路径，开机启动设置失败";
            return false;
        }

        var command = $"/create /tn \"{TaskName}\" /tr \"\\\"{exe}\\\"\" /sc onlogon /rl highest /f";
        var created = RunSchtasks(command);
        message = created == 0
            ? "已启用开机启动（登录时以管理员权限启动 Atlas）"
            : $"启用开机启动失败（schtasks 退出码 {created}）";
        return created == 0;
    }

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
}
