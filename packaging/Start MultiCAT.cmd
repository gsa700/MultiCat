@echo off
rem Launch the MultiCAT service (background) then the app window.
cd /d "%~dp0"
echo Starting MultiCAT service...
start "MultiCAT Service" /min "%~dp0MultiCat.Service\MultiCat.Service.exe"
rem Give the service a moment to open its control pipe.
timeout /t 2 /nobreak >nul
echo Starting MultiCAT window...
start "" "%~dp0MultiCat.Gui\MultiCat.Gui.exe"
