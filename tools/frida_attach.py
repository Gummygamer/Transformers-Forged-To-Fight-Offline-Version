import frida, sys, time, subprocess
SCRIPT = sys.argv[1] if len(sys.argv) > 1 else "tools/hook_dot.js"
OUT = sys.argv[2] if len(sys.argv) > 2 else "probe/frida_out.log"
BOOT = int(sys.argv[3]) if len(sys.argv) > 3 else 24   # wait to MENU before attaching (avoid boot-time crash)
RUN = int(sys.argv[4]) if len(sys.argv) > 4 else 16
ADB = "D:/Android/Sdk/platform-tools/adb.exe"
D = "127.0.0.1:5555"

subprocess.run([ADB, "-s", D, "shell", "am", "force-stop", "com.kabam.bigrobot"])
subprocess.run([ADB, "-s", D, "shell", "am", "start", "-n",
                "com.kabam.bigrobot/com.explodingbarrel.Activity"],
               stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
print("launched; booting %ds to menu before attach..." % BOOT, flush=True)
time.sleep(BOOT)
dev = frida.get_usb_device(timeout=15)
session = dev.attach("Transformers")
script = session.create_script(open(SCRIPT, encoding="utf-8").read())
out = open(OUT, "w", encoding="utf-8")
def on_msg(m, data):
    t = m.get("type")
    if t == "send": out.write(str(m.get("payload")) + "\n")
    elif t == "log": out.write(m.get("payload", "") + "\n")
    elif t == "error": out.write("JSERR: " + m.get("stack", str(m)) + "\n")
    out.flush()
script.on("message", on_msg)
script.load()
print("attached at menu + hooks loaded", flush=True)
time.sleep(3)
subprocess.run([ADB, "-s", D, "shell", "input", "tap", "960", "810"])  # PLAY NOW
print("tapped PLAY NOW; capturing %ds" % RUN, flush=True)
time.sleep(RUN)
try: session.detach()
except Exception: pass
out.close()
print("done -> " + OUT)
