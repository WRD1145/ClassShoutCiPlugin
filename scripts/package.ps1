<#
.SYNOPSIS
    打出可发布的 ClassIsland 插件包（.cipx）。

.DESCRIPTION
    打包用 SDK 自带的一键打包：dotnet publish -p:CreateCipx=true。
    它在 cipx\ 下产出 .cipx 与 checksums.md（ClassIsland 插件市场要的 MD5 校验信息）。

    .cipx 实际就是一个 zip，**根目录里必须有 manifest.yml**，安装时会被解压到
    Plugins\<插件 id>\ 下（见 ClassIsland 源码 PluginService 的安装逻辑）。
    包里的 apiVersion 直接取自仓库里的 manifest.yml，所以两者天然一致 ——
    清单说"能在 2.1.0.1 以上工作"而二进制是拿别的版本编的，这种错配在安装那一刻
    不会被发现，要等调用 API 才炸，现象极难归因。

    默认只出 2.1.0.1：它的 apiVersion 更低、覆盖的宿主更多，是发行版用的那份。
    ClassIsland 2.1.1.1 目前处于开发预览，要单独试的时候用 -Variants 显式指定。

.EXAMPLE
    pwsh ./scripts/package.ps1
    pwsh ./scripts/package.ps1 -Variants 2.1.0.1, 2.1.1.1
#>
[CmdletBinding()]
param(
    [string[]]$Variants = @('2.1.0.1'),
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot

try {
    $packages = @()

    foreach ($variant in $Variants) {
        Write-Host ''
        Write-Host "==== 针对 ClassIsland $variant 打包 ====" -ForegroundColor Cyan

        # 每次从干净的 cipx 目录开始，避免把上一次的产物当成本次的结果
        Remove-Item (Join-Path $repoRoot 'cipx') -Recurse -Force -ErrorAction SilentlyContinue

        & dotnet publish -c $Configuration -p:ClassIslandPluginSdkVersion=$variant -p:CreateCipx=true --nologo -v q
        if ($LASTEXITCODE -ne 0) {
            throw "针对 $variant 的打包失败（退出码 $LASTEXITCODE）"
        }

        $package = Get-ChildItem (Join-Path $repoRoot 'cipx') -Filter '*.cipx' -File -ErrorAction SilentlyContinue |
            Select-Object -First 1

        if (-not $package) {
            throw "打包完成但 cipx\ 下没有 .cipx —— 确认传了 -p:CreateCipx=true"
        }

        # 核对包内清单：没有 manifest.yml 的包 ClassIsland 根本不会认，
        # apiVersion 与目标版本不符则是那种"装得上、一调 API 就炸"的错配。
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [System.IO.Compression.ZipFile]::OpenRead($package.FullName)

        try {
            $entry = $zip.Entries | Where-Object { $_.FullName -eq 'manifest.yml' }
            if (-not $entry) {
                throw "$($package.Name) 里没有 manifest.yml —— 这样的包 ClassIsland 根本不会认"
            }

            $reader = New-Object System.IO.StreamReader($entry.Open())
            $text = $reader.ReadToEnd()
            $reader.Dispose()

            $actual = ([regex]::Match($text, '(?m)^apiVersion:\s*(\S+)\s*$')).Groups[1].Value
            if ($actual -ne $variant) {
                throw "包内 apiVersion 是 '$actual'，期望 '$variant'"
            }

            # 许可文本只作提示、不当门槛：包本身能用才是第一位的。
            $hasLicense = ($zip.Entries | Where-Object { $_.FullName -eq 'LICENSE' }).Count -gt 0
            $licenseNote = if ($hasLicense) { '含许可文本' } else { '注意：包内没有许可文本' }

            $sizeKb = [math]::Round($package.Length / 1KB, 1)
            Write-Host "  → $($package.Name)（$sizeKb KB，apiVersion=$actual，$licenseNote）" -ForegroundColor Green
        }
        finally {
            $zip.Dispose()
        }

        $packages += $package
    }

    Write-Host ''
    Write-Host ('=' * 60)
    Write-Host "共打出 $($packages.Count) 份，校验信息见 cipx\checksums.md" -ForegroundColor Green
    foreach ($p in $packages) { Write-Host "  $($p.Name)" }

    Write-Host ''
    Write-Host '发布提醒：ClassIsland 插件市场的 Tag 必须严格是 a.b.c.d（如 1.0.0.0），' -ForegroundColor Yellow
    Write-Host '带 v 前缀或不完整的 Tag 会让索引生成器认不出你的插件。' -ForegroundColor Yellow
}
finally {
    Pop-Location
}
