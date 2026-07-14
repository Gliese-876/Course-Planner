@echo off
setlocal

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-CoursePlanner.ps1"
set "COURSE_PLANNER_INSTALL_EXIT=%ERRORLEVEL%"

echo.
if not "%COURSE_PLANNER_INSTALL_EXIT%"=="0" (
  echo Course Planner installation did not complete. Review the message above.
) else (
  echo You can close this window and open Course Planner from the Start menu.
)
pause
exit /b %COURSE_PLANNER_INSTALL_EXIT%
