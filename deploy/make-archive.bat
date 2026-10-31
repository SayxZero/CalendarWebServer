@echo off
rem ---------------------------------------------------------------------------
rem  The archive of the presentation site for a Linux server (branch site-only):
rem  the program without its Windows launcher, the pages of the presentation and
rem  deploy\, in cosmos-site.tar.gz of this folder. deploy\SITE-SETUP.md says the
rem  rest. The planner in the browser stays out of it.
rem
rem      make-archive.bat            make it, then wait for a key
rem      make-archive.bat -noui      do not wait
rem ---------------------------------------------------------------------------
setlocal
set "ROOT=%~dp0.."
set "SITE=%ROOT%\..\CalendarWeb\site"
set "WORK=%ROOT%\publish-site"
set "ARCHIVE=%ROOT%\cosmos-site.tar.gz"
set "NOUI="
if /i "%~1"=="-noui" set "NOUI=1"

if not exist "%SITE%\index.html" (
    echo [ERROR] The site is not found in %SITE% - clone CalendarWeb beside this folder.
    goto :fail
)
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_NOLOGO=1"
if exist "%WORK%" rmdir /s /q "%WORK%"
del /q "%ARCHIVE%" 2>nul
dotnet publish "%ROOT%\src\CalendarWebServer\CalendarWebServer.csproj" -c Release -o "%WORK%" -nologo -v quiet -p:UseAppHost=false || goto :fail

mkdir "%WORK%\site\css" "%WORK%\site\js" || goto :fail
copy /y "%SITE%\index.html" "%WORK%\site\" >nul || goto :fail
copy /y "%SITE%\css\landing.css" "%WORK%\site\css\" >nul || goto :fail
copy /y "%SITE%\js\landing.js" "%WORK%\site\js\" >nul || goto :fail
copy /y "%SITE%\js\landingPrices.js" "%WORK%\site\js\" >nul || goto :fail
xcopy "%SITE%\img" "%WORK%\site\img\" /E /I /Q /Y >nul || goto :fail
xcopy "%SITE%\video" "%WORK%\site\video\" /E /I /Q /Y >nul || goto :fail
xcopy "%SITE%\docs" "%WORK%\site\docs\" /E /I /Q /Y >nul || goto :fail
xcopy "%ROOT%\deploy" "%WORK%\deploy\" /E /I /Q /Y >nul || goto :fail
del /q "%WORK%\deploy\make-archive.bat" 2>nul

rem Run from inside: the tar of Windows reads a colon in a path as a remote host.
pushd "%WORK%" || goto :fail
tar -czf ..\cosmos-site.tar.gz * || (popd & goto :fail)
popd
echo Made %ARCHIVE%
set "CODE=0"
goto :done
:fail
echo [ERROR] The archive is not made.
set "CODE=1"
:done
if not defined NOUI pause
exit /b %CODE%
