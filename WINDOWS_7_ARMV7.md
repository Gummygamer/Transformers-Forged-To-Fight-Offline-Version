# Windows 7: build and run the 32-bit offline APK

This is the complete native-Windows workflow for the 32-bit `armeabi-v7a`
client. It does not require WSL, Docker, a Linux virtual machine, `patchelf`, or
LIEF. Git Bash is used only to run the repository's certificate-generation
script; its programs are Windows executables.

The binary patches are specific to `com.kabam.bigrobot` version **9.2.0**. Use
only an APK copy you are entitled to use. Do not add that APK, extracted game
files, generated keys, build output, or anything under `media/` to Git.

## 1. Install Windows 7-compatible tools

Use a 64-bit Windows 7 SP1 PC and install:

- Python 3.8.10 (64-bit), with `python.exe` on `PATH`.
- A Windows 7-compatible Git for Windows release, providing Git Bash and OpenSSL.
- 7-Zip, with `7z.exe` on `PATH`.
- JDK 8, with `java.exe` and `keytool.exe` on `PATH`.
- Android SDK Platform Tools 30.0.5.
- Android SDK Build Tools 30.0.3.
- Android NDK r21e for Windows x86-64.

New Android command-line tool releases may not start on Windows 7. The versions
above are old enough for this host and new enough for the commands below. Use
official archived Android developer downloads.

For the examples, put the repository in a short path without spaces:

```text
C:\TFTF-Offline
```

Put the pristine 9.2.0 APK here:

```text
C:\TFTF-Offline\input\Transformers-9.2.0.apk
```

Do not commit it.

## 2. Configure and check the tools

Open Command Prompt. Adjust these paths for the actual installations:

```cmd
cd /d C:\TFTF-Offline
set ANDROID_SDK_ROOT=C:\Android\Sdk
set ANDROID_NDK_ROOT=C:\Android\android-ndk-r21e
set PATH=%ANDROID_SDK_ROOT%\platform-tools;%ANDROID_SDK_ROOT%\build-tools\30.0.3;%PATH%
```

Check each tool:

```cmd
python --version
java -version
7z
adb version
zipalign -h
apksigner version
```

Python must report 3.8.x. Install the patcher's Python dependency:

```cmd
python -m pip install capstone==5.0.1
```

## 3. Check that the phone accepts 32-bit apps

Enable Developer options and USB debugging, connect and authorize the phone,
then run:

```cmd
adb devices
adb shell getprop ro.product.cpu.abilist
```

The ABI list must contain `armeabi-v7a`. A 64-bit phone is acceptable if its
Android build still supports 32-bit applications.

## 4. Generate the local server certificate

Open Git Bash:

```bash
cd /c/TFTF-Offline
bash Server/gen_certs.sh
```

This creates `Server/certs/server.pem`, which the server needs. Keep the
generated private keys local.

## 5. Build the ARMv7 runtime hook

Return to Command Prompt:

```cmd
cd /d C:\TFTF-Offline
"%ANDROID_NDK_ROOT%\toolchains\llvm\prebuilt\windows-x86_64\bin\armv7a-linux-androideabi21-clang.cmd" -shared -O2 -fPIC -Wl,-soname,libdothook.so -o tools\nativehook\libdothook-armeabi-v7a.so tools\nativehook\hook_arm32.c tools\nativehook\inapk_server.c -llog
```

Confirm the output exists:

```cmd
dir tools\nativehook\libdothook-armeabi-v7a.so
```

This generated `.so` is a local build artifact and must not be committed.

## 6. Extract and patch the 32-bit `libil2cpp.so`

Extract only the ARMv7 library:

```cmd
mkdir work 2>nul
7z x -y input\Transformers-9.2.0.apk -owork\original32 "lib\armeabi-v7a\libil2cpp.so"
dir work\original32\lib\armeabi-v7a\libil2cpp.so
```

Apply the six ARMv7 code patches and the native, layout-preserving dependency
injection:

```cmd
python patches\patch_il2cpp.py --abi armeabi-v7a --needed inplace --apply -o work\libil2cpp-armv7-patched.so work\original32\lib\armeabi-v7a\libil2cpp.so
```

The last lines must include:

```text
[+] added DT_NEEDED libdothook.so (ARMv7 in-place, no layout shift)
[+] wrote work\libil2cpp-armv7-patched.so
```

The Windows method is strict and specific to the pristine 9.2.0 ARMv7 library.
It fails instead of guessing if the expected ELF layout differs. The existing
Linux route is unchanged: on Linux ARMv7 still defaults to `patchelf`, or it can
be selected explicitly with `--needed patchelf`.

## 7. Build the unsigned 32-bit APK

Choose Wi-Fi/LAN or USB mode.

### Wi-Fi/LAN

Run `ipconfig` and find the PC's IPv4 address on the same private network as the
phone. This example uses `192.168.1.25`:

```cmd
mkdir build 2>nul
python Server\build_phone_apk.py --abi armeabi-v7a --server-host 192.168.1.25 --patched-il2cpp work\libil2cpp-armv7-patched.so input\Transformers-9.2.0.apk build\tftf-armv7-wifi-unsigned.apk
```

Use a DHCP reservation if possible. If the PC address changes, rebuild with the
new address. Guest Wi-Fi frequently blocks communication between devices.

### USB

USB mode uses the default server host, `127.0.0.1`, with ADB reverse:

```cmd
mkdir build 2>nul
python Server\build_phone_apk.py --abi armeabi-v7a --patched-il2cpp work\libil2cpp-armv7-patched.so input\Transformers-9.2.0.apk build\tftf-armv7-usb-unsigned.apk
```

The builder embeds the patched ARMv7 `libil2cpp.so` and current ARMv7 hook
directly into the pristine source APK. It removes `arm64-v8a` by default so
Android cannot select the unpatched 64-bit client. The new
`--patched-il2cpp` option is optional; existing Linux workflows that supply an
already-patched source APK continue to work unchanged.

For a self-contained no-PC build, add `--bundle-server --scheme http --server-host
127.0.0.1 --server-port 8080` to the command in this section. After signing and installing that
APK, skip section 4 (certificate), the reverse/LAN host-server setup where it applies in the
following workflow, section 9 (start the local server), and the `adb reverse` steps. Still use
section 8 to align and sign the APK; nothing on the PC needs to run at play time.

## 8. Align and sign

Create a debug signing key once, if it does not already exist:

```cmd
if not exist "%USERPROFILE%\.android" mkdir "%USERPROFILE%\.android"
keytool -genkeypair -keystore "%USERPROFILE%\.android\debug.keystore" -storepass android -keypass android -alias androiddebugkey -keyalg RSA -keysize 2048 -validity 10000 -dname "CN=Android Debug,O=Android,C=US"
```

For Wi-Fi:

```cmd
zipalign -f -p 4 build\tftf-armv7-wifi-unsigned.apk build\tftf-armv7-wifi-aligned.apk
apksigner sign --ks "%USERPROFILE%\.android\debug.keystore" --ks-key-alias androiddebugkey --ks-pass pass:android --key-pass pass:android --out build\Transformers-9.2-offline-armv7-wifi.apk build\tftf-armv7-wifi-aligned.apk
apksigner verify --verbose build\Transformers-9.2-offline-armv7-wifi.apk
```

For USB:

```cmd
zipalign -f -p 4 build\tftf-armv7-usb-unsigned.apk build\tftf-armv7-usb-aligned.apk
apksigner sign --ks "%USERPROFILE%\.android\debug.keystore" --ks-key-alias androiddebugkey --ks-pass pass:android --key-pass pass:android --out build\Transformers-9.2-offline-armv7-usb.apk build\tftf-armv7-usb-aligned.apk
apksigner verify --verbose build\Transformers-9.2-offline-armv7-usb.apk
```

Verify that the finished Wi-Fi APK has ARMv7 libraries and no arm64 libraries:

```cmd
7z l build\Transformers-9.2-offline-armv7-wifi.apk | findstr /I "armeabi-v7a"
7z l build\Transformers-9.2-offline-armv7-wifi.apk | findstr /I "arm64-v8a"
```

The first command should list libraries. The second should print nothing.
Substitute the USB filename when using USB mode.

## 9. Start the local server

Start a separate Command Prompt and leave it open:

```cmd
cd /d C:\TFTF-Offline
python Server\run_local.py
```

The server listens on HTTPS 8443 and HTTP 8080.

For Wi-Fi, open only those ports on the private Windows network. Run once from
an elevated Command Prompt:

```cmd
netsh advfirewall firewall add rule name="TFTF offline server" dir=in action=allow protocol=TCP localport=8080,8443 profile=private
```

The stock-phone build does not need a phone CA installation; the native
validation patches accept the locally generated server certificate.

## 10. Install and launch

For Wi-Fi:

```cmd
adb install --no-incremental -r -d build\Transformers-9.2-offline-armv7-wifi.apk
adb shell am force-stop com.kabam.bigrobot
adb shell am start -n com.kabam.bigrobot/com.explodingbarrel.Activity
```

The USB cable can then be disconnected. Keep the phone, PC, and server on the
same LAN.

For USB:

```cmd
adb install --no-incremental -r -d build\Transformers-9.2-offline-armv7-usb.apk
adb reverse tcp:8443 tcp:8443
adb reverse tcp:8080 tcp:8080
adb reverse --list
adb shell am force-stop com.kabam.bigrobot
adb shell am start -n com.kabam.bigrobot/com.explodingbarrel.Activity
```

Keep USB debugging connected. Reapply both reverse rules after reconnecting USB
or rebooting the phone.

If ADB does not recognize `--no-incremental`, use Package Manager directly:

```cmd
adb push build\Transformers-9.2-offline-armv7-usb.apk /data/local/tmp/tftf-armv7.apk
adb shell pm install -r -d /data/local/tmp/tftf-armv7.apk
adb shell rm /data/local/tmp/tftf-armv7.apk
```

Use the Wi-Fi filename instead when appropriate.

## Troubleshooting

`INSTALL_FAILED_UPDATE_INCOMPATIBLE` means the installed app was signed with a
different key. Uninstalling permits installation but erases that installation's
local app data:

```cmd
adb uninstall com.kabam.bigrobot
```

Do that only if losing its data is acceptable.

If login hangs:

1. Confirm `python Server\run_local.py` is still running.
2. For Wi-Fi, confirm the PC still has the IP embedded in the APK.
3. Confirm Windows Firewall allows TCP 8080 and 8443 on the private profile.
4. For USB, rerun `adb reverse --list`.
5. Confirm the APK contains `lib/armeabi-v7a/libdothook.so`, the patched
   `libil2cpp.so`, and no `lib/arm64-v8a/`.
6. Wait about 45 seconds at first boot before tapping the title screen.
