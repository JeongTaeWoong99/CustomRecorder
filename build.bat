@echo off
chcp 65001 >nul
cd /d "%~dp0"
taskkill /im CustomRecorder.exe /f >nul 2>&1
echo [%date% %time%] build start > build.log
rem single-file CustomRecorder.exe in the project folder (self-contained: runs without installing .NET), same as the GitHub release
"C:\Program Files\dotnet\dotnet.exe" publish src\CustomRecorder.App\CustomRecorder.App.csproj -c Release -p:Platform=x64 -p:PublishSingleFile=true -p:SelfContained=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -o publish -nologo -clp:NoSummary >> build.log 2>&1
if errorlevel 1 goto done
copy /y publish\CustomRecorder.exe CustomRecorder.exe >nul
rem then the normal build (bin\, needs the installed .NET): runs after publish so bin\ is not left in self-contained mode
"C:\Program Files\dotnet\dotnet.exe" build CustomRecorder.sln -c Release -p:Platform=x64 -nologo -clp:NoSummary >> build.log 2>&1
if errorlevel 1 goto done
:done
echo EXITCODE=%errorlevel% >> build.log
