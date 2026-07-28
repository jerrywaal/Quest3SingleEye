param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourceFile = Join-Path $projectRoot "src\Quest3SingleEye.cs"
$outputDir = Join-Path $projectRoot "release"
$outputExe = Join-Path $outputDir "Quest3SingleEye.exe"
$compiler = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$icon = Join-Path $projectRoot "app.ico"

if (-not (Test-Path -LiteralPath $compiler)) {
    throw "未找到 .NET Framework C# 编译器：$compiler"
}

New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

$arguments = @(
    "/nologo",
    "/target:winexe",
    "/optimize+",
    "/platform:anycpu",
    "/codepage:65001",
    "/reference:System.dll",
    "/reference:System.Core.dll",
    "/reference:System.Drawing.dll",
    "/reference:System.Windows.Forms.dll",
    "/out:$outputExe"
)

if (Test-Path -LiteralPath $icon) {
    $arguments += "/win32icon:$icon"
}

$arguments += $sourceFile
& $compiler $arguments
if ($LASTEXITCODE -ne 0) {
    throw "编译失败，退出代码：$LASTEXITCODE"
}

Copy-Item -LiteralPath (Join-Path $projectRoot "Quest3SingleEye.exe.config") -Destination ($outputExe + ".config") -Force
Write-Host "构建完成：$outputExe"
