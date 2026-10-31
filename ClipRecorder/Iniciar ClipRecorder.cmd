@echo off
rem Abre ClipRecorder sin ventana de consola. Solo hace falta la primera vez:
rem despues se crean los accesos directos en el Escritorio y en el inicio de Windows.
start "" /min "%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0ClipRecorder.ps1"
