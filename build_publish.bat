@echo off
setlocal EnableDelayedExpansion
title StorySteamAI - Build & Publish

:: ====== EDIT THESE TWO LINES ======
set "APPID=480"
set "STEAM_SDK_DIR=C:\path\to\steamworks\sdk"
:: ==================================

set "DLL_SRC=%STEAM_SDK_DIR%\redistributable_bin\win64\steam_api64.dll"
set "PROJ=StorySteamAI.csproj"
set "CS=Program.cs"

if not exist "%PROJ%" (
  echo [ERROR] %PROJ% not found. Put this .bat next to the .csproj and Program.cs
  exit /b 1
)
if not exist "%CS%" (
  echo [ERROR] %CS% not found. Put this .bat next to Program.cs
  exit /b 1
)

where dotnet >nul 2>&1 || (echo [ERROR] .NET SDK not found. Install .NET 8 SDK and retry. & exit /b 1)

echo.
echo [*] Ensuring Steamworks.NET package is referenced...
findstr /i /c:"Steamworks.NET" "%PROJ%" >nul 2>&1
if errorlevel 1 (
  echo     -> Adding NuGet package Steamworks.NET
  dotnet add package Steamworks.NET || (echo [ERROR] Failed to add Steamworks.NET & exit /b 1)
) else (
  echo     -> Already referenced.
)

echo.
echo [*] Restoring NuGet packages...
dotnet restore || (echo [ERROR] restore failed & exit /b 1)

echo.
echo [*] Publishing single-file EXE (Release, win-x64)...
dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false || (echo [ERROR] publish failed & exit /b 1)

set "OUT=bin\Release\net8.0-windows\win-x64\publish"
if not exist "%OUT%" (
  echo [ERROR] Publish folder not found: %OUT%
  exit /b 1
)

echo.
echo [*] Copying steam_api64.dll ...
if not exist "%DLL_SRC%" (
  echo [ERROR] Could not find steam_api64.dll at:
  echo         %DLL_SRC%
  echo         -> Set STEAM_SDK_DIR correctly at top of this .bat
  exit /b 1
)
copy /Y "%DLL_SRC%" "%OUT%\steam_api64.dll" >nul

echo [*] Writing steam_appid.txt (dev only) ...
> "%OUT%\steam_appid.txt" echo %APPID%

echo.
echo [DONE] Build complete.
echo        EXE: %OUT%\StorySteamAI.exe
echo        (Dev only) steam_appid.txt created and steam_api64.dll copied.
echo        Launch EXE while Steam client is running.
echo.
pause
