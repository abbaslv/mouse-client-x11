@echo off
setlocal
set "ROOT=%~dp0"
set "EXE=%ROOT%bin\X11BatteryTray.exe"

if not exist "%EXE%" (
    if exist "%ROOT%X11BatteryTray.exe" (
        set "EXE=%ROOT%X11BatteryTray.exe"
    ) else (
        echo X11BatteryTray.exe not found. Building from source...
        powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%src\X11BatteryTrayNative\build.ps1"
        if errorlevel 1 (
            echo.
            echo Build failed. You can also download a prebuilt binary from GitHub Releases.
            pause
            exit /b 1
        )
    )
)

if "%~1"=="" (
    start "" "%EXE%"
) else (
    "%EXE%" %*
)
