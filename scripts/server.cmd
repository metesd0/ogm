@echo off
setlocal EnableExtensions

REM =====================================================================
REM  Ogm Sunucu - yayinlama ve calistirma scripti
REM
REM  Kullanim:
REM    server.cmd publish          -> tek dosya (single-file) yayinlar
REM    server.cmd run              -> yayinlanan sunucuyu calistirir
REM    server.cmd debug            -> gelistirme modunda calistirir (dotnet run)
REM    server.cmd smoke            -> uctan uca duman testini calistirir
REM
REM  Ikinci parametre ile RID secilebilir (varsayilan: win-x64):
REM    server.cmd publish win-arm64
REM =====================================================================

set ACTION=%~1
if "%ACTION%"=="" set ACTION=help

set RID=%~2
if "%RID%"=="" set RID=win-x64

set ROOT=%~dp0..
set PROJECT=%ROOT%\src\Ogm.Server\Ogm.Server.csproj
set DIST_DIR=%ROOT%\dist\server\%RID%
set EXE=%DIST_DIR%\Ogm.Server.exe

if /I "%ACTION%"=="publish" goto :publish
if /I "%ACTION%"=="run" goto :run
if /I "%ACTION%"=="debug" goto :debug
if /I "%ACTION%"=="smoke" goto :smoke
goto :help

:publish
echo [publish] Ogm Sunucu yayinlaniyor (RID=%RID%, single-file, self-contained)...
dotnet publish "%PROJECT%" -c Release -r %RID% --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -o "%DIST_DIR%"
if errorlevel 1 (
  echo [publish] HATA: Yayinlama basarisiz.
  exit /b 1
)
echo [publish] Tamamlandi: %EXE%
echo [publish] Panel dosyalari (wwwroot) ve appsettings.json exe'nin yanindadir.
goto :eof

:run
if not exist "%EXE%" (
  echo [run] HATA: %EXE% bulunamadi. Once "server.cmd publish" calistirin.
  exit /b 1
)
echo [run] Sunucu baslatiliyor... Panel: http://localhost:5099
"%EXE%"
goto :eof

:debug
echo [debug] Sunucu gelistirme modunda baslatiliyor... Panel: http://localhost:5099
dotnet run --project "%PROJECT%" -c Debug
goto :eof

:smoke
if not exist "%EXE%" (
  echo [smoke] Once "server.cmd publish" calistirin.
  exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0smoke-test.ps1" -ServerExe "%EXE%"
goto :eof

:help
echo Ogm Sunucu scripti.
echo   server.cmd publish [rid]
echo   server.cmd run
echo   server.cmd debug
echo   server.cmd smoke
goto :eof
