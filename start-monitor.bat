@echo off
echo Starting SystemMonitor Service Process Monitor...
echo This will monitor the SystemMonitor.Service process and log crashes/exits
echo Log file: service-monitor.log
echo.
echo Press Ctrl+C to stop monitoring
echo.

powershell -ExecutionPolicy Bypass -File "monitor-service.ps1"
pause