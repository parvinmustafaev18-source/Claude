@echo off
rem ВНИМАНИЕ: файл сохранён в кодировке CP866 с переводами строк CRLF.
rem В UTF-8 пересохранять нельзя - cmd.exe разобьёт кириллицу и файл сломается.
rem
rem Обновлять только stable/without-lirthick, собрать плагин и отправить DLL
rem обратно в эту же ветку. main и ветки Claude не подмешиваются.
rem Где нет Visual Studio - ставит готовую сборку из dist\.
chcp 866 >nul
setlocal EnableExtensions EnableDelayedExpansion
rem Выполнять временную копию: git merge может заменить сам update.bat.
if /I "%~1"=="/run" goto :run
set "RUNNER=%TEMP%\MeshPlugin-update-%RANDOM%-%RANDOM%.bat"
copy /Y "%~f0" "!RUNNER!" >nul
if errorlevel 1 goto :fail
"!RUNNER!" /run "%~dp0"
exit /b 1

:run
cd /d "%~2"
if errorlevel 1 goto :fail
set "TARGET=stable/without-lirthick"

echo.
echo ==========================================
echo   Обновление MeshPlugin
echo ==========================================
echo.

where git >nul 2>nul
if errorlevel 1 (
    echo [СТОП] На этом компьютере не установлен git.
    echo Скачать: https://git-scm.com/download/win
    goto :fail
)

rem Не запускать обновление или отправку сборки из main и архивных веток.
set "CUR="
for /f "tokens=*" %%B in ('git rev-parse --abbrev-ref HEAD') do set "CUR=%%B"
if not "!CUR!"=="!TARGET!" (
    echo [СТОП] Требуется ветка !TARGET!, сейчас !CUR!.
    echo Выполните в папке проекта:
    echo   git fetch origin
    echo   git switch --track -c !TARGET! origin/!TARGET!
    echo Если локальная ветка уже существует: git switch !TARGET!
    goto :fail
)

rem --- 0. Подпись коммитов: на новой машине её обычно нет, без неё git
rem      отказывается коммитить с невнятной ошибкой по-английски.
git config user.email >nul 2>nul
if errorlevel 1 (
    echo Подпись коммитов не задана - ставлю ту же, что на других компьютерах.
    git config user.name "parvinmustafaev18-source"
    git config user.email "parvinmustafaev18-source@users.noreply.github.com"
    echo.
)

rem --- 1. AutoCAD должен быть закрыт: иначе он держит старую DLL ---
tasklist /FI "IMAGENAME eq acad.exe" 2>nul | find /I "acad.exe" >nul
if not errorlevel 1 (
    echo [СТОП] AutoCAD сейчас открыт.
    echo.
    echo Пока AutoCAD работает, он держит прежнюю версию плагина и
    echo заменить её нельзя. Закройте AutoCAD и запустите снова.
    goto :fail
)

rem --- 2. Свои правки сохранить, иначе они помешают слиянию ---
git diff --quiet && git diff --cached --quiet
if errorlevel 1 (
    echo Свои изменения:
    echo.
    git status --short
    echo.
    git add -A
    git commit -m "Правки с компьютера %COMPUTERNAME% от %DATE%"
    if errorlevel 1 goto :fail
    echo.
)

rem --- 3. Забрать только стабильную версию без LIRTHICK ---
echo Забираю свежую версию без LIRTHICK: !TARGET!...
git fetch origin "refs/heads/!TARGET!:refs/remotes/origin/!TARGET!"
if errorlevel 1 (
    echo.
    echo [ОШИБКА] Не удалось связаться с GitHub. Проверьте интернет.
    goto :fail
)

rem Проверить входящую версию до слияния и установки.
git grep -q -F LIRTHICK "origin/!TARGET!" -- "*.cs" "MeshPlugin.csproj"
if errorlevel 2 goto :fail
if not errorlevel 1 goto :wrongversion

git merge --ff-only "origin/!TARGET!"
if errorlevel 1 goto :mergefail

rem Проверить и локальный код: свои коммиты могли вернуть команду.
git grep -q -F LIRTHICK -- "*.cs" "MeshPlugin.csproj"
if errorlevel 2 goto :fail
if not errorlevel 1 goto :wrongversion
echo.

rem --- 4. Собрать; без Visual Studio (код 2) поставить готовую сборку ---
call build.bat /nopause
if errorlevel 2 (
    echo.
    echo Visual Studio здесь нет - проверяю готовую сборку из dist\.
    echo.
    if exist "dist\needs-build.txt" (
        echo [СТОП] Новая команда LIRSPLIT ещё не собрана в DLL.
        echo Запустите update.bat на компьютере с AutoCAD и Visual Studio / Build Tools.
        echo После сборки новая DLL появится в этой ветке; повторите обновление здесь.
        goto :fail
    )
    call install.bat /nopause
    if errorlevel 1 goto :fail
    goto :done
)
if errorlevel 1 goto :fail

rem --- 5. Отправить собранную DLL, чтобы её увидели другие компьютеры ---
echo Отправляю на GitHub...
git add -A
git diff --cached --quiet
if errorlevel 1 (
    git commit -m "Сборка с компьютера %COMPUTERNAME% от %DATE%"
    if errorlevel 1 goto :fail
)
git push -u origin "HEAD:refs/heads/!TARGET!"
if errorlevel 1 (
    echo.
    echo [ОШИБКА] Не удалось отправить на GitHub.
    echo Если открылось окно входа - войдите и запустите снова.
    echo Плагин при этом уже собран и установлен, тестировать можно.
    goto :fail
)

:done
echo.
echo ==========================================
echo   ГОТОВО - версия без LIRTHICK
echo ==========================================
echo.
echo Запустите AutoCAD - плагин загрузится сам.
echo Проверка: команда LIRVERSION, она печатает время сборки.
echo.
pause
exit /b 0

:mergefail
echo.
echo [СТОП] Локальная и серверная версии разошлись.
echo Автоматическое слияние не выполняется, ваши коммиты сохранены.
echo Откройте помощника в этой папке для разбора расхождения.
goto :fail

:wrongversion
echo.
echo [СТОП] В исходниках обнаружен LIRTHICK. Установка не выполняется.
goto :fail

:fail
echo.
pause
exit /b 1
