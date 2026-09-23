<#
.SYNOPSIS
    发布一个插件版本到 GitHub Releases：打包 → 建发行版 → 上传附件 → 逐个核对。

.DESCRIPTION
    与 ClassShout 主仓库的 release.ps1 同一套路数，理由也一样：
    手工发布时的风险不是"传错文件"，而是**传没传完没人知道** ——
    Releases 的附件列表是唯一的交付凭据，而命令行上传既不会在失败时中止整个流程，
    也不会回头核对结果。

    所以上传之后必须回查：每个附件的大小、以及发行版的附件数量与名字，
    任何一项对不上都以非零码退出。

    Tag 规范：ClassIsland 插件市场要求严格是 **a.b.c.d**（如 1.0.1.0），
    带 v 前缀或不完整的 Tag 会被索引生成器直接忽略，而这一条在发布时没有任何反馈。

.PARAMETER Version
    版本号，形如 1.0.1.0。必须与 manifest.yml 里的 version 一致 ——
    否则发出去的包自称另一个版本，而插件市场按 Tag 索引，两边对不上。

.PARAMETER NotesFile
    发行说明的 Markdown 文件。

.PARAMETER Token
    GitHub 令牌。默认依次读环境变量 GITHUB_TOKEN、GH_TOKEN。不会打印到输出里。

.PARAMETER Proxy
    访问 GitHub 用的代理，例如 http://127.0.0.1:7890。不填则直连。

.PARAMETER SkipPack
    跳过打包，直接用现有的 cipx\（调试脚本本身时用）。

.EXAMPLE
    pwsh ./scripts/release.ps1 -Version 1.0.1.0 -NotesFile notes.md -Proxy http://127.0.0.1:7890
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$NotesFile,

    [string]$Token,
    [string]$Proxy,

    [switch]$SkipPack
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot

$apiBase = 'https://api.github.com/repos/WRD1145/ClassShoutCiPlugin'

function Invoke-GitHub {
    param(
        [string]$Method,
        [string]$Uri,
        [object]$Body,
        [string]$InFile,
        [string]$ContentType
    )

    $params = @{
        Method  = $Method
        Uri     = $Uri
        Headers = @{
            Authorization = "Bearer $resolvedToken"
            Accept        = 'application/vnd.github+json'
            'User-Agent'  = 'ClassShoutCiPlugin-release'
        }
    }

    if ($Proxy) { $params.Proxy = $Proxy }
    if ($Body) { $params.Body = ($Body | ConvertTo-Json -Depth 5); $params.ContentType = 'application/json' }
    if ($InFile) { $params.InFile = $InFile; $params.ContentType = $ContentType }

    Invoke-RestMethod @params
}

try {
    # ---------- 前置检查：先把"还没开始就已经错了"的事挡掉 ----------

    if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') {
        throw "版本号格式不对：$Version（插件市场的 Tag 必须严格是 a.b.c.d，不能带 v 前缀）"
    }

    $manifest = Get-Content 'manifest.yml' -Raw
    $manifestVersion = if ($manifest -match '(?m)^version:\s*(\S+)\s*$') { $Matches[1] } else { '' }

    if ($manifestVersion -ne $Version) {
        throw "manifest.yml 里的 version 是 '$manifestVersion'，与要发布的 $Version 不一致"
    }

    $resolvedToken = if ($Token) { $Token }
        elseif ($env:GITHUB_TOKEN) { $env:GITHUB_TOKEN }
        elseif ($env:GH_TOKEN) { $env:GH_TOKEN }
        else { throw '没有可用的 GitHub 令牌：请用 -Token，或设置 GITHUB_TOKEN / GH_TOKEN' }

    if (-not (Test-Path $NotesFile)) {
        throw "发行说明文件不存在：$NotesFile"
    }

    $dirty = git status --porcelain
    if ($dirty) {
        throw "工作区不干净，先提交再发布：`n$dirty"
    }

    $head = git rev-parse --short HEAD
    Write-Host "发布 ClassIsland 插件 $Version（提交 $head）" -ForegroundColor Cyan

    # ---------- 打包 ----------

    if (-not $SkipPack) {
        & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'package.ps1')
        if ($LASTEXITCODE -ne 0) { throw "打包失败（退出码 $LASTEXITCODE）" }
    }

    $package = Get-ChildItem 'cipx' -Filter '*.cipx' -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $package) {
        throw 'cipx\ 下没有 .cipx —— 先跑 scripts\package.ps1'
    }

    # ---------- 建发行版（已存在就复用） ----------

    $release = $null
    try {
        $release = Invoke-GitHub -Method Get -Uri "$apiBase/releases/tags/$Version"
    }
    catch {
        $release = $null
    }

    if ($release) {
        Write-Host "  发行版 $Version 已存在，往它上面补附件" -ForegroundColor Yellow
    }
    else {
        $release = Invoke-GitHub -Method Post -Uri "$apiBase/releases" -Body @{
            tag_name         = $Version
            target_commitish = 'main'
            name             = $Version
            body             = (Get-Content $NotesFile -Raw)
        }
        Write-Host "  已创建发行版 $Version" -ForegroundColor Green
    }

    # ---------- 上传附件 ----------

    $assetName = $package.Name
    $existing = $release.assets | Where-Object { $_.name -eq $assetName }

    if ($existing) {
        Write-Host "  同名附件已存在，先删掉旧的（避免出现两个同名资产）" -ForegroundColor Yellow
        Invoke-GitHub -Method Delete -Uri "$apiBase/releases/assets/$($existing.id)" | Out-Null
    }

    $uploaded = Invoke-GitHub -Method Post `
        -Uri "https://uploads.github.com/repos/WRD1145/ClassShoutCiPlugin/releases/$($release.id)/assets?name=$assetName" `
        -InFile $package.FullName `
        -ContentType 'application/octet-stream'

    # ---------- 核对：只看"命令都执行完了"是不够的 ----------

    if ($uploaded.size -ne $package.Length) {
        throw "上传后大小不符：本地 $($package.Length) 字节，远端 $($uploaded.size) 字节"
    }

    $release = Invoke-GitHub -Method Get -Uri "$apiBase/releases/tags/$Version"
    $names = ($release.assets | ForEach-Object { $_.name }) -join '、'

    Write-Host ''
    Write-Host ('=' * 60)
    Write-Host "发行版 $Version 的附件（$($release.assets.Count) 个）：$names" -ForegroundColor Green
    Write-Host "  $assetName  $([math]::Round($uploaded.size / 1KB, 1)) KB" -ForegroundColor Green
    Write-Host ''
    Write-Host (Get-Content 'cipx\checksums.md' -Raw)
}
finally {
    Pop-Location
}
