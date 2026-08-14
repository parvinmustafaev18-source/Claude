@echo off
rem ВНИМАНИЕ: файл сохранён в кодировке CP866 с переводами строк CRLF.
rem В UTF-8 пересохранять нельзя - cmd.exe разобьёт кириллицу и файл сломается.
rem
rem Установка ГОТОВОЙ сборки плагина. Ничего не компилирует, поэтому не нужны
rem ни Visual Studio, ни права администратора: всё пишется в профиль пользователя.
rem Для компьютера, где плагин только тестируют.
chcp 866 >nul
setlocal EnableExtensions
cd /d "%~dp0"

set "BUNDLE=%APPDATA%\Autodesk\ApplicationPlugins\MeshPlugin.bundle"

echo.
echo ==========================================
echo   Установка MeshPlugin
echo ==========================================
echo.

if not exist "dist\MeshPlugin.dll" (
    echo [СТОП] Не найден файл dist\MeshPlugin.dll
    echo.
    echo Готовой сборки нет. Запустите sync.bat, чтобы забрать свежую
    echo версию с GitHub. Если и после этого файла нет - значит сборку
    echo ещё не выложили с домашнего компьютера.
    goto :fail
)

tasklist /FI "IMAGENAME eq acad.exe" 2>nul | find /I "acad.exe" >nul
if not errorlevel 1 (
    echo [СТОП] AutoCAD сейчас открыт.
    echo.
    echo Пока AutoCAD работает, он держит прежнюю версию плагина и
    echo заменить её нельзя. Закройте AutoCAD и запустите установку снова.
    goto :fail
)

if not exist "%BUNDLE%\Contents\" mkdir "%BUNDLE%\Contents"
copy /Y "bundle\PackageContents.xml" "%BUNDLE%\" >nul
if errorlevel 1 goto :copyfail
copy /Y "dist\MeshPlugin.dll" "%BUNDLE%\Contents\" >nul
if errorlevel 1 goto :copyfail

echo ==========================================
echo   ГОТОВО - плагин установлен
echo ==========================================
for %%F in ("dist\MeshPlugin.dll") do echo   Версия сборки: %%~tF
echo   Папка: %BUNDLE%
echo.
echo Запустите AutoCAD - плагин загрузится сам.
echo Проверка: команда MESHHELLO.
echo.
echo Если AutoCAD пишет, что не может загрузить сборку, введите в нём
echo SECURELOAD со значением 0 - это настройка пользователя, права
echo администратора для неё не нужны.
echo.
pause
exit /b 0

:copyfail
echo.
echo [ОШИБКА] Не удалось скопировать файлы в папку профиля:
echo %BUNDLE%
echo Возможно, папку блокирует антивирус или политика организации.
goto :fail

:fail
echo.
pause
exit /b 1
