@echo off
rem ---------------------------------------------------------------------------
rem  Builds the site server and runs it in this console: the window is the log,
rem  Ctrl+C stops it.
rem
rem      run-web.bat                                  port 8780, sync server 127.0.0.1:8765
rem      run-web.bat -p 8781 -u 127.0.0.1:8766        a test server beside the real one
rem      run-web.bat --web-root D:\site               the site from another folder
rem      run-web.bat -noui                            do not wait for a key after exit
rem
rem  Everything except -noui goes to CalendarWebServer as it is; --help lists it.
rem  The site itself is found in ..\CalendarWeb\site of the umbrella checkout.
rem ---------------------------------------------------------------------------
setlocal
rem Kept before the loop: shift moves %0 along with the arguments.
set "HERE=%~dp0"
set "NOUI="
set "ARGS="
:parse
if "%~1"=="" goto :ready
if /i "%~1"=="-noui" (set "NOUI=1") else (set ARGS=%ARGS% "%~1")
shift
goto :parse
:ready
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_NOLOGO=1"
dotnet build "%HERE%src\CalendarWebServer\CalendarWebServer.csproj" -c Release -v quiet -nologo || goto :fail
"%HERE%src\CalendarWebServer\bin\Release\net10.0\CalendarWebServer.exe" %ARGS%
set "CODE=%ERRORLEVEL%"
goto :done
:fail
set "CODE=1"
echo.
echo [ERROR] The build failed.
:done
if not defined NOUI pause
exit /b %CODE%
