<#
.SYNOPSIS
    为每个受支持的 ClassIsland 版本各出一份插件包。

.DESCRIPTION
    ClassIsland 2.1.0.1 与 2.1.1.1 之间引入了破坏性变更（与本插件用到的 API 无关，
    但程序集版本变了），针对一个版本编译出来的插件装到另一个版本上不保证能加载。
    所以两份分别编译、分别打包。

    每一份的 manifest.yml 里 apiVersion 必须等于它编译时引用的 SDK 版本：
      清单说"能在 2.1.0.1 以上工作"，而二进制是拿 2.1.1.1 编的 —— 这种错配
      在安装那一刻不会被发现，等到调用 API 才炸，且现象极难归因。

    默认发行版是 apiVersion 更低的那份（2.1.0.1），因为它覆盖的宿主更多。

.EXAMPLE
    pwsh ./scripts/package.ps1
#>
[CmdletBinding()]
param(
    # 要出的版本，顺序即发行版推荐顺序：第一个是主推的那份。
    [string[]]$Variants = @('2.1.0.1', '2.1.1.1'),

    [string]$Configuration = 'Release',
    [string]$OutputRoot = 'dist'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot

try {
    if (Test-Path $OutputRoot) {
        Remove-Item $OutputRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null

    $built = @()

    foreach ($variant in $Variants) {
        Write-Host ''
        Write-Host "==== 针对 ClassIsland $variant 编译 ====" -ForegroundColor Cyan

        & dotnet build -c $Configuration -p:ClassIslandPluginSdkVersion=$variant --nologo -v q
        if ($LASTEXITCODE -ne 0) {
            throw "针对 $variant 的编译失败（退出码 $LASTEXITCODE）"
        }

        $binDir = Join-Path $repoRoot "bin\$Configuration\net10.0-windows"
        if (-not (Test-Path $binDir)) {
            throw "找不到构建输出：$binDir"
        }

        # 整份产物拷出来，再按这一份的目标版本改写清单里的 apiVersion
        $stage = Join-Path $OutputRoot "classshout.bridge-$variant"
        New-Item -ItemType Directory -Force -Path $stage | Out-Null
        Copy-Item (Join-Path $binDir '*') $stage -Recurse -Force

        $manifestPath = Join-Path $stage 'manifest.yml'
        $manifest = Get-Content $manifestPath -Raw
        $stamped = $manifest -replace '(?m)^apiVersion:\s*\S+\s*$', "apiVersion: $variant"

        if ($stamped -eq $manifest -and $manifest -notmatch "(?m)^apiVersion:\s*$([regex]::Escape($variant))\s*$") {
            throw "改写 $variant 的 apiVersion 没有生效 —— 清单里没有匹配到 apiVersion 行"
        }

        Set-Content $manifestPath $stamped -NoNewline -Encoding utf8

        # 立刻核对，别让"清单没改成功"混进发行版
        $actual = (Select-String -Path $manifestPath -Pattern '^apiVersion:\s*(\S+)').Matches[0].Groups[1].Value
        if ($actual -ne $variant) {
            throw "清单里的 apiVersion 是 $actual，期望 $variant"
        }

        # 只留运行时要用的东西，别把 pdb、deps 之类一起发出去
        foreach ($pattern in '*.pdb', '*.deps.json', 'classshout-bridge.log', '*.xml') {
            Get-ChildItem $stage -Filter $pattern -File -ErrorAction SilentlyContinue | Remove-Item -Force
        }

        $zip = Join-Path $OutputRoot "ClassShoutCiPlugin-$variant.zip"
        Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
        Remove-Item $stage -Recurse -Force

        $size = [math]::Round((Get-Item $zip).Length / 1KB, 1)
        Write-Host "  → $zip（$size KB，apiVersion=$actual）" -ForegroundColor Green
        $built += [pscustomobject]@{ Variant = $variant; Zip = $zip; ApiVersion = $actual }
    }

    Write-Host ''
    Write-Host ('=' * 60)
    Write-Host "共打出 $($built.Count) 份：" -ForegroundColor Green
    foreach ($b in $built) {
        Write-Host ("  {0,-14} {1}" -f $b.Variant, (Split-Path $b.Zip -Leaf))
    }
    Write-Host ''
    Write-Host "发行版推荐用 $($built[0].Variant) 那份：它的 apiVersion 更低，覆盖的宿主更多。" -ForegroundColor Yellow
}
finally {
    Pop-Location
}
