@echo off
rem Register MultiCAT as the OmniRig COM server (one-time, needs admin).
rem The exe self-elevates; accept the Windows prompt.
"%~dp0MultiCat.OmniRig.exe" --register
pause
