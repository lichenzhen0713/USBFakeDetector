@echo off
rem Double-click this file to start the USB fake drive detector.
rem The program will request administrator privileges automatically.
cd /d "%~dp0"
if not exist "bin\UsbFakeDriveDetector.exe" (
    echo Program not found, building...
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
    if errorlevel 1 (
        echo Build failed.
        pause
        exit /b 1
    )
)
start "" "%~dp0bin\UsbFakeDriveDetector.exe"
