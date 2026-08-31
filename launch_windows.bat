@echo off
rem Windows launcher for the APK patcher GUI.
rem
rem Installs the `legible` interpreter (and the build tools it needs: Rust,
rem a C++ linker, git), Java, and the Android SDK/NDK if they are not already
rem available, makes Git's POSIX shell available to Legible's process builtins,
rem then runs
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
if %errorlevel%==0 (
  call :update_legible
) else (
  call :install_legible
  if not !errorlevel!==0 exit /b 1
)

where legible >nul 2>nul
if not %errorlevel%==0 (
  echo legible was installed but is not on PATH in this shell.
  echo Open a new terminal ^(so %USERPROFILE%\.cargo\bin is picked up^) and re-run this script.
  exit /b 1
)

call :enable_java_path
where java >nul 2>nul
if not %errorlevel%==0 (
  call :install_java
  if not !errorlevel!==0 exit /b 1
)

call :enable_android_path
call :ensure_android_sdk
if not !errorlevel!==0 exit /b 1

call :ensure_debug_keystore
if not !errorlevel!==0 exit /b 1

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
echo     The GUI will open in your default browser; use the printed URL if it does not.
set "APK_PATCHER_GUI_OPENER=powershell -NoProfile -Command Start-Process"
legible run tools\apk_patcher_gui\server.lbl --port !GUI_PORT! %*
exit /b %errorlevel%

:enable_java_path
where java >nul 2>nul
if %errorlevel%==0 exit /b 0

if defined JAVA_HOME if exist "%JAVA_HOME%\bin\java.exe" set "PATH=%JAVA_HOME%\bin;!PATH!"
exit /b 0

:enable_android_path
if not defined ANDROID_HOME if defined ANDROID_SDK_ROOT set "ANDROID_HOME=!ANDROID_SDK_ROOT!"
if not defined ANDROID_SDK_ROOT if defined ANDROID_HOME set "ANDROID_SDK_ROOT=!ANDROID_HOME!"
if not defined ANDROID_HOME exit /b 0

if exist "!ANDROID_HOME!\platform-tools\adb.exe" set "PATH=!ANDROID_HOME!\platform-tools;!PATH!"

set "ANDROID_BUILD_TOOLS_DIR="
for /f "delims=" %%B in ('dir /b /ad /o-n "!ANDROID_HOME!\build-tools" 2^>nul') do if not defined ANDROID_BUILD_TOOLS_DIR set "ANDROID_BUILD_TOOLS_DIR=!ANDROID_HOME!\build-tools\%%B"
if defined ANDROID_BUILD_TOOLS_DIR if exist "!ANDROID_BUILD_TOOLS_DIR!\zipalign.exe" set "PATH=!ANDROID_BUILD_TOOLS_DIR!;!PATH!"

set "ANDROID_NDK_DIR="
for /f "delims=" %%N in ('dir /b /ad /o-n "!ANDROID_HOME!\ndk" 2^>nul') do if not defined ANDROID_NDK_DIR set "ANDROID_NDK_DIR=!ANDROID_HOME!\ndk\%%N"
if defined ANDROID_NDK_DIR if exist "!ANDROID_NDK_DIR!\toolchains\llvm\prebuilt\windows-x86_64\bin\aarch64-linux-android28-clang.cmd" set "PATH=!ANDROID_NDK_DIR!\toolchains\llvm\prebuilt\windows-x86_64\bin;!PATH!"
exit /b 0

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

:install_java
where winget >nul 2>nul
if not %errorlevel%==0 goto :no_winget

echo ==^> Installing JDK 21 via winget...
winget install --id Microsoft.OpenJDK.21 -e --silent --accept-package-agreements --accept-source-agreements --override "/quiet /norestart ADDLOCAL=FeatureMain,FeatureEnvironment,FeatureJarFileRunWith,FeatureJavaHome"

rem Do not rely on winget's installer override having registered JAVA_HOME.
set "JAVA_HOME="
for /f "delims=" %%J in ('dir /b /ad /o-n "%ProgramFiles%\Microsoft\jdk-21*-hotspot" 2^>nul') do if not defined JAVA_HOME set "JAVA_HOME=%ProgramFiles%\Microsoft\%%J"
if not defined JAVA_HOME (
  echo Could not locate the JDK 21 installation under "%ProgramFiles%\Microsoft".
  exit /b 1
)

set "PATH=!JAVA_HOME!\bin;!PATH!"
setx JAVA_HOME "!JAVA_HOME!" >nul
where java >nul 2>nul
if not !errorlevel!==0 (
  echo JDK 21 was installed but java is not on PATH in this shell.
  exit /b 1
)
exit /b 0

:ensure_android_sdk
rem Resolve an SDK root: an existing env var, or the standard per-user location
rem that `pipeline.lbl` also probes (%LOCALAPPDATA%\Android\Sdk).
if not defined ANDROID_HOME if defined ANDROID_SDK_ROOT set "ANDROID_HOME=!ANDROID_SDK_ROOT!"
if not defined ANDROID_HOME set "ANDROID_HOME=%LOCALAPPDATA%\Android\Sdk"
set "ANDROID_SDK_ROOT=!ANDROID_HOME!"
call :enable_android_path

rem Work out which packages are still missing. A previous interrupted run can
rem leave only cmdline-tools behind, and the old launcher never installed the
rem NDK once zipalign existed, so check each package independently.
set "NEED_BUILD_TOOLS=1"
set "NEED_PLATFORM_TOOLS=1"
set "NEED_NDK=1"
where zipalign >nul 2>nul && set "NEED_BUILD_TOOLS="
where adb >nul 2>nul && set "NEED_PLATFORM_TOOLS="
call :ndk_clang_present && set "NEED_NDK="

if not defined NEED_BUILD_TOOLS if not defined NEED_PLATFORM_TOOLS if not defined NEED_NDK (
  setx ANDROID_HOME "!ANDROID_HOME!" >nul
  setx ANDROID_SDK_ROOT "!ANDROID_HOME!" >nul
  exit /b 0
)

call :ensure_cmdline_tools
if not !errorlevel!==0 exit /b 1

set "SDK_PACKAGES="
if defined NEED_PLATFORM_TOOLS set "SDK_PACKAGES=!SDK_PACKAGES! "platform-tools""
if defined NEED_BUILD_TOOLS set "SDK_PACKAGES=!SDK_PACKAGES! "build-tools;35.0.0""
if defined NEED_NDK set "SDK_PACKAGES=!SDK_PACKAGES! "ndk;26.1.10909125""

echo ==^> Installing Android SDK packages:!SDK_PACKAGES!
echo     This first-time download can take several minutes and use 1-2 GB.

rem sdkmanager reads license acceptances from stdin. Piping `echo` into a
rem `call`ed .bat is unreliable in cmd, so feed acceptances from a file.
set "SDK_YES=%TEMP%\apk-patcher-sdk-yes-!RANDOM!-!RANDOM!.txt"
powershell -NoProfile -Command "Set-Content -LiteralPath $env:SDK_YES -Value (1..64 | ForEach-Object { 'y' })"
call "!SDKMANAGER!" "--sdk_root=!ANDROID_HOME!" --licenses < "!SDK_YES!" >nul 2>nul
call "!SDKMANAGER!" "--sdk_root=!ANDROID_HOME!" !SDK_PACKAGES! < "!SDK_YES!"
set "SDK_INSTALL_RESULT=!errorlevel!"
del "!SDK_YES!" >nul 2>nul

call :enable_android_path
set "SDK_OK=1"
if defined NEED_BUILD_TOOLS ( where zipalign >nul 2>nul || set "SDK_OK=" )
if defined NEED_PLATFORM_TOOLS ( where adb >nul 2>nul || set "SDK_OK=" )
if defined NEED_NDK ( call :ndk_clang_present || set "SDK_OK=" )
if not defined SDK_OK (
  echo Android SDK package installation did not complete successfully ^(sdkmanager exit !SDK_INSTALL_RESULT!^).
  echo Install the missing packages manually, then re-run this script:
  echo   "!SDKMANAGER!" "--sdk_root=!ANDROID_HOME!"!SDK_PACKAGES!
  exit /b 1
)

setx ANDROID_HOME "!ANDROID_HOME!" >nul
setx ANDROID_SDK_ROOT "!ANDROID_HOME!" >nul
exit /b 0

:ndk_clang_present
rem Succeeds (exit 0) when a usable NDK clang wrapper is installed.
if not defined ANDROID_NDK_DIR exit /b 1
if exist "!ANDROID_NDK_DIR!\toolchains\llvm\prebuilt\windows-x86_64\bin\aarch64-linux-android28-clang.cmd" exit /b 0
exit /b 1

:ensure_cmdline_tools
set "SDKMANAGER=!ANDROID_HOME!\cmdline-tools\latest\bin\sdkmanager.bat"
if exist "!SDKMANAGER!" exit /b 0

echo ==^> Downloading Android SDK command-line tools...
if not exist "!ANDROID_HOME!" (
  mkdir "!ANDROID_HOME!"
  if not !errorlevel!==0 (
    echo Could not create the Android SDK directory at "!ANDROID_HOME!".
    exit /b 1
  )
)
if not exist "!ANDROID_HOME!\cmdline-tools" (
  mkdir "!ANDROID_HOME!\cmdline-tools"
  if not !errorlevel!==0 (
    echo Could not create the Android SDK command-line-tools directory.
    exit /b 1
  )
)

set "CMDLINE_TOOLS_ZIP=%TEMP%\commandlinetools-win-15859902_latest.zip"
set "CMDLINE_TOOLS_TMP=%TEMP%\commandlinetools-win-!RANDOM!-!RANDOM!"
powershell -NoProfile -Command "Invoke-WebRequest -UseBasicParsing -Uri 'https://dl.google.com/android/repository/commandlinetools-win-15859902_latest.zip' -OutFile $env:CMDLINE_TOOLS_ZIP"
if not !errorlevel!==0 (
  echo Failed to download the Android SDK command-line tools.
  exit /b 1
)
if not exist "!CMDLINE_TOOLS_ZIP!" (
  echo The Android SDK command-line-tools download is missing.
  exit /b 1
)

powershell -NoProfile -Command "Expand-Archive -LiteralPath $env:CMDLINE_TOOLS_ZIP -DestinationPath $env:CMDLINE_TOOLS_TMP -Force"
if not !errorlevel!==0 (
  echo Failed to extract the Android SDK command-line tools.
  exit /b 1
)
move "!CMDLINE_TOOLS_TMP!\cmdline-tools" "!ANDROID_HOME!\cmdline-tools\latest" >nul
if not !errorlevel!==0 (
  echo Failed to install the Android SDK command-line tools.
  exit /b 1
)

set "SDKMANAGER=!ANDROID_HOME!\cmdline-tools\latest\bin\sdkmanager.bat"
if not exist "!SDKMANAGER!" (
  echo The Android SDK command-line tools are still missing after download.
  exit /b 1
)
exit /b 0

:ensure_debug_keystore
rem apksigner needs a keystore; the pipeline defaults to the Android debug
rem keystore, which only exists once a tool has created it. Generate it with
rem the JDK's keytool if it is missing.
set "DEBUG_KEYSTORE=%USERPROFILE%\.android\debug.keystore"
if exist "!DEBUG_KEYSTORE!" exit /b 0

set "KEYTOOL=keytool"
where keytool >nul 2>nul
if not !errorlevel!==0 (
  if defined JAVA_HOME if exist "!JAVA_HOME!\bin\keytool.exe" set "KEYTOOL=!JAVA_HOME!\bin\keytool.exe"
)

echo ==^> Creating the Android debug keystore at "!DEBUG_KEYSTORE!"...
if not exist "%USERPROFILE%\.android" mkdir "%USERPROFILE%\.android"
"!KEYTOOL!" -genkeypair -v -keystore "!DEBUG_KEYSTORE!" -storepass android -keypass android -alias androiddebugkey -keyalg RSA -keysize 2048 -validity 10000 -dname "CN=Android Debug,O=Android,C=US"
if not exist "!DEBUG_KEYSTORE!" (
  echo Could not create the Android debug keystore automatically.
  echo Create it manually with:
  echo   keytool -genkeypair -v -keystore "!DEBUG_KEYSTORE!" -storepass android -keypass android -alias androiddebugkey -keyalg RSA -keysize 2048 -validity 10000 -dname "CN=Android Debug,O=Android,C=US"
  exit /b 1
)
exit /b 0

:update_legible
rem legible is already installed. Keep it in sync with the pinned interpreter
rem source so repository scripts that need newer builtins (for example
rem `bytes_inflate`, used by the APK builder) keep working. This is best
rem effort: if anything here fails, the working interpreter is left in place.
where git >nul 2>nul
if not %errorlevel%==0 exit /b 0
if not exist "%LEGIBLE_SRC%\Cargo.toml" exit /b 0
git -C "%LEGIBLE_SRC%" rev-parse --is-inside-work-tree >nul 2>nul
if not %errorlevel%==0 exit /b 0

set "LEGIBLE_HEAD_BEFORE="
set "LEGIBLE_HEAD_AFTER="
for /f "delims=" %%H in ('git -C "%LEGIBLE_SRC%" rev-parse HEAD 2^>nul') do set "LEGIBLE_HEAD_BEFORE=%%H"
git -C "%LEGIBLE_SRC%" remote set-url origin "%LEGIBLE_REPO%" >nul 2>nul
git -C "%LEGIBLE_SRC%" pull --ff-only origin "%LEGIBLE_BRANCH%" >nul 2>nul
if not %errorlevel%==0 (
  echo ==^> Could not check for legible interpreter updates; using the installed version.
  exit /b 0
)
for /f "delims=" %%H in ('git -C "%LEGIBLE_SRC%" rev-parse HEAD 2^>nul') do set "LEGIBLE_HEAD_AFTER=%%H"
if "!LEGIBLE_HEAD_BEFORE!"=="!LEGIBLE_HEAD_AFTER!" exit /b 0

echo ==^> Updating the legible interpreter to match the pinned source...
where cargo >nul 2>nul
if not !errorlevel!==0 if exist "%USERPROFILE%\.cargo\bin\cargo.exe" set "PATH=%USERPROFILE%\.cargo\bin;%PATH%"
where cargo >nul 2>nul
if not !errorlevel!==0 (
  echo     Rust/cargo is not available to rebuild legible; keeping the current version.
  echo     Install Rust from https://rustup.rs and re-run this script to update.
  exit /b 0
)
pushd "%LEGIBLE_SRC%"
cargo install --path . --no-default-features --locked
set "LEGIBLE_UPDATE_RESULT=%errorlevel%"
popd
if not "%LEGIBLE_UPDATE_RESULT%"=="0" echo     legible rebuild failed; keeping the previously installed version.
set "PATH=%USERPROFILE%\.cargo\bin;%PATH%"
exit /b 0

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
echo   - JDK 21:                https://learn.microsoft.com/java/openjdk/download
echo   - Android command-line tools:
echo                            https://developer.android.com/studio#command-tools
echo     Use sdkmanager to install platform-tools, "build-tools;35.0.0", and
echo     "ndk;26.1.10909125", then set JAVA_HOME and ANDROID_HOME or ANDROID_SDK_ROOT.
exit /b 1
