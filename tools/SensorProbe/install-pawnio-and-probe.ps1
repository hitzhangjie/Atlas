# 以管理员权限：静默安装 PawnIO 2.2.0 → 检查服务/注册表 → 重新运行 SensorProbe
# 由普通用户 PowerShell 启动，脚本内部通过 UAC 提权（执行时会弹一次 UAC）
$ErrorActionPreference = 'Continue'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)   # tools/PawnIO -> repo root
$setup    = Join-Path $repoRoot 'tools\PawnIO_setup.exe'
$log      = Join-Path $PSScriptRoot 'pawnio-install.log'
$dll      = Join-Path $repoRoot 'tools\SensorProbe\bin\Release\net8.0\win-x64\SensorProbe.dll'
$out      = Join-Path $repoRoot 'tools\SensorProbe\probe-elevated.txt'

"=== $(Get-Date -Format o) ===" | Out-File $log -Encoding utf8
"elevated? $(([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))" | Out-File $log -Append -Encoding utf8

if (-not (Test-Path $setup)) { "missing setup: $setup" | Out-File $log -Append; exit 2 }

$p = Start-Process -FilePath $setup -ArgumentList '-install', '-silent' -Wait -PassThru
"installer exit=$($p.ExitCode)" | Out-File $log -Append -Encoding utf8
Start-Sleep -Seconds 2

"--- service ---" | Out-File $log -Append -Encoding utf8
sc.exe query PawnIO | Out-File $log -Append -Encoding utf8
"--- uninstall key ---" | Out-File $log -Append -Encoding utf8
Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO' -ErrorAction SilentlyContinue |
    Select-Object DisplayName, DisplayVersion, InstallLocation | Out-File $log -Append -Encoding utf8
"--- device ---" | Out-File $log -Append -Encoding utf8
Test-Path '\\.\PawnIO' | Out-File $log -Append -Encoding utf8

"--- running probe ---" | Out-File $log -Append -Encoding utf8
if (Test-Path $dll) {
    dotnet exec $dll *> $out
    "probe exit=$LASTEXITCODE" | Out-File $log -Append -Encoding utf8
} else {
    "missing dll: $dll" | Out-File $log -Append -Encoding utf8
}
