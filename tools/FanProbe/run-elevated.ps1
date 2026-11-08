# 以管理员权限运行 FanProbe（逐通道 100% 测试并恢复默认）
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$dll = Join-Path $repoRoot 'tools\FanProbe\bin\Release\net8.0\FanProbe.dll'
$out = Join-Path $repoRoot 'tools\FanProbe\fan-probe.txt'
if (-not (Test-Path $dll)) { throw "先构建：dotnet build tools\FanProbe\FanProbe.csproj -c Release" }
dotnet exec $dll *> $out
"exit=$LASTEXITCODE" | Out-File $out -Append -Encoding utf8
