@echo off
set "HSRC_RESTORE_EXE=%~dp0HearthstoneReconnector.exe"
powershell -NoProfile -Command "Start-Process -FilePath $env:HSRC_RESTORE_EXE -ArgumentList '--restore' -Verb RunAs -Wait"
