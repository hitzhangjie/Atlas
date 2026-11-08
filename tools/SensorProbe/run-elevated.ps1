# 以管理员权限运行 SensorProbe（会弹出 UAC），输出写入 probe-elevated.txt
# 用法（普通 PowerShell 即可，脚本自己提权）：
#   pwsh -NoProfile -ExecutionPolicy Bypass -File tools\SensorProbe\run-elevated.ps1
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$dll = Join-Path $repoRoot 'tools\SensorProbe\bin\Release\net8.0\win-x64\SensorProbe.dll'
$out = Join-Path $repoRoot 'tools\SensorProbe\probe-elevated.txt'

if (-not (Test-Path $dll)) {
    throw "先构建：dotnet build tools\SensorProbe\SensorProbe.csproj -c Release"
}

$pwsh = (Get-Command pwsh).Source
$cmd = "dotnet exec '$dll' *> '$out'"

Write-Host "请求管理员权限（UAC）..."
$p = Start-Process -FilePath $pwsh `
    -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', $cmd) `
    -Verb RunAs -Wait -PassThru

Write-Host "exit=$($p.ExitCode)"
if (Test-Path $out) {
    Write-Host "输出：$out"
    Get-Content $out | Select-Object -First 80
} else {
    Write-Host "没有生成输出文件（可能 UAC 被取消）"
}
