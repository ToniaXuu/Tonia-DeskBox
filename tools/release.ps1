# 本地一键出包：发布单文件 → 编译安装包 → 提示如何发版
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File tools\release.ps1 -Version 0.2.1
#
# 说明：正式发版建议走 CI（推送 tag 即自动出包并上传 Release）。
# 这个脚本用于本地快速验证或应急出包，不负责上传。

param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "版本号必须形如 0.2.1（当前：$Version）"
}

# ISCC 可能装在「每用户」或「所有用户」位置，两处都探
$isccCandidates = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    throw "没找到 ISCC.exe，请先安装 Inno Setup 6：winget install JRSoftware.InnoSetup"
}

Push-Location $root
try {
    if (-not $SkipPublish) {
        Write-Host "[1/3] 发布 self-contained 单文件 …" -ForegroundColor Cyan
        dotnet publish src/ToniaDeskBox/ToniaDeskBox.csproj `
            -c Release -r win-x64 --self-contained true `
            -p:Version=$Version -o publish
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败：$LASTEXITCODE" }
    }
    else {
        Write-Host "[1/3] 跳过发布（沿用 publish\ 现有产物）" -ForegroundColor DarkGray
    }

    $exe = Join-Path $root 'publish\ToniaDeskBox.exe'
    if (-not (Test-Path $exe)) { throw "缺少 publish\ToniaDeskBox.exe" }
    $exeVersion = (Get-Item $exe).VersionInfo.ProductVersion
    if ($exeVersion -ne $Version) {
        throw "publish 产物版本是 $exeVersion，与指定版本 $Version 不一致（是不是忘了 -SkipPublish？）"
    }

    Write-Host "[2/3] 编译安装包 …" -ForegroundColor Cyan
    & $iscc "/DAppVersion=$Version" 'installer\ToniaDeskBox.iss'
    if ($LASTEXITCODE -ne 0) { throw "ISCC 编译失败：$LASTEXITCODE" }

    $setup = Join-Path $root "dist\ToniaDeskBox-Setup-$Version.exe"
    if (-not (Test-Path $setup)) { throw "没有生成 $setup" }

    Write-Host "[3/3] 完成" -ForegroundColor Green
    Write-Host ("      安装包：{0}（{1:N1} MB）" -f $setup, ((Get-Item $setup).Length / 1MB))
    Write-Host ""
    Write-Host "要让「检查更新」看到这一版，推送同名 tag 即可（CI 会重新出包并上传 Release）：" -ForegroundColor Yellow
    Write-Host "    git tag v$Version && git push origin v$Version"
}
finally {
    Pop-Location
}
