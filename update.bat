@echo off
rem ВНИМАНИЕ: файл сохранён в кодировке CP866 с переводами строк CRLF.
rem В UTF-8 пересохранять нельзя - cmd.exe разобьёт кириллицу и файл сломается.
rem
rem Одна кнопка на весь цикл: забрать свежий код с GitHub (в том числе ветку,
rem в которую пишет Claude), собрать плагин и отправить собранную DLL обратно.
rem Где нет Visual Studio - ставит готовую сборку из dist\.
chcp 866 >nul
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0"

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

rem --- 0. Подпись коммитов: на новой машине её обычно нет, без неё git
rem      отказывается коммитить с невнятной ошибкой по-английски.
git config user.email >nul 2>nul
if errorlevel 1 (
    echo Подпись коммитов не задана - ставлю ту же, что на других компьютерах.
    git config --global user.name "parvinmustafaev18-source"
    git config --global user.email "parvinmustafaev18-source@users.noreply.github.com"
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

set "CUR="
for /f "tokens=*" %%B in ('git rev-parse --abbrev-ref HEAD') do set "CUR=%%B"

rem --- 3. Забрать с GitHub: сначала общую ветку, затем ветку Claude ---
echo Забираю свежую версию (ветка !CUR!)...
git fetch --prune origin
if errorlevel 1 (
    echo.
    echo [ОШИБКА] Не удалось связаться с GitHub. Проверьте интернет.
    goto :fail
)

git merge --no-edit origin/main
if errorlevel 1 goto :mergefail

rem Ветка Claude называется claude/... и меняется от задачи к задаче,
rem поэтому берём самую свежую по времени коммита, а не по имени.
set "AIBRANCH="
for /f "tokens=*" %%B in ('git for-each-ref --sort=-committerdate --format="%%(refname:short)" --count=1 "refs/remotes/origin/claude/*"') do set "AIBRANCH=%%B"
if defined AIBRANCH (
    echo Ветка Claude: !AIBRANCH!
    git merge --no-edit "!AIBRANCH!"
    if errorlevel 1 goto :mergefail
)
echo.

rem --- 4. Собрать; без Visual Studio (код 2) поставить готовую сборку ---
call build.bat /nopause
if errorlevel 2 (
    echo.
    echo Visual Studio здесь нет - ставлю готовую сборку из dist\.
    echo.
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
git push -u origin "!CUR!"
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
echo   ГОТОВО
echo ==========================================
echo.
echo Запустите AutoCAD - плагин загрузится сам.
echo Проверка: команда MESHHELLO, она печатает время сборки.
echo.
pause
exit /b 0

:mergefail
echo.
echo [ВНИМАНИЕ] Не удалось совместить правки автоматически.
echo Один и тот же файл менялся в двух местах. Сами это не разбирайте -
echo откройте Claude Code в этой папке и скажите "конфликт при слиянии".
goto :fail

:fail
echo.
pause
exit /b 1
