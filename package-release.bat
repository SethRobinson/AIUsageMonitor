@echo off
setlocal

cd /d "%~dp0"

set "APP_NAME=SethsAIUsageMonitor"
set "PROJECT=AIUsageMonitor.csproj"
set "RUNTIME=win-x64"
set "ARTIFACTS_DIR=%CD%\artifacts"
set "BUILD_DIR=%CD%\build"
set "PUBLISH_DIR=%ARTIFACTS_DIR%\publish\%APP_NAME%"
set "ZIP_PATH=%ARTIFACTS_DIR%\%APP_NAME%-win-x64.zip"
set "PUBLISH_EXE=%PUBLISH_DIR%\%APP_NAME%.exe"
set "BUILD_EXE=%BUILD_DIR%\%APP_NAME%.exe"
set "SIGNTOOL_EXE=C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe"

taskkill /f /im "%APP_NAME%.exe" >nul 2>nul
taskkill /f /im "AIUsageMonitor.exe" >nul 2>nul

if exist "%PUBLISH_DIR%" rmdir /s /q "%PUBLISH_DIR%"
if exist "%ZIP_PATH%" del /q "%ZIP_PATH%"
if not exist "%ARTIFACTS_DIR%" mkdir "%ARTIFACTS_DIR%"
if not exist "%BUILD_DIR%" mkdir "%BUILD_DIR%"

dotnet publish "%PROJECT%" -c Release -r "%RUNTIME%" --self-contained true ^
  /p:PublishSingleFile=true ^
  /p:IncludeNativeLibrariesForSelfExtract=true ^
  /p:EnableCompressionInSingleFile=true ^
  /p:DebugType=None ^
  /p:DebugSymbols=false ^
  -o "%PUBLISH_DIR%"

if errorlevel 1 exit /b %errorlevel%

if exist "%PUBLISH_DIR%\AIUsageMonitor.exe" ren "%PUBLISH_DIR%\AIUsageMonitor.exe" "%APP_NAME%.exe"

if not exist "%PUBLISH_EXE%" (
  echo Expected release executable was not found: "%PUBLISH_EXE%"
  exit /b 1
)

del /q "%PUBLISH_DIR%\*.pdb" 2>nul
del /q "%PUBLISH_DIR%\*.xml" 2>nul
del /q "%PUBLISH_DIR%\*.log" 2>nul
del /q "%PUBLISH_DIR%\*.jsonl" 2>nul
del /q "%PUBLISH_DIR%\monitor.settings.json" 2>nul
del /q "%PUBLISH_DIR%\monitor.log.jsonl" 2>nul
del /q "%PUBLISH_DIR%\usage.fake.json" 2>nul

if not defined RT_PROJECTS (
  echo RT_PROJECTS is not set. Cannot sign "%PUBLISH_EXE%".
  exit /b 1
)

if not exist "%RT_PROJECTS%\Signing\sign.bat" (
  echo Signing script was not found: "%RT_PROJECTS%\Signing\sign.bat"
  exit /b 1
)

if not exist "%SIGNTOOL_EXE%" (
  echo SignTool was not found: "%SIGNTOOL_EXE%"
  exit /b 1
)

REM ~2s settle delay so the publish process releases the exe handle before signing.
REM Use ping, not "timeout": timeout aborts with "ERROR: Input redirection is not
REM supported" when stdin is redirected, which headless/agent runs do to skip the
REM pause in sign.bat. ping needs no console and emits no stderr noise.
ping -n 3 127.0.0.1 >nul 2>&1
call "%RT_PROJECTS%\Signing\sign.bat" "%PUBLISH_EXE%" "Seth's AI Usage Monitor" "rtsoft.com"

"%SIGNTOOL_EXE%" verify /pa /v "%PUBLISH_EXE%"

if errorlevel 1 exit /b %errorlevel%

copy /y "%PUBLISH_EXE%" "%BUILD_EXE%" >nul

if errorlevel 1 exit /b %errorlevel%

powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; Compress-Archive -LiteralPath $env:PUBLISH_EXE -DestinationPath $env:ZIP_PATH -Force"

if errorlevel 1 exit /b %errorlevel%

echo Created "%ZIP_PATH%"
echo Copied "%BUILD_EXE%"
