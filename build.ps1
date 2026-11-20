<#
  LongStitch 一键构建脚本。

  用法：
    .\build.ps1                 编译 + 跑精度电池
    .\build.ps1 -Publish        发布绿色版文件夹到 publish\（含空的 data\ 数据文件夹）
    .\build.ps1 -Zip            把 publish\ 打成绿色版 zip（要先跑过 -Publish）
    .\build.ps1 -Installer      用 Inno Setup 6 编译安装器（要先跑过 -Publish）
    .\build.ps1 -Only png       只跑某一组电池（透传给 --only）

  任意一步失败立刻以非 0 退出码结束，方便 CI 直接判。

  第六十四改起不再做单文件发布：自包含单文件每次启动都要把原生库自解压到
  %TEMP%\.net\（永远在 C 盘），与"少在 C 盘堆文件"的发布要求冲突；
  文件夹发布没有任何启动期写入，数据统一落进 exe 旁的 data\ 文件夹（跟随所在盘）。

  说明：脚本里的可执行字符串保持 ASCII，中文只出现在注释里。
  原因是 Windows PowerShell 5.1 读取"无 BOM 的 UTF-8"时会按 ANSI 解码，
  中文出现在字符串字面量里会变成乱码甚至改变判断；放在注释里则无害。
  想要中文输出请用 pwsh（PowerShell 7），或先执行 chcp 65001。
#>

[CmdletBinding()]
param(
    [switch]$Publish,
    [switch]$Zip,
    [switch]$Installer,
    [string]$Only
)

$ErrorActionPreference = 'Stop'
Set-Location -Path $PSScriptRoot

function Step($name, $body) {
    Write-Host ''
    Write-Host "==> $name" -ForegroundColor Cyan
    & $body
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $name (exit $LASTEXITCODE)" -ForegroundColor Red
        exit $LASTEXITCODE
    }
}

# 从 csproj 里抠 <Version>，绿色版 zip 与安装器的文件名都按它命名
function Get-AppVersion {
    $line = Select-String -Path 'src/LongStitch.App/LongStitch.App.csproj' -Pattern '<Version>(.*)</Version>'
    return $line.Matches[0].Groups[1].Value.Trim()
}

Step 'dotnet build (0 warnings expected)' { dotnet build --nologo -v minimal }

Step 'precision battery' {
    if ($Only) { dotnet run --project checks/LongStitch.Checks --no-build -- --only $Only }
    else       { dotnet run --project checks/LongStitch.Checks --no-build }
}

if ($Publish) {
    Step 'publish folder win-x64 (self-contained, NOT single-file)' {
        dotnet publish src/LongStitch.App -c Release -r win-x64 --self-contained -o publish
    }
    # data\ 是数据根目录的开关：exe 旁有这个文件夹，偏好/最近项目/日志就都落在这里面。
    # 空文件夹也要存在——Compress-Archive 会丢空目录，所以 zip 那步用 Windows 自带的 bsdtar。
    New-Item -ItemType Directory -Force -Path (Join-Path $PSScriptRoot 'publish/data') | Out-Null
    $exe = Join-Path $PSScriptRoot 'publish/LongStitch.exe'
    $files = (Get-ChildItem -Recurse -File (Join-Path $PSScriptRoot 'publish')).Count
    if (Test-Path $exe) {
        $mb = [math]::Round(((Get-ChildItem -Recurse -File (Join-Path $PSScriptRoot 'publish') |
            Measure-Object Length -Sum).Sum) / 1MB, 1)
        Write-Host ''
        Write-Host "artifact: $exe  ($files files, $mb MB total)" -ForegroundColor Green
    }
}

if ($Zip) {
    $version = Get-AppVersion
    $zipPath = Join-Path $PSScriptRoot "out/LongStitch-win-x64-v$version.zip"
    New-Item -ItemType Directory -Force -Path (Join-Path $PSScriptRoot 'out') | Out-Null
    Step "pack green zip -> out/LongStitch-win-x64-v$version.zip" {
        # tar -a 按扩展名选 zip 格式（Windows 10+ 自带 bsdtar）：保留空目录与中文文件名
        tar -a -c -f $zipPath -C (Join-Path $PSScriptRoot 'publish') .
    }
    Write-Host "artifact: $zipPath" -ForegroundColor Green
}

if ($Installer) {
    # Inno Setup 6 默认装在 Program Files (x86) 下（ISCC.exe 是 32 位工具链，但产物没问题）
    $isccCandidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )
    $iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) {
        Write-Host 'FAILED: ISCC.exe not found. Install Inno Setup 6 first (https://jrsoftware.org/isinfo.php)' -ForegroundColor Red
        exit 1
    }
    Step 'compile installer (Inno Setup 6)' {
        & $iscc (Join-Path $PSScriptRoot 'installer/LongStitch.iss')
    }
    $version = Get-AppVersion
    Write-Host "artifact: $(Join-Path $PSScriptRoot "out/LongStitch-installer-v$version.exe")" -ForegroundColor Green
}

Write-Host ''
Write-Host 'ALL GREEN' -ForegroundColor Green
