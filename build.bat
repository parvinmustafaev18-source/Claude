@echo off
rem ВНИМАНИЕ: этот файл сохранён в кодировке CP866 (кодировка консоли Windows).
rem В UTF-8 его пересохранять нельзя - cmd.exe разобьёт кириллицу и файл сломается.
chcp 866 >nul
setlocal EnableExtensions
cd /d "%~dp0"

echo.
echo ==========================================
echo   Сборка MeshPlugin
echo ==========================================
echo.

rem --- 1. AutoCAD должен быть закрыт: иначе он держит старую DLL ---
tasklist /FI "IMAGENAME eq acad.exe" 2>nul | find /I "acad.exe" >nul
if not errorlevel 1 (
    echo [СТОП] AutoCAD сейчас открыт.
    echo.
    echo Пока AutoCAD работает, он держит старую версию плагина и
    echo заменить её нельзя. Закройте AutoCAD и запустите сборку снова,
    echo иначе вы будете тестировать ПРЕЖНЮЮ сборку.
    goto :fail
)

rem --- 2. Найти MSBuild: сначала через vswhere, потом по типовым путям ---
set "MSBUILD="
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if exist "%VSWHERE%" (
    for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe 2^>nul`) do set "MSBUILD=%%i"
)
if not defined MSBUILD (
    for %%P in (
        "%ProgramFiles%\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
        "%ProgramFiles%\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe"
        "%ProgramFiles%\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe"
        "%ProgramFiles%\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
        "%ProgramFiles(x86)%\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe"
        "%ProgramFiles(x86)%\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
    ) do if not defined MSBUILD if exist %%P set "MSBUILD=%%~P"
)
if not defined MSBUILD (
    echo [СТОП] Не найден MSBuild - программа, которая собирает проект.
    echo.
    echo Установите Visual Studio 2022 Community или Build Tools:
    echo https://visualstudio.microsoft.com/downloads/
    echo При установке отметьте "Разработка классических приложений .NET".
    goto :fail
)

rem --- 3. Собственно сборка ---
"%MSBUILD%" MeshPlugin.csproj /p:Configuration=Debug /p:Platform=x64 /nologo /v:minimal
if errorlevel 1 (
    echo.
    echo [ОШИБКА] Собрать не удалось.
    echo.
    echo Выше указаны файл и номер строки с ошибкой. Плагин НЕ обновлён -
    echo AutoCAD продолжит работать на прежней сборке.
    echo Если написано "AutoCAD not found" - на этом компьютере не установлен
    echo AutoCAD, без него собрать плагин нельзя.
    goto :fail
)

rem --- 4. Готово ---
echo.
echo ==========================================
echo   ГОТОВО - плагин собран
echo ==========================================
for %%F in ("bin\x64\Debug\MeshPlugin.dll") do echo   Собрано: %%~tF
echo   Установлен в: %APPDATA%\Autodesk\ApplicationPlugins\MeshPlugin.bundle
echo.
echo Запустите AutoCAD - плагин загрузится сам.
echo Проверка: команда MESHHELLO. Каждая команда MESH* печатает время
echo сборки - оно должно совпасть с указанным выше.
echo.
pause
exit /b 0

:fail
echo.
pause
exit /b 1
