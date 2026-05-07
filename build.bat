@echo off
echo ========================================
echo   GrokPY-CSharp Build Script
echo ========================================
echo.

echo [1/3] Restore packages...
dotnet restore GrokPY.sln
if %errorlevel% neq 0 (
    echo ERROR: Restore failed!
    pause
    exit /b 1
)

echo.
echo [2/3] Build Release...
dotnet build GrokPY.sln -c Release --no-restore
if %errorlevel% neq 0 (
    echo ERROR: Build failed!
    pause
    exit /b 1
)

echo.
echo [3/3] Publish single-file exe...
dotnet publish GrokPY.App/GrokPY.App.csproj ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:PublishReadyToRun=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -o ./publish ^
  --no-restore
if %errorlevel% neq 0 (
    echo ERROR: Publish failed!
    pause
    exit /b 1
)

echo.
echo ========================================
echo   BUILD THANH CONG!
echo   File: publish\GrokPY.App.exe
echo ========================================
pause
