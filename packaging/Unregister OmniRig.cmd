@echo off
rem Remove MultiCAT's OmniRig COM server registration (needs admin).
rem The exe self-elevates; accept the Windows prompt.
"%~dp0MultiCat.OmniRig.exe" --unregister
pause
