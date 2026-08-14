@echo off
rem ВНИМАНИЕ: файл сохранён в кодировке CP866 с переводами строк CRLF.
rem В UTF-8 пересохранять нельзя - cmd.exe разобьёт кириллицу и файл сломается.
chcp 866 >nul
rem EnableDelayedExpansion обязателен: переменная, заданная внутри блока if (...),
rem без него подставляется значением, каким была ДО входа в блок.
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0"

echo.
echo ==========================================
echo   Синхронизация с GitHub
echo ==========================================
echo.

where git >nul 2>nul
if errorlevel 1 (
    echo [СТОП] На этом компьютере не установлен git.
    echo Скачать: https://git-scm.com/download/win
    goto :fail
)

rem --- 1. Свои правки: показать и предложить сохранить ---
git diff --quiet && git diff --cached --quiet
if errorlevel 1 (
    echo Изменённые файлы:
    echo.
    git status --short
    echo.
    set /p ANSWER="Сохранить эти изменения и отправить? [Y/n]: "
    if /I "!ANSWER!"=="n" (
        echo Отменено. Ничего не отправлено.
        goto :fail
    )
    git add -A
    git commit -m "Правки с компьютера %COMPUTERNAME% от %DATE%"
    if errorlevel 1 goto :fail
    echo.
)

rem --- 2. Забрать чужие правки (rebase: без лишних merge-коммитов) ---
echo Забираю свежую версию...
git pull --rebase
if errorlevel 1 (
    echo.
    echo [ВНИМАНИЕ] Не удалось совместить правки автоматически.
    echo Тот же файл менялся на двух компьютерах. Сами это не разбирайте -
    echo откройте Claude Code в этой папке и скажите "конфликт при синхронизации".
    goto :fail
)

rem --- 3. Отправить своё ---
echo Отправляю...
git push
if errorlevel 1 (
    echo.
    echo [ОШИБКА] Не удалось отправить на GitHub.
    echo Если открылось окно входа - войдите в аккаунт и запустите снова.
    echo Если нет интернета - правки сохранены локально, отправите позже.
    goto :fail
)

echo.
echo ==========================================
echo   ГОТОВО - всё синхронизировано
echo ==========================================
echo.
echo Свежая версия кода на месте, ваши правки на GitHub.
echo Дальше: закрыть AutoCAD, запустить build.bat, открыть AutoCAD.
echo.
pause
exit /b 0

:fail
echo.
pause
exit /b 1
