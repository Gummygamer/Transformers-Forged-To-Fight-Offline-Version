/* Newly authored. Do-nothing stand-ins for three native libraries of the original app that the game imports with
 * DllImport: android-signal (crash reporting), apkprotect (APK signature checks) and enetlib (realtime networking).
 * The offline game uses none of their features; the stand-ins only have to exist and return "failed / empty".
 * Names and signatures come from the game's DllImport declarations. Built into three libraries by build.sh. */
#include <stddef.h>
#include <string.h>
#include <stdlib.h>

/* Mono frees every string a DllImport'ed function returns (like the C# marshaler of the original libraries expects
 * malloc'd memory), so these must be heap copies: returning a pointer to a constant corrupts the allocator. */
static const char *empty_string(void) { return strdup(""); }
#define kEmpty empty_string()

#if defined(LIB_ANDROID_SIGNAL)
int APK_Check_Start(const char *jarPath) { return 0; }
const char *APK_Check_Complete(int v) { return kEmpty; }
const char *_BR_BugReport(const char *host, const char *project, int enableSessionLog) { return kEmpty; }
void _BR_Log(const char *log) { }
void _BR_ForceHardCrash(int crashType) { }
void _BR_RegisterRecommendedSignalHandlers(void) { }
void _BR_RegisterRecommendedSimpleSignalHandlers(void) { }
int _BR_RegisterUnsafeSignalHandler(const char *signalName) { return 0; }
int _BR_RegisterSignalHandler(const char *signalName, int simple, int fatal, int swallowSignal) { return 0; }
#endif

#if defined(LIB_APKPROTECT)
int APK_Check_Start(const char *jarPath) { return 0; }
int APK_Check_Start_Salt(const char *jarPath, const char *salt) { return 0; }
int APK_Check_Step(int maxBytesToRead, int maxFilesToExamine, int stepsBeforeLog) { return 1; }
const char *APK_Check_Complete(int v) { return kEmpty; }
const char *APK_GetLibraries(void) { return kEmpty; }
#endif

#if defined(LIB_ENETLIB)
int enet_initialize(void) { return -1; }
void enet_deinitialize(void) { }
unsigned int enet_time_get(void) { return 0; }
void *enet_host_create(void *address, unsigned peerCount, unsigned channelLimit, unsigned inBw, unsigned outBw) { return NULL; }
int enet_host_service(void *host, void *event, unsigned timeout) { return -1; }
int enet_address_set_host(void *address, const char *hostname) { return -1; }
int enet_address_set_port(void *address, unsigned short port) { return -1; }
int enet_address_get_port(void *address) { return 0; }
int enet_address_get_family(void *address) { return 0; }
void enet_address_init(int family, void *address) { }
int enet_address_get_host(void *address, unsigned char *data, unsigned dataLen) { return -1; }
void *enet_host_connect(void *host, void *address, unsigned channelCount, unsigned data) { return NULL; }
int enet_host_compress_with_range_coder(void *host) { return -1; }
void *enet_packet_create(const unsigned char *data, int dataLength, unsigned flags) { return NULL; }
void enet_packet_destroy(void *packet) { }
void enet_peer_disconnect_now(void *peer, unsigned data) { }
void enet_peer_disconnect(void *peer, unsigned data) { }
int enet_peer_send(void *peer, unsigned char channel, void *packet) { return -1; }
void enet_host_destroy(void *host) { }
#endif
