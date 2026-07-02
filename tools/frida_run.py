import frida, sys, time, subprocess
SCRIPT = sys.argv[1] if len(sys.argv) > 1 else "tools/hook_dot.js"
OUT = sys.argv[2] if len(sys.argv) > 2 else "probe/frida_out.log"
BOOT = int(sys.argv[3]) if len(sys.argv) > 3 else 25
RUN = int(sys.argv[4]) if len(sys.argv) > 4 else 18
ADB = "D:/Android/Sdk/platform-tools/adb.exe"
D = "127.0.0.1:5555"

dev = frida.get_usb_device(timeout=15)
subprocess.run([ADB, "-s", D, "shell", "am", "force-stop", "com.kabam.bigrobot"])
pid = dev.spawn(["com.kabam.bigrobot"])
session = dev.attach(pid)
script = session.create_script(open(SCRIPT, encoding="utf-8").read())
out = open(OUT, "w", encoding="utf-8")
def on_msg(m, data):
    t = m.get("type")
    if t == "send":
        out.write(str(m.get("payload")) + "\n")
    elif t == "log":
        out.write(m.get("payload", "") + "\n")
    elif t == "error":
        out.write("JSERR: " + m.get("stack", str(m)) + "\n")
    out.flush()
script.on("message", on_msg)
script.load()
dev.resume(pid)
print("spawned, resumed; booting %ds" % BOOT, flush=True)
time.sleep(BOOT)
subprocess.run([ADB, "-s", D, "shell", "input", "tap", "960", "810"])  # PLAY NOW
print("tapped PLAY NOW; capturing %ds" % RUN, flush=True)
time.sleep(RUN)
try:
    session.detach()
except Exception:
    pass
out.close()
print("done -> " + OUT)
