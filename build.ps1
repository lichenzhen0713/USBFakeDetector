# Build UsbFakeDriveDetector (requires only .NET Framework 4.x csc.exe)
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$srcDir = Join-Path $root "src"
$binDir = Join-Path $root "bin"

$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe"
}
if (-not (Test-Path $csc)) {
    throw "Cannot find .NET Framework 4.x csc.exe"
}

$sources = Get-ChildItem (Join-Path $srcDir "*.cs") | ForEach-Object { $_.FullName }
if (-not $sources) { throw "No .cs source files found in src" }

New-Item -ItemType Directory -Force -Path $binDir | Out-Null

$references = @(
    "/r:System.dll",
    "/r:System.Core.dll",
    "/r:System.Drawing.dll",
    "/r:System.Windows.Forms.dll",
    "/r:System.Management.dll"
)
$common = @("/nologo", "/optimize+", "/unsafe+", "/codepage:65001") + $references

Write-Host "Compiling GUI ..."
& $csc @common "/target:winexe" "/main:UsbCheck.Program" "/out:$binDir\UsbFakeDriveDetector.exe" $sources
if ($LASTEXITCODE -ne 0) { throw "GUI build failed (exit $LASTEXITCODE)" }

Write-Host "Compiling self-test ..."
& $csc @common "/target:exe" "/main:UsbCheck.SelfTestMain" "/out:$binDir\selftest.exe" $sources
if ($LASTEXITCODE -ne 0) { throw "Self-test build failed (exit $LASTEXITCODE)" }

Write-Host ""
Write-Host "Build OK:" -ForegroundColor Green
Get-ChildItem $binDir -Filter *.exe |
    Select-Object Name, @{ Name = "SizeKB"; Expression = { [math]::Round($_.Length / 1KB) } } |
    Format-Table -AutoSize
