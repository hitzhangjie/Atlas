<#
.SYNOPSIS
    构建「当前分支」的 Atlas，并按分支自动区分输出目录。

.DESCRIPTION
    build.ps1 构建的是「脚本所在检出」的当前分支/工作区，本身并不绑定 main。
    本脚本是它的薄包装，解决两个日常问题：
      1) 构建前明确打印当前分支 @ commit、工作区和 worktree 状态，避免拿错产物；
      2) 非 main 分支默认输出到 dist-<分支>，不会覆盖 dist\ 里的正式版。
    其余参数与 build.ps1 完全一致，直接透传。

.PARAMETER Mode
    Framework（默认）/ SelfContained / SingleFile。

.PARAMETER Configuration
    Release（默认）/ Debug。

.PARAMETER Runtime
    目标运行时标识，默认 win-x64。

.PARAMETER Output
    自定义输出目录；不指定时 main → dist，其他分支 → dist-<分支>。

.PARAMETER Zip
    发布后打包到 artifacts\。

.PARAMETER NoClean
    不先清空输出目录。

.PARAMETER StopAtlas
    构建前尝试结束正在运行的 Atlas（需要管理员权限终端）。

.PARAMETER AllowRunning
    检测到 Atlas 运行时也继续构建（输出到其他目录时使用；本脚本在输出不是 dist 时会自动开启）。

.EXAMPLE
    .\build-branch.ps1
    构建当前分支：main 输出 dist\，其他分支输出 dist-<分支>\。

.EXAMPLE
    .\build-branch.ps1 -Mode SingleFile -Zip
    当前分支构建单文件版并打包。

.EXAMPLE
    .\build-branch.ps1 -Output D:\Temp\atlas-dev
    输出到指定目录（构建的仍然是当前分支）。
#>
#Requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Framework', 'SelfContained', 'SingleFile')]
    [string]$Mode = 'Framework',

    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [string]$Runtime = 'win-x64',

    [string]$Output,

    [switch]$Zip,

    [switch]$NoClean,

    [switch]$StopAtlas,

    [switch]$AllowRunning
)

Set-StrictMode -Version 1.0
$ErrorActionPreference = 'Stop'

function Fail {
    param([string]$Text)
    Write-Host ''
    Write-Host "错误：$Text" -ForegroundColor Red
    exit 1
}

$repoRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
$buildScript = Join-Path $repoRoot 'build.ps1'
if (-not (Test-Path -LiteralPath $buildScript -PathType Leaf)) {
    Fail "找不到 build.ps1：$buildScript。build-branch.ps1 需要和 build.ps1 放在同一个仓库根目录。"
}

$gitCommand = Get-Command -Name git -CommandType Application -ErrorAction SilentlyContinue
if (-not $gitCommand) {
    Fail '未找到 git 命令。build-branch.ps1 需要 git 来识别当前分支；也可以直接使用 build.ps1。'
}

$branchName = $null
try {
    $branchOut = @(& git -C $repoRoot rev-parse --abbrev-ref HEAD 2>$null)
    if ($LASTEXITCODE -eq 0 -and $branchOut.Count -gt 0) { $branchName = "$($branchOut[0])".Trim() }
}
catch { }

if (-not $branchName) {
    Fail "无法识别当前分支（$repoRoot 不是 git 仓库或 git 调用失败）。"
}

$commitHash = ''
try {
    $commitOut = @(& git -C $repoRoot rev-parse --short HEAD 2>$null)
    if ($LASTEXITCODE -eq 0 -and $commitOut.Count -gt 0) { $commitHash = "$($commitOut[0])".Trim() }
}
catch { }

$dirtyFiles = @()
try { $dirtyFiles = @(& git -C $repoRoot status --porcelain 2>$null) } catch { }
$isWorktree = Test-Path -LiteralPath (Join-Path $repoRoot '.git') -PathType Leaf

# 未指定输出目录时按分支区分：main 保持 dist，其他分支用 dist-<分支>
$safeBranch = ($branchName -replace '[^A-Za-z0-9._-]', '-')
if ($branchName -eq 'HEAD') {
    if ($commitHash) { $safeBranch = "detached-$commitHash" } else { $safeBranch = 'detached' }
}

if (-not $PSBoundParameters.ContainsKey('Output') -or [string]::IsNullOrWhiteSpace($Output)) {
    if ($branchName -eq 'main' -or $branchName -eq 'master') {
        $Output = 'dist'
    }
    else {
        $Output = "dist-$safeBranch"
    }
}

$outputFull = if ([IO.Path]::IsPathRooted($Output)) {
    [IO.Path]::GetFullPath($Output).TrimEnd('\')
}
else {
    [IO.Path]::GetFullPath((Join-Path $repoRoot $Output)).TrimEnd('\')
}
$defaultDist = [IO.Path]::GetFullPath((Join-Path $repoRoot 'dist')).TrimEnd('\')

$sourceLabel = if ($commitHash) { "$branchName @ $commitHash" } else { $branchName }

Write-Host ''
Write-Host '==> 当前分支构建' -ForegroundColor Cyan
Write-Host "  分支    : $sourceLabel"
Write-Host "  目录    : $repoRoot"
Write-Host "  输出    : $outputFull"
if ($isWorktree) {
    Write-Host '  提示    : 当前目录是一个 git worktree，构建的是该 worktree 检出的分支。' -ForegroundColor DarkGray
}
if ($dirtyFiles.Count -gt 0) {
    Write-Host "  警告    : 工作区有 $($dirtyFiles.Count) 个未提交修改，构建产物会包含这些修改。" -ForegroundColor Yellow
}

# 输出不是 dist 时，Atlas 正在运行也不会占用该目录，自动允许继续构建
$buildArgs = @{
    Mode          = $Mode
    Configuration = $Configuration
    Runtime       = $Runtime
    Output        = $Output
}
if ($Zip) { $buildArgs['Zip'] = $true }
if ($NoClean) { $buildArgs['NoClean'] = $true }
if ($StopAtlas) { $buildArgs['StopAtlas'] = $true }
if ($AllowRunning) { $buildArgs['AllowRunning'] = $true }
elseif (-not $StopAtlas -and $outputFull -ne $defaultDist) { $buildArgs['AllowRunning'] = $true }

& $buildScript @buildArgs
$exitCode = $LASTEXITCODE
if ($exitCode -ne 0) {
    Fail "build.ps1 构建失败（退出码 $exitCode）。"
}

Write-Host ''
Write-Host "当前分支构建完成：$sourceLabel → $outputFull" -ForegroundColor Green
Write-Host '提示：产物 ProductVersion 里带构建时的 commit hash，可用于核对构建的是哪个提交。' -ForegroundColor DarkGray
exit 0
