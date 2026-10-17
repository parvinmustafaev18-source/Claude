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
rem Ключ /nopause: не ждать нажатия клавиши - так файл зовёт update.bat.
if /I "%~1"=="/nopause" set "NOPAUSE=1"

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

rem --- Свежесть готовой сборки ---
rem Дата ФАЙЛА dist\MeshPlugin.dll ничего не говорит: git ставит её по времени
rem скачивания, поэтому только что подтянутая октябрьская сборка выглядит свежей.
rem Настоящий признак - дата КОММИТА, которым DLL попала в репозиторий. Если код
rem (*.cs) коммитили позже, значит собранного кода в dist нет, и новых команд в
rem плагине не будет. Молча ставить такую сборку нельзя - это выглядит как
rem "обновился, а команды нет".
set "TDLL="
set "TSRC="
set "DLLDATE="
where git >nul 2>nul
if errorlevel 1 goto :skipstale
for /f "tokens=*" %%T in ('git log -1 --format=%%ct -- "dist/MeshPlugin.dll" 2^>nul') do set "TDLL=%%T"
for /f "tokens=*" %%T in ('git log -1 --format=%%ct -- "*.cs" 2^>nul') do set "TSRC=%%T"
for /f "tokens=*" %%T in ('git log -1 --format=%%cd --date=short -- "dist/MeshPlugin.dll" 2^>nul') do set "DLLDATE=%%T"
if not defined TDLL goto :skipstale
if not defined TSRC goto :skipstale
if %TSRC% GTR %TDLL% (
    echo [ВНИМАНИЕ] Готовая сборка в dist СТАРШЕ кода.
    echo.
    echo   DLL в dist собрана из кода по состоянию на %DLLDATE%,
    echo   а исходники в репозитории правились позже.
    echo   Значит новых команд в этой сборке НЕТ, сколько ни обновляйся.
    echo.
    echo   Чтобы получить актуальную версию: соберите плагин на компьютере
    echo   с Visual Studio (build.bat или update.bat) - он положит свежую DLL
    echo   в dist\ и отправит её на GitHub. После этого здесь хватит update.bat.
    echo.
    if not defined NOPAUSE pause
)
:skipstale

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
for %%F in ("dist\MeshPlugin.dll") do echo   Файл DLL: %%~tF (время скачивания, не сборки)
if defined DLLDATE echo   Код собранной версии: по состоянию на %DLLDATE%
echo   Папка: %BUNDLE%
echo.
echo Запустите AutoCAD - плагин загрузится сам.
echo Проверка: команда LIRVERSION.
echo.
echo Если AutoCAD пишет, что не может загрузить сборку, введите в нём
echo SECURELOAD со значением 0 - это настройка пользователя, права
echo администратора для неё не нужны.
echo.
if not defined NOPAUSE pause
exit /b 0

:copyfail
echo.
echo [ОШИБКА] Не удалось скопировать файлы в папку профиля:
echo %BUNDLE%
echo Возможно, папку блокирует антивирус или политика организации.
goto :fail

:fail
echo.
if not defined NOPAUSE pause
exit /b 1
