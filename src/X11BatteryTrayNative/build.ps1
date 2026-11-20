$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$gcc = Join-Path $repoRoot ".tools\w64devkit\bin\gcc.exe"
if (!(Test-Path $gcc)) {
    $cmd = Get-Command gcc -ErrorAction SilentlyContinue
    if ($cmd) {
        $gcc = $cmd.Source
    } else {
        throw "gcc.exe not found at $gcc or in PATH. Install MinGW-w64 / w64devkit to build."
    }
}

$outDir = Join-Path $repoRoot "bin"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$outExe = Join-Path $outDir "X11BatteryTray.exe"

$sources = @(
    (Join-Path $PSScriptRoot "main.c"),
    (Join-Path $PSScriptRoot "mouse_manager.c"),
    (Join-Path $PSScriptRoot "backend_x11.c"),
    (Join-Path $PSScriptRoot "backend_compx.c"),
    (Join-Path $PSScriptRoot "backend_logitech.c"),
    (Join-Path $PSScriptRoot "backend_razer.c"),
    (Join-Path $PSScriptRoot "backend_stdhid.c"),
    (Join-Path $PSScriptRoot "backend_ble.c"),
    (Join-Path $PSScriptRoot "icon_renderer.c"),
    (Join-Path $PSScriptRoot "flyout_window.c")
)

$args = @(
    "-Os", "-s",
    "-municode", "-mwindows",
    "-Wall", "-Wextra", "-Werror",
    "-D_WIN32_WINNT=0x0A00",
    "-o", $outExe
) + $sources + @(
    "-luser32", "-lgdi32", "-lshell32", "-ladvapi32", "-lsetupapi", "-lhid"
)

& $gcc @args
if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE"
}

$info = Get-Item $outExe
Write-Output ("Built: {0} ({1:N1} KB)" -f $info.FullName, ($info.Length / 1024.0))
