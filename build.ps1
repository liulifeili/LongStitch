<#
  LongStitch 一键构建脚本。

  用法：
    .\build.ps1                 编译 + 跑精度电池
    .\build.ps1 -Publish        再额外发布免安装单文件到 publish\
    .\build.ps1 -Only png       只跑某一组电池（透传给 --only）

  任意一步失败立刻以非 0 退出码结束，方便 CI 直接判。

  说明：脚本里的可执行字符串保持 ASCII，中文只出现在注释里。
  原因是 Windows PowerShell 5.1 读取"无 BOM 的 UTF-8"时会按 ANSI 解码，
  中文出现在字符串字面量里会变成乱码甚至改变判断；放在注释里则无害。
  想要中文输出请用 pwsh（PowerShell 7），或先执行 chcp 65001。
#>

[CmdletBinding()]
param(
    [switch]$Publish,
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

Step 'dotnet build (0 warnings expected)' { dotnet build --nologo -v minimal }

Step 'precision battery' {
    if ($Only) { dotnet run --project checks/LongStitch.Checks --no-build -- --only $Only }
    else       { dotnet run --project checks/LongStitch.Checks --no-build }
}

if ($Publish) {
    Step 'publish single-file win-x64' {
        dotnet publish src/LongStitch.App -c Release -r win-x64 --self-contained `
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
    }
    $exe = Join-Path $PSScriptRoot 'publish/LongStitch.exe'
    if (Test-Path $exe) {
        $mb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
        Write-Host ''
        Write-Host "artifact: $exe  ($mb MB)" -ForegroundColor Green
    }
}

Write-Host ''
Write-Host 'ALL GREEN' -ForegroundColor Green
