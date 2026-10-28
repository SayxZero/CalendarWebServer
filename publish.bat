@echo off
rem ---------------------------------------------------------------------------
rem  Lays the site server out for a machine without the source tree:
rem  publish\CalendarWebServer.exe (framework-dependent, needs the ASP.NET Core
rem  10 runtime), appsettings.json, and the site itself in publish\site, which
rem  the server finds beside itself.
rem
rem      publish.bat                    into publish\
rem      publish.bat -out D:\web        into another folder
rem      publish.bat -noui              do not wait for a key at the end
rem ---------------------------------------------------------------------------
setlocal
set "HERE=%~dp0"
set "OUT=%HERE%publish"
set "NOUI="
:parse
if "%~1"=="" goto :ready
if /i "%~1"=="-noui" set "NOUI=1"
if /i "%~1"=="-out" (set "OUT=%~2" & shift)
shift
goto :parse
:ready
set "SITE=%HERE%..\CalendarWeb\site"
if not exist "%SITE%\index.html" (
    echo [ERROR] The site is not found in %SITE% - clone CalendarWeb beside this folder.
    goto :fail
)
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_NOLOGO=1"
dotnet publish "%HERE%src\CalendarWebServer\CalendarWebServer.csproj" -c Release -o "%OUT%" -nologo -v quiet || goto :fail
if exist "%OUT%\site" rmdir /s /q "%OUT%\site"
xcopy "%SITE%" "%OUT%\site\" /E /I /Q /Y >nul || goto :fail
echo Published to %OUT%
set "CODE=0"
goto :done
:fail
set "CODE=1"
:done
if not defined NOUI pause
exit /b %CODE%
