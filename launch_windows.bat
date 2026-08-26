@echo off
rem Windows launcher for the APK patcher GUI.
rem
rem Installs the `legible` interpreter (and the build tools it needs: Rust,
rem a C++ linker, git) if it is not already on PATH, makes Git's POSIX shell
rem available to Legible's process builtins, then runs
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

set "LEGIBLE_REPO=https://github.com/Gummygamer/legible-lang.git"
set "LEGIBLE_BRANCH=development"
set "LEGIBLE_ROOT=%LOCALAPPDATA%\legible-lang"
set "LEGIBLE_SRC=%LEGIBLE_ROOT%\src"

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

call :enable_git_shell
if not !errorlevel!==0 exit /b 1

rem server.lbl normally checks random ports with the Linux-only `ss` command.
rem Select an available port with PowerShell so Windows skips that probe.
set "GUI_PORT="
for /f "delims=" %%P in ('powershell -NoProfile -Command "$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0); $listener.Start(); $port = $listener.LocalEndpoint.Port; $listener.Stop(); $port"') do set "GUI_PORT=%%P"
if not defined GUI_PORT (
  echo Could not choose an available port for the APK patcher GUI.
  exit /b 1
)

echo ==^> Launching APK patcher GUI...
echo     Note: this app auto-opens the browser via a Linux-only command, so on
echo     Windows it will not open automatically -- copy the printed URL below
echo     into your browser once the server is listening.
legible run tools\apk_patcher_gui\server.lbl --port !GUI_PORT! --no-browser %*
exit /b %errorlevel%

:enable_git_shell
where sh >nul 2>nul
if %errorlevel%==0 exit /b 0

rem Git for Windows keeps sh.exe beside its Unix tools rather than in the
rem `cmd` directory that its installer normally adds to PATH. Locate that
rem sibling directory from git.exe first, then try the standard install roots.
set "GIT_EXE="
for /f "delims=" %%G in ('where git 2^>nul') do if not defined GIT_EXE set "GIT_EXE=%%G"
if defined GIT_EXE (
  for %%G in ("!GIT_EXE!") do set "GIT_CMD_DIR=%%~dpG"
  for %%S in ("!GIT_CMD_DIR!..\bin\sh.exe") do if exist "%%~fS" set "PATH=%%~dpS;!PATH!"
)

if exist "%ProgramFiles%\Git\bin\sh.exe" set "PATH=%ProgramFiles%\Git\bin;!PATH!"
if defined ProgramFiles(x86) if exist "%ProgramFiles(x86)%\Git\bin\sh.exe" set "PATH=%ProgramFiles(x86)%\Git\bin;!PATH!"
if exist "%LOCALAPPDATA%\Programs\Git\bin\sh.exe" set "PATH=%LOCALAPPDATA%\Programs\Git\bin;!PATH!"

where sh >nul 2>nul
if %errorlevel%==0 exit /b 0

echo Could not find the POSIX shell included with Git for Windows.
echo Repair or reinstall Git from https://git-scm.com/download/win and re-run this script.
exit /b 1

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

if not exist "%LEGIBLE_ROOT%" mkdir "%LEGIBLE_ROOT%"
if not !errorlevel!==0 (
  echo Could not create the Legible source cache at "%LEGIBLE_ROOT%".
  exit /b 1
)

rem A cancelled or failed clone can leave a .git entry behind without a usable
rem worktree. Validate both Git metadata and Cargo.toml before attempting an
rem update; otherwise preserve the broken cache and clone a clean copy.
set "LEGIBLE_SRC_VALID="
if exist "%LEGIBLE_SRC%\Cargo.toml" (
  git -C "%LEGIBLE_SRC%" rev-parse --is-inside-work-tree >nul 2>nul
  if !errorlevel!==0 set "LEGIBLE_SRC_VALID=1"
)

if defined LEGIBLE_SRC_VALID (
  echo ==^> Updating Legible interpreter source...
  git -C "%LEGIBLE_SRC%" remote set-url origin "%LEGIBLE_REPO%"
  if not !errorlevel!==0 (
    echo Could not configure the Legible source repository.
    exit /b 1
  )
  git -C "%LEGIBLE_SRC%" pull --ff-only origin "%LEGIBLE_BRANCH%"
  if not !errorlevel!==0 (
    echo Could not update the Legible source checkout. Resolve any local changes
    echo in "%LEGIBLE_SRC%" or remove that directory, then re-run this script.
    exit /b 1
  )
) else (
  if exist "%LEGIBLE_SRC%" (
    set "LEGIBLE_BACKUP=%LEGIBLE_ROOT%\src.invalid-!RANDOM!-!RANDOM!"
    echo ==^> Found an incomplete Legible source cache; preserving it as:
    echo     !LEGIBLE_BACKUP!
    move "%LEGIBLE_SRC%" "!LEGIBLE_BACKUP!" >nul
    if not !errorlevel!==0 (
      echo Could not move the incomplete cache out of the way.
      exit /b 1
    )
  )
  echo ==^> Cloning Legible interpreter source...
  git clone --depth 1 --branch "%LEGIBLE_BRANCH%" "%LEGIBLE_REPO%" "%LEGIBLE_SRC%"
  if not !errorlevel!==0 exit /b 1
)

if not exist "%LEGIBLE_SRC%\Cargo.toml" (
  echo The Legible source checkout is missing Cargo.toml; installation cannot continue.
  exit /b 1
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
