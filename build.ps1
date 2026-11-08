<#
.SYNOPSIS
    Atlas 一键构建 / 打包脚本。

.DESCRIPTION
    把 src\Atlas.App 发布到 dist\（默认），支持三种发布模式，可选生成 zip 压缩包。
    构建前会检查 .NET SDK、读取版本号，并检查 Atlas 是否正在运行（占用输出文件会导致发布失败）。

.PARAMETER Mode
    发布模式：
      Framework     框架依赖（默认）。体积最小，目标机需要 .NET 8 Desktop Runtime。
      SelfContained 自包含目录。目标机免装运行时，体积约 150~200 MB。
      SingleFile    自包含单文件 exe。目标机免装运行时，单文件，启动稍慢。

.PARAMETER Configuration
    Release（默认）或 Debug。

.PARAMETER Runtime
    目标运行时标识，默认 win-x64。

.PARAMETER Output
    输出目录，默认仓库下的 dist。

.PARAMETER Zip
    发布后把输出目录打包成 zip，放到 artifacts\。

.PARAMETER NoClean
    不先清空输出目录（增量发布，可能残留旧文件）。

.PARAMETER StopAtlas
    构建前尝试结束正在运行的 Atlas（需要管理员权限的终端）。

.PARAMETER AllowRunning
    检测到 Atlas 正在运行时也继续构建（仅在输出到其他目录时使用）。

.EXAMPLE
    .\build.ps1
    框架依赖构建到 dist\。

.EXAMPLE
    .\build.ps1 -Mode SingleFile -Zip
    发布自包含单文件 exe，并在 artifacts\ 生成 zip 压缩包。

.EXAMPLE
    .\build.ps1 -Mode SelfContained -Output D:\Temp\atlas-portable
    发布自包含目录到指定位置。
#>
#Requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Framework', 'SelfContained', 'SingleFile')]
    [string]$Mode = 'Framework',

    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [string]$Runtime = 'win-x64',

    [string]$Output = 'dist',

    [switch]$Zip,

    [switch]$NoClean,

    [switch]$StopAtlas,

    [switch]$AllowRunning
)

Set-StrictMode -Version 1.0
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
$projectPath = Join-Path $repoRoot 'src\Atlas.App\Atlas.App.csproj'

function Write-Section {
    param([string]$Text)
    Write-Host ''
    Write-Host "==> $Text" -ForegroundColor Cyan
}

function Write-Warn {
    param([string]$Text)
    Write-Host "警告：$Text" -ForegroundColor Yellow
}

function Fail {
    param([string]$Text)
    Write-Host ''
    Write-Host "错误：$Text" -ForegroundColor Red
    exit 1
}

function Resolve-OutputDirectory {
    param([string]$Path)

    if ([IO.Path]::IsPathRooted($Path)) {
        $full = [IO.Path]::GetFullPath($Path)
    }
    else {
        $full = [IO.Path]::GetFullPath((Join-Path $repoRoot $Path))
    }

    return $full.TrimEnd('\')
}

# ---------- 基本检查 ----------

if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    Fail "找不到项目文件：$projectPath。请确认脚本放在仓库根目录。"
}

$outputDir = Resolve-OutputDirectory $Output
$rootCompare = $repoRoot.ToLowerInvariant()
$outputCompare = $outputDir.ToLowerInvariant()
if ($outputCompare -eq $rootCompare -or $rootCompare.StartsWith($outputCompare + '\')) {
    Fail "输出目录不能是仓库根目录或它的上级目录：$outputDir"
}
if ($outputDir -eq [IO.Path]::GetPathRoot($outputDir).TrimEnd('\')) {
    Fail "输出目录不能是盘符根目录：$outputDir"
}
if (Test-Path -LiteralPath $outputDir -PathType Leaf) {
    Fail "输出路径已存在且是一个文件：$outputDir"
}

$dotnetCommand = Get-Command -Name dotnet -CommandType Application -ErrorAction SilentlyContinue
if (-not $dotnetCommand) {
    Fail '未找到 dotnet 命令。请先安装 .NET 8 SDK：https://dotnet.microsoft.com/download/dotnet/8.0'
}

$sdkVersion = (& dotnet --version 2>$null | Select-Object -First 1)
if ($LASTEXITCODE -ne 0 -or -not $sdkVersion) {
    Fail 'dotnet 不可用（dotnet --version 失败）。请确认 .NET SDK 安装正确。'
}
$sdkVersion = "$sdkVersion".Trim()
try { $sdkMajor = [int]($sdkVersion -split '\.')[0] } catch { $sdkMajor = 0 }
if ($sdkMajor -lt 8) {
    Fail "需要 .NET 8 或更新版本的 SDK，当前为 $sdkVersion。"
}

# 从 csproj 读取版本号，用于压缩包命名
$version = '0.0.0'
try {
    # 项目文件是 UTF-8 无 BOM，PowerShell 5.1 的 Get-Content 会按 ANSI 读取导致中文乱码，所以显式指定编码
    [xml]$projectXml = [IO.File]::ReadAllText($projectPath, [Text.Encoding]::UTF8)
    $versionNodes = @($projectXml.SelectNodes('//PropertyGroup/Version'))
    if ($versionNodes.Count -gt 0 -and $versionNodes[0].InnerText.Trim()) {
        $version = $versionNodes[0].InnerText.Trim()
    }
}
catch {
    Write-Warn "无法从 csproj 读取版本号，压缩包将使用 $version。"
}

# ---------- 检查 Atlas 是否正在运行 ----------

$atlasProcesses = @(Get-Process -Name 'Atlas' -ErrorAction SilentlyContinue)
if ($atlasProcesses.Count -gt 0) {
    $atlasPids = ($atlasProcesses | ForEach-Object { $_.Id }) -join ', '
    Write-Warn "检测到 Atlas 正在运行（PID: $atlasPids）。"

    if ($StopAtlas) {
        Write-Section '尝试关闭正在运行的 Atlas'
        foreach ($atlasProcess in $atlasProcesses) {
            try {
                $atlasProcess | Stop-Process -Force -ErrorAction Stop
                [void]$atlasProcess.WaitForExit(5000)
                Write-Host "  已结束进程 $($atlasProcess.Id)" -ForegroundColor DarkGray
            }
            catch {
                Write-Warn "无法结束进程 $($atlasProcess.Id)：$($_.Exception.Message)"
            }
        }
        Start-Sleep -Milliseconds 400
        $remaining = @(Get-Process -Name 'Atlas' -ErrorAction SilentlyContinue)
        if ($remaining.Count -gt 0) {
            $remainingPids = ($remaining | ForEach-Object { $_.Id }) -join ', '
            Fail "仍有 Atlas 进程未退出（PID: $remainingPids）。请手动退出 Atlas，或在管理员权限的终端里运行本脚本。"
        }
    }
    elseif (-not $AllowRunning) {
        Fail @"
Atlas 正在运行，输出目录中的 Atlas.dll / Atlas.exe 等文件被占用，发布一定会失败。

请先关闭 Atlas（关闭主窗口即可），然后重新运行本脚本。
  - 或者在管理员权限的终端里运行：.\build.ps1 -StopAtlas
  - 如果只是构建到其他目录：.\build.ps1 -Output <目录> -AllowRunning
"@
    }
    else {
        Write-Warn '已指定 -AllowRunning，继续构建；如果输出目录被占用，发布仍会失败。'
    }
}

# ---------- 清理输出目录 ----------

if (-not $NoClean -and (Test-Path -LiteralPath $outputDir)) {
    Write-Section '清理旧输出目录'
    Write-Host "  $outputDir" -ForegroundColor DarkGray
    Remove-Item -LiteralPath $outputDir -Recurse -Force
}
elseif ($NoClean) {
    Write-Section '跳过清理（-NoClean）'
}

[void](New-Item -ItemType Directory -Path $outputDir -Force)

# ---------- 发布 ----------

$publishArgs = @(
    'publish',
    $projectPath,
    '--configuration', $Configuration,
    '--runtime', $Runtime,
    '--output', $outputDir,
    '--nologo'
)

switch ($Mode) {
    'Framework' {
        $publishArgs += @('--self-contained', 'false')
    }
    'SelfContained' {
        $publishArgs += @('--self-contained', 'true')
    }
    'SingleFile' {
        $publishArgs += @(
            '--self-contained', 'true',
            '-p:PublishSingleFile=true',
            '-p:IncludeNativeLibrariesForSelfExtract=true',
            '-p:EnableCompressionInSingleFile=true'
        )
    }
}

Write-Section "发布 Atlas（$Mode / $Configuration / $Runtime）"
Write-Host ("dotnet " + ($publishArgs -join ' ')) -ForegroundColor DarkGray
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    Fail "dotnet publish 失败（退出码 $LASTEXITCODE）。"
}

$exePath = Join-Path $outputDir 'Atlas.exe'
if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
    Fail "发布命令执行完成，但没有找到 Atlas.exe：$exePath"
}

$totalBytes = (Get-ChildItem -LiteralPath $outputDir -Recurse -File | Measure-Object -Property Length -Sum).Sum
$totalSizeMb = [math]::Round($totalBytes / 1MB, 1)

# ---------- 压缩包 ----------

$zipPath = $null
if ($Zip) {
    $artifactsDir = Join-Path $repoRoot 'artifacts'
    [void](New-Item -ItemType Directory -Path $artifactsDir -Force)

    $zipName = "Atlas-v$version-$Runtime-$($Mode.ToLowerInvariant()).zip"
    $zipPath = Join-Path $artifactsDir $zipName
    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    Write-Section '生成 zip 压缩包'
    Compress-Archive -Path (Join-Path $outputDir '*') -DestinationPath $zipPath -CompressionLevel Optimal
    $zipSizeMb = [math]::Round((Get-Item -LiteralPath $zipPath).Length / 1MB, 1)
    Write-Host "  $zipPath（$zipSizeMb MB）" -ForegroundColor DarkGray
}

# ---------- 汇总 ----------

Write-Host ''
Write-Host '构建完成' -ForegroundColor Green
Write-Host "  模式    : $Mode"
Write-Host "  输出目录: $outputDir（$totalSizeMb MB）"
Write-Host "  主程序  : $exePath"
if ($zipPath) {
    Write-Host "  压缩包  : $zipPath"
}
Write-Host '  启动    : 双击 Atlas.exe（会弹 UAC），或运行 run-atlas.cmd'

if ($Mode -eq 'Framework') {
    Write-Host ''
    Write-Host '提示：这是框架依赖构建，目标机需要安装 .NET 8 Desktop Runtime；免运行时请用 -Mode SelfContained 或 -Mode SingleFile。' -ForegroundColor Yellow
}
