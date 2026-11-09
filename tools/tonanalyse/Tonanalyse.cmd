@echo off
rem Orbivo-Tonanalyse: Video auf diese Datei ziehen oder doppelklicken (dann öffnet sich ein Dateidialog).
chcp 65001 >nul
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tonanalyse.ps1" %*
echo.
pause
