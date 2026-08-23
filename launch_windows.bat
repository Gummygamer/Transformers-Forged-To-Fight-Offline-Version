@echo off
rem Windows launcher for the APK patcher GUI.
rem
rem Installs the `legible` interpreter (and the build tools it needs: Rust,
rem a C++ linker, git) if it is not already on PATH, then runs
rem `legible run tools\apk_patcher_gui\server.lbl` from the repository root.
rem Re-running this script after the first successful run is fast: it finds
rem `legible` already installed and skips straight to launching the GUI.
rem
rem Automated installs use winget (built into modern Windows 10/11). If
rem winget is not available, this script prints manual install links instead
rem of guessing at a workaround.
setlocal enabledelayedexpansion

set "SCRIPT_DIR=%~dp0"
cd /d "%SCRIPT_DIR%"

set "LEGIBLE_REPO=https://github.com/darabat/legible"
set "LEGIBLE_SRC=%LOCALAPPDATA%\legible-lang\src"

where legible >nul 2>nul
if not %errorlevel%==0 (
  call :install_legible
  if not !errorlevel!==0 exit /b 1
)

where legible >nul 2>nul
if not %errorlevel%==0 (
  echo legible was installed but is not on PATH in this shell.
  echo Open a new terminal ^(so %USERPROFILE%\.cargo\bin is picked up^) and re-run this script.
  exit /b 1
)

echo ==^> Launching APK patcher GUI...
echo     Note: this app auto-opens the browser via a Linux-only command, so on
echo     Windows it will not open automatically -- copy the printed URL below
echo     into your browser once the server is listening.
legible run tools\apk_patcher_gui\server.lbl --no-browser %*
exit /b %errorlevel%

:install_legible
where git >nul 2>nul
if not %errorlevel%==0 (
  echo ==^> Installing git via winget...
  where winget >nul 2>nul
  if not !errorlevel!==0 goto :no_winget
  winget install --id Git.Git -e --silent --accept-package-agreements --accept-source-agreements
  where git >nul 2>nul
  if not !errorlevel!==0 (
    echo Could not install git automatically. Install it from https://git-scm.com/download/win
    echo and re-run this script.
    exit /b 1
  )
)

where cl >nul 2>nul
if not %errorlevel%==0 (
  echo ==^> Installing Visual C++ Build Tools ^(needed to compile native dependencies^)...
  echo     This step can take several minutes.
  where winget >nul 2>nul
  if not !errorlevel!==0 goto :no_winget
  winget install --id Microsoft.VisualStudio.2022.BuildTools -e --silent --accept-package-agreements --accept-source-agreements --override "--quiet --wait --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended"
  rem cl.exe is only on PATH from a "Developer Command Prompt"; a fresh
  rem terminal will find it via the workload's registered environment after
  rem the next `cargo install` invocation, since rustup/cargo probe the
  rem standard install location directly.
)

where cargo >nul 2>nul
if not %errorlevel%==0 (
  echo ==^> Installing Rust via rustup...
  set "RUSTUP_INIT=%TEMP%\rustup-init.exe"
  powershell -NoProfile -Command "Invoke-WebRequest -UseBasicParsing -Uri https://win.rustup.rs/x86_64 -OutFile '!RUSTUP_INIT!'"
  if not exist "!RUSTUP_INIT!" (
    echo Failed to download rustup-init.exe. Install Rust manually from https://rustup.rs
    echo and re-run this script.
    exit /b 1
  )
  "!RUSTUP_INIT!" -y --default-host x86_64-pc-windows-msvc --default-toolchain stable
  del "!RUSTUP_INIT!" >nul 2>nul
  set "PATH=%USERPROFILE%\.cargo\bin;%PATH%"
)

where cargo >nul 2>nul
if not %errorlevel%==0 (
  echo Rust installation did not complete correctly. Open a new terminal, run
  echo "cargo --version" to confirm it works, then re-run this script.
  exit /b 1
)

if exist "%LEGIBLE_SRC%\.git" (
  echo ==^> Updating Legible interpreter source...
  git -C "%LEGIBLE_SRC%" pull --ff-only
) else (
  echo ==^> Cloning Legible interpreter source...
  if not exist "%LOCALAPPDATA%\legible-lang" mkdir "%LOCALAPPDATA%\legible-lang"
  git clone --depth 1 "%LEGIBLE_REPO%" "%LEGIBLE_SRC%"
  if not !errorlevel!==0 exit /b 1
)

rem --no-default-features skips the optional SDL2 build (window/graphics
rem builtins), which nothing in this project uses, so no system SDL2 dev
rem libraries are required on Windows. HTTP, JSON, file, and SQLite builtins
rem (used by the APK patcher GUI) are not behind a feature flag and always
rem get built.
echo ==^> Building and installing the legible interpreter ^(first build can take several minutes^)...
pushd "%LEGIBLE_SRC%"
cargo install --path . --no-default-features --locked
set "BUILD_RESULT=%errorlevel%"
popd
if not "%BUILD_RESULT%"=="0" exit /b 1

set "PATH=%USERPROFILE%\.cargo\bin;%PATH%"
exit /b 0

:no_winget
echo Could not find winget to install dependencies automatically.
echo Install these manually, then re-run this script:
echo   - git:                   https://git-scm.com/download/win
echo   - Visual Studio Build Tools ^("Desktop development with C++"^):
echo                            https://visualstudio.microsoft.com/visual-cpp-build-tools/
echo   - Rust:                  https://rustup.rs
exit /b 1
