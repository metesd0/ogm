@echo off
setlocal EnableExtensions
title Ogm Ajan Kaldirici

:: Yonetici yetkisi kontrolu
net session >nul 2>&1
if not %errorlevel%==0 (
    echo [BILGI] Yonetici yetkisi isteniyor...
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

echo ===================================================
echo           Ogm Print Agent Kaldirma
echo ===================================================
echo.

set SERVICE_NAME=OgmPrintAgent
set INSTALL_DIR=%ProgramFiles%\Ogm\Agent

echo [1/3] Servis durduruluyor...
sc stop "%SERVICE_NAME%" >nul 2>&1
timeout /t 2 /nobreak >nul

echo [2/3] Servis kaldiriliyor...
sc delete "%SERVICE_NAME%" >nul 2>&1

:: Calisan herhangi bir Ogm.Agent sureci varsa sonlandir
taskkill /F /IM Ogm.Agent.exe >nul 2>&1

echo [3/3] Kurulum dosyalari temizleniyor...
if exist "%INSTALL_DIR%" (
    rmdir /S /Q "%INSTALL_DIR%" >nul 2>&1
)

echo.
echo ===================================================
echo  Ogm Ajan bilgisayardan basariyla kaldirildi!
echo ===================================================
echo.
pause
