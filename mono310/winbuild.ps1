# Native Windows driver for the 3.1.0 Unity-2020 recompile: a Windows x64 (Mono) player built with the Windows
# Unity 2020.3.31f1 editor (no Docker). See README.md, "Windows build".
# Default is the assetless launcher shell (behaviour only; the player converts the user's own 3.1.0 and 9.2.0 APKs
# on first run). TFTF_SHELL=0 builds the full recompile with the ripped assets baked in (local testing only).
# Usage: powershell -File mono310\winbuild.ps1 [assemble|assets|build|all]   (default: all)
# Reuses an existing rip and decompile (RIPPED, ILSPY_OUT; produced by run.sh rip/decompile).
param([string]$Step = "all")
$ErrorActionPreference = "Stop"
$Mono310 = $PSScriptRoot
$Repo = Split-Path $Mono310 -Parent
function Default([string]$name, [string]$value) { if (-not [Environment]::GetEnvironmentVariable($name)) { [Environment]::SetEnvironmentVariable($name, $value) } }

Default TFTF_SHELL "1"
if ($env:TFTF_SHELL -ne "1") { $env:TFTF_SHELL = "" }
$Shell = $env:TFTF_SHELL -eq "1"
Default TFTF_WORK "C:\tftf-310-win"
Default TFTF_310_APK "$env:USERPROFILE\Downloads\com.kabam.bigrobot_v3.1.0-822754_Android-4.4.apk"
Default TFTF_92_APK "$Repo\transformers-forged-to-fight-9-2-0.apk"
Default TFTF_TREES_BIN "$env:TFTF_WORK\treedb\trees.bin"      # run.sh treedb; shell only
# shell only: legible-lang's development branch (short-circuit and/or, which the server scripts rely on), built with
# cargo build --release --no-default-features --bin legible
Default TFTF_LEGIBLE_WIN "$env:TFTF_WORK\legible-target-dev\release\legible.exe"
Default RIPPED "$env:TFTF_WORK\ripped"
Default ILSPY_OUT "$env:TFTF_WORK\ilspy-out"
Default PROJECT ("$env:TFTF_WORK\" + $(if ($Shell) { "ShellProject" } else { "UnityProject" }))
Default UNITY_2020_EXE "C:\Program Files\Unity\Hub\Editor\2020.3.31f1\Editor\Unity.exe"
Default PYTHON "python"
$env:TFTF_TARGET = "windows"
$env:PYTHONUTF8 = "1"          # the repairs read and write the decompiled sources as UTF-8
$P = $env:PROJECT
$S = "$Mono310\scripts"

function Py { & $env:PYTHON @args; if ($LASTEXITCODE) { throw "python $($args[0]) failed ($LASTEXITCODE)" } }
function Need([string[]]$vars) {
    foreach ($v in $vars) {
        if (-not (Test-Path ([Environment]::GetEnvironmentVariable($v)))) { throw "missing $v`: $([Environment]::GetEnvironmentVariable($v))" }
    }
}

function Step-Assemble {
    Need @("RIPPED", "ILSPY_OUT", "TFTF_310_APK")
    if ($Shell) { Need @("TFTF_TREES_BIN") }
    Py "$S\assemble_project.py"
}

function Step-Assets {  # same as run.sh step_assets; the shell keeps only the authored shaders
    $N = "$P\Assets"; $r = "$N\Resources\Shader310"
    New-Item -ItemType Directory -Force $N | Out-Null
    if (-not $Shell) {
        $E = "$env:RIPPED\ExportedProject\Assets"
        foreach ($d in Get-ChildItem $E) {
            if ($d.Name -in "Scripts", "Plugins", "Scenes", "Scripts.meta", "Plugins.meta") { continue }
            Copy-Item $d.FullName $N -Recurse -Force
        }
        Copy-Item "$E\Scenes" $N -Recurse -Force
        Py "$S\convert_audio.py" $N
    }
    Remove-Item -Recurse -Force "$N\Shader310", "$N\Shader310.meta" -ErrorAction SilentlyContinue
    Py "$S\gen_shaders.py" $r
    if (-not $Shell -and (Test-Path $env:TFTF_92_APK)) {   # the shell reads the donor moves from the 9.2.0 APK at install
        & $env:PYTHON "$S\extract_donor_moves.py" $env:TFTF_92_APK "$N\Resources\movedata92"
        if ($LASTEXITCODE) { Write-Warning "donor move data not extracted; 9.2 fighters will have no hit events" }
    }
    $sh = "$Mono310\unity\shaders"
    Copy-Item "$sh\EBG_UI_BlendColored.shader" "$r\EBG_UI_BlendColored.shader"
    Copy-Item "$sh\EBBlit.shader" "$r\Hidden_EB_Blit.shader"
    Copy-Item "$sh\EBNoToneMapBlit.shader" "$r\Hidden_EB_PostFX_NoToneMapBlit.shader"
    Copy-Item "$sh\EBToneMapBlit.shader" "$r\Hidden_EB_PostFX_ToneMapBlit.shader"
    Copy-Item "$sh\EBUIScreenCapture.shader" "$r\Hidden_EB_UIScreenCapture.shader"
    Copy-Item "$sh\HudHealth.shader" "$r\Hud_Health.shader"
    Copy-Item "$sh\HudHealth.shader" "$r\EBG_UI_HudHealth.shader"
    if (-not $Shell) { Copy-Item "$sh\HudHealth.shader" "$N\Resources\ui\shaders\Hud_Health.shader" }
}

function Copy-ApkStreamingAssets {  # full build only: what Android finalize copies into the APK
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $sa = "$P\Assets\StreamingAssets"
    $zip = [IO.Compression.ZipFile]::OpenRead($env:TFTF_310_APK)
    try {
        foreach ($e in $zip.Entries) {
            $n = $e.FullName
            if (-not $n.StartsWith("assets/") -or $n.StartsWith("assets/bin/") -or $n.EndsWith("/")) { continue }
            $t = Join-Path $sa $n.Substring(7)
            New-Item -ItemType Directory -Force (Split-Path $t) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($e, $t, $true)
        }
    } finally { $zip.Dispose() }
}

function Step-Build {
    if (-not (Test-Path $env:UNITY_2020_EXE)) { throw "set UNITY_2020_EXE to the Windows Unity 2020.3.31f1 Unity.exe" }
    if ($Shell) { Need @("TFTF_LEGIBLE_WIN") }
    else {
        $endpoint = if ($env:TFTF_ENDPOINT) { $env:TFTF_ENDPOINT } else { "http://127.0.0.1:8080" }
        [IO.File]::WriteAllText("$P\Assets\Resources\endpointconfig.bytes", '{"Default":{"Prod":"' + $endpoint + '"}}')
        Copy-ApkStreamingAssets
    }
    $out = "$P\win\TFTF.exe"; $log = "$P\unity-win-build.log"
    Remove-Item -Recurse -Force "$P\win" -ErrorAction SilentlyContinue
    $env:TFTF_310_OUTPUT = $out
    # A Hub-entitlement Personal license is accepted by the 2020.3 editor only when it runs with a window;
    # -batchmode then fails with "not been activated". BuildWindows exits the editor itself either way.
    $batch = if ($env:TFTF_UNITY_BATCH -eq "1") { @("-batchmode", "-nographics") } else { @() }
    $u = Start-Process -FilePath $env:UNITY_2020_EXE -WorkingDirectory "C:\" -Wait -PassThru -ArgumentList ($batch + @(
        "-projectPath", "`"$P`"", "-buildTarget", "StandaloneWindows64",
        "-executeMethod", "Recompile310Build.BuildWindows", "-logFile", "`"$log`""))
    $result = Select-String -Path $log -Pattern "Windows build:" | Select-Object -Last 1
    if ($result) { $result.Line }
    if (-not (Select-String -Path $log -Pattern "Windows build: Succeeded" -Quiet)) {
        Select-String -Path $log -Pattern "error CS" | Select-Object -First 20 | ForEach-Object { $_.Line }
        throw "Unity Windows build failed (exit $($u.ExitCode)); see $log"
    }
    # The Android build's SDK glue (JNI, Android-only native libraries) throws on a PC player: stub it (needs the .NET 10 SDK).
    & dotnet run --file "$Mono310\launcher\tools\jnistub.cs" -- "$P\win\TFTF_Data\Managed"
    if ($LASTEXITCODE) { throw "jnistub failed ($LASTEXITCODE)" }
    if ($Shell) { Copy-Item $env:TFTF_LEGIBLE_WIN "$P\win\legible.exe" }   # TftfServer starts it from next to TFTF.exe
    "done: $out"
}

switch ($Step) {
    "assemble" { Step-Assemble }
    "assets" { Step-Assets }
    "build" { Step-Build }
    "all" { Step-Assemble; Step-Assets; Step-Build }
    default { throw "usage: winbuild.ps1 [assemble|assets|build|all]" }
}
