@echo off
rem Dat lai mat khau quan tri - BAN THU NGHIEM (CSDL Test). Tat cua so "HAK thu nghiem" truoc khi chay.
cd /d "%~dp0"
set Hak__Environment=Test
set /p HAKUSER=Ten dang nhap quan tri (ban thu nghiem): 
"%~dp0HAK.exe" --reset-admin-password %HAKUSER%
pause
