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

#if defined(__aarch64__)
#define _GNU_SOURCE
#include <stdio.h>
#include <stdint.h>
#include <stdbool.h>
#include <dlfcn.h>
#include <pthread.h>
#include <unistd.h>
#include <sys/mman.h>
#include <android/log.h>

#define LOG_TAG "TFTF_SP3"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)

/* --- ARM64 Inline Hook Engine --- */
typedef void* (*fn8)(void*, void*, void*, void*, void*, void*, void*, void*);

static void write_jump(uint8_t* dst, void* target) {
    uint32_t* p = (uint32_t*)dst;
    p[0] = 0x58000051;          // ldr x17, #8
    p[1] = 0xD61F0220;          // br  x17
    *(uint64_t*)(dst + 8) = (uint64_t)target;
}

static int relocate(uint8_t* tr, uint8_t* src, int ninstr) {
    int o = 0;
    for (int i = 0; i < ninstr; i++) {
        uint32_t in = *(uint32_t*)(src + i * 4);
        uint64_t pc = (uint64_t)(src + i * 4);
        if ((in & 0x7C000000) == 0x14000000) { // B / BL (imm26)
            int64_t off = (int64_t)(in << 6) >> 4; uint64_t tgt = pc + off;
            uint32_t link = in & 0x80000000;
            *(uint32_t*)(tr + o) = 0x58000050; o += 4;
            *(uint32_t*)(tr + o) = link ? 0xD63F0200 : 0xD61F0200; o += 4;
            *(uint64_t*)(tr + o) = tgt; o += 8;
        } else if ((in & 0xFF000010) == 0x54000000) { // B.cond (imm19)
            int64_t off = (int64_t)((in >> 5) & 0x7FFFF); off = (off << 45) >> 43; uint64_t tgt = pc + off;
            uint32_t cond = in & 0xF;
            *(uint32_t*)(tr + o) = 0x54000000 | (0x14 >> 2 << 5) | (cond ^ 1); o += 4;
            *(uint32_t*)(tr + o) = 0x58000050; o += 4; *(uint32_t*)(tr + o) = 0xD61F0200; o += 4; *(uint64_t*)(tr + o) = tgt; o += 8;
        } else if ((in & 0x7E000000) == 0x34000000) { // CBZ/CBNZ (imm19)
            int64_t off = (int64_t)((in >> 5) & 0x7FFFF); off = (off << 45) >> 43; uint64_t tgt = pc + off;
            uint32_t inv = in ^ 0x01000000;
            *(uint32_t*)(tr + o) = (inv & 0xFF00001F) | (0x14 >> 2 << 5); o += 4;
            *(uint32_t*)(tr + o) = 0x58000050; o += 4; *(uint32_t*)(tr + o) = 0xD61F0200; o += 4; *(uint64_t*)(tr + o) = tgt; o += 8;
        } else if ((in & 0x7E000000) == 0x36000000) { // TBZ/TBNZ (imm14)
            int64_t off = (int64_t)((in >> 5) & 0x3FFF); off = (off << 50) >> 48; uint64_t tgt = pc + off;
            uint32_t inv = in ^ 0x01000000;
            *(uint32_t*)(tr + o) = (inv & 0xFFF8001F) | (0x14 >> 2 << 5); o += 4;
            *(uint32_t*)(tr + o) = 0x58000050; o += 4; *(uint32_t*)(tr + o) = 0xD61F0200; o += 4; *(uint64_t*)(tr + o) = tgt; o += 8;
        } else if ((in & 0x9F000000) == 0x10000000) { // ADR
            uint32_t rd = in & 0x1F; int64_t imm = (((in >> 5) & 0x7FFFF) << 2) | ((in >> 29) & 3); imm = (imm << 43) >> 43;
            uint64_t tgt = pc + imm;
            *(uint32_t*)(tr + o) = 0x58000040 | rd; o += 4; *(uint32_t*)(tr + o) = 0x14000003; o += 4; *(uint64_t*)(tr + o) = tgt; o += 8;
        } else if ((in & 0x9F000000) == 0x90000000) { // ADRP
            uint32_t rd = in & 0x1F; int64_t imm = (((in >> 5) & 0x7FFFF) << 2) | ((in >> 29) & 3); imm = (imm << 43) >> 43; imm <<= 12;
            uint64_t tgt = (pc & ~0xFFFULL) + imm;
            *(uint32_t*)(tr + o) = 0x58000040 | rd; o += 4; *(uint32_t*)(tr + o) = 0x14000003; o += 4; *(uint64_t*)(tr + o) = tgt; o += 8;
        } else if ((in & 0x3B000000) == 0x18000000) { // LDR (literal)
            uint32_t rt = in & 0x1F; int64_t off = (int64_t)((in >> 5) & 0x7FFFF); off = (off << 45) >> 43; uint64_t tgt = pc + off;
            int is64 = (in >> 30) & 1;
            *(uint32_t*)(tr + o) = 0x58000040 | rt; o += 4; *(uint32_t*)(tr + o) = 0x14000003; o += 4; *(uint64_t*)(tr + o) = tgt; o += 8;
            *(uint32_t*)(tr + o) = is64 ? (0xF9400000 | rt | (rt << 5)) : (0xB9400000 | rt | (rt << 5)); o += 4;
        } else {
            *(uint32_t*)(tr + o) = in; o += 4;
        }
    }
    return o;
}

static int inline_hook(void* target, void* handler, fn8* orig_out) {
    uint8_t* t = (uint8_t*)target;
    uintptr_t pg = (uintptr_t)t & ~0xFFFUL;
    if (mprotect((void*)pg, 0x2000, PROT_READ | PROT_WRITE | PROT_EXEC) != 0) {
        LOGE("mprotect failed on %p", t);
        return -1;
    }
    uint8_t* tr = (uint8_t*)mmap(NULL, 256, PROT_READ | PROT_WRITE | PROT_EXEC, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    if (tr == MAP_FAILED) {
        LOGE("mmap failed for trampoline");
        return -1;
    }
    int trlen = relocate(tr, t, 4);
    write_jump(tr + trlen, t + 16);
    __builtin___clear_cache((char*)tr, (char*)tr + trlen + 16);
    *orig_out = (fn8)tr;
    write_jump(t, handler);
    __builtin___clear_cache((char*)t, (char*)t + 16);
    LOGI("Successfully hooked %p -> %p (trampoline %p)", t, handler, tr);
    return 0;
}

/* --- IL2CPP Dynamic Types and Functions --- */
typedef void* (*il2cpp_domain_get_fn)(void);
typedef void* (*il2cpp_domain_assembly_open_fn)(void* domain, const char* name);
typedef void* (*il2cpp_assembly_get_image_fn)(void* assembly);
typedef void* (*il2cpp_class_from_name_fn)(void* image, const char* namespaze, const char* name);
typedef void* (*il2cpp_class_get_method_from_name_fn)(void* klass, const char* name, int argsCount);
typedef void* (*il2cpp_class_get_field_from_name_fn)(void* klass, const char* name);
typedef size_t (*il2cpp_field_get_offset_fn)(void* field);
typedef void* (*il2cpp_string_new_fn)(const char* str);
typedef uint16_t* (*il2cpp_string_chars_fn)(void*);
typedef int32_t (*il2cpp_string_length_fn)(void*);

static il2cpp_domain_get_fn p_il2cpp_domain_get = NULL;
static il2cpp_domain_assembly_open_fn p_il2cpp_domain_assembly_open = NULL;
static il2cpp_assembly_get_image_fn p_il2cpp_assembly_get_image = NULL;
static il2cpp_class_from_name_fn p_il2cpp_class_from_name = NULL;
static il2cpp_class_get_method_from_name_fn p_il2cpp_class_get_method_from_name = NULL;
static il2cpp_class_get_field_from_name_fn p_il2cpp_class_get_field_from_name = NULL;
static il2cpp_field_get_offset_fn p_il2cpp_field_get_offset = NULL;
static il2cpp_string_new_fn p_il2cpp_string_new = NULL;
static il2cpp_string_chars_fn p_il2cpp_string_chars = NULL;
static il2cpp_string_length_fn p_il2cpp_string_length = NULL;

/* UnityEngine Engine APIs */
typedef void* (*fn_get_transform)(void* go, void* method);
typedef void* (*fn_get_child)(void* tf, int index, void* method);
typedef int (*fn_get_child_count)(void* tf, void* method);
typedef void* (*fn_get_game_object)(void* comp, void* method);
typedef void* (*fn_get_name)(void* obj, void* method);
typedef void (*fn_set_name)(void* obj, void* str, void* method);

static fn_get_transform p_go_get_transform = NULL;
static fn_get_child p_tf_GetChild = NULL;
static fn_get_child_count p_tf_get_childCount = NULL;
static fn_get_game_object p_comp_get_gameObject = NULL;
static fn_get_name p_get_name = NULL;
static fn_set_name p_set_name = NULL;

static fn8 orig_FixupEntity = NULL;
static size_t off_entityFullId = 0x28;

static bool obj_ok(void* p) {
    uintptr_t u = (uintptr_t)p;
    return u >= 0x10000 && (u & 7) == 0 && u < 0x800000000000ULL;
}

static int read_il2cpp_str(void* str_obj, char* out, size_t out_sz) {
    if (!str_obj || !obj_ok(str_obj) || out_sz == 0) {
        if (out && out_sz > 0) out[0] = 0;
        return 0;
    }
    int32_t len = 0;
    if (p_il2cpp_string_length) {
        len = p_il2cpp_string_length(str_obj);
    } else {
        len = *(int32_t*)((char*)str_obj + 16);
    }
    if (len <= 0 || len >= (int32_t)out_sz) {
        out[0] = 0;
        return 0;
    }
    uint16_t* chars = NULL;
    if (p_il2cpp_string_chars) {
        chars = p_il2cpp_string_chars(str_obj);
    } else {
        chars = (uint16_t*)((char*)str_obj + 20);
    }
    if (!chars) {
        out[0] = 0;
        return 0;
    }
    for (int i = 0; i < len; i++) {
        chars[i] < 128 ? (out[i] = (char)chars[i]) : (out[i] = '?');
    }
    out[len] = 0;
    return 1;
}

static int extract_actor_expected_name(const char* fullId, const char* locatorTag, char* out, size_t out_sz) {
    if (!fullId || !locatorTag || !out || out_sz == 0) return 0;
    const char* loc = strstr(fullId, locatorTag);
    if (!loc) return 0;
    const char* p = loc - 1;
    while (p >= fullId && *p != '|') p--;
    const char* start = p + 1;
    size_t len = (size_t)(loc - start);
    if (len == 0 || len >= out_sz) return 0;
    memcpy(out, start, len);
    out[len] = 0;
    return 1;
}

static int str_replace_first(const char* src, const char* from, const char* to, char* dst, size_t dst_sz) {
    if (!src || !from || !to || !dst || dst_sz == 0) return 0;
    const char* p = strstr(src, from);
    if (!p) return 0;
    size_t prefix_len = (size_t)(p - src);
    size_t from_len = strlen(from);
    size_t to_len = strlen(to);
    size_t suffix_len = strlen(p + from_len);
    if (prefix_len + to_len + suffix_len >= dst_sz) return 0;
    memcpy(dst, src, prefix_len);
    memcpy(dst + prefix_len, to, to_len);
    memcpy(dst + prefix_len + to_len, p + from_len, suffix_len + 1);
    return 1;
}

static void remap_track_for_redeco(void* track, const char* fullId, const char* bot) {
    if (!track || !fullId || !bot || !p_il2cpp_string_new) return;
    char new_fullId[512] = {0};
    int replaced = 0;

    // 1. Lifeline (borrows Arcee)
    if (strstr(bot, "lifeline")) {
        if (str_replace_first(fullId, "arcee_gs_deluxe2014", "lifeline_gs_deluxe2014", new_fullId, sizeof(new_fullId))) {
            replaced = 1;
        }
    }
    // 2. Thrust (borrows Dirge)
    else if (strstr(bot, "thrust")) {
        if (str_replace_first(fullId, "dirge_gs_deluxe2008", "thrust_gs_deluxe2008", new_fullId, sizeof(new_fullId))) {
            replaced = 1;
        }
    }
    // 3. SG Optimus Prime (borrows Nemesis Prime & Optimus Prime)
    else if (strstr(bot, "optimusprime_sg")) {
        char temp[512] = {0};
        const char* cur = fullId;
        if (str_replace_first(cur, "OptimusPrime_GS_V", "NemesisPrime_GS_Voyager2015", temp, sizeof(temp))) {
            cur = temp;
            replaced = 1;
        }
        if (str_replace_first(cur, "nemesisprime_gs_voyager2015", "optimusprime_sg_gs_voyager2015", new_fullId, sizeof(new_fullId))) {
            replaced = 1;
        } else if (replaced) {
            strncpy(new_fullId, temp, sizeof(new_fullId) - 1);
        }
    }
    // 4. Seekers
    else if (strstr(bot, "hotlink") || strstr(bot, "acidstorm") || strstr(bot, "sunstorm") ||
             strstr(bot, "ionstorm") || strstr(bot, "novastorm") || strstr(bot, "bitstream") ||
             strstr(bot, "starscream_ghost")) {
        if (str_replace_first(fullId, "thundercracker_gs_leader2015", bot, new_fullId, sizeof(new_fullId))) {
            replaced = 1;
        }
    }
    // 5. Star Saber (borrows Jetfire)
    else if (strstr(bot, "starsaber")) {
        char temp[512] = {0};
        const char* cur = fullId;
        if (str_replace_first(cur, "jetfire_gs_leader2014", "starsaber_gs_leader2014", temp, sizeof(temp))) {
            cur = temp;
            replaced = 1;
        }
        if (str_replace_first(cur, "trail_jetfire_gs", "trail_starsaber_gs", new_fullId, sizeof(new_fullId))) {
            replaced = 1;
        } else if (replaced) {
            strncpy(new_fullId, temp, sizeof(new_fullId) - 1);
        }
    }

    if (replaced && new_fullId[0]) {
        void* new_f_str = p_il2cpp_string_new(new_fullId);
        if (new_f_str && obj_ok(new_f_str)) {
            *(void**)((char*)track + off_entityFullId) = new_f_str;
            LOGI("MATINEE_REMAP_TRACK bot='%s': '%s' -> '%s'", bot, fullId, new_fullId);
        }
    }
}

static void* s_saved_actor0_obj = NULL;
static char s_saved_actor0_name[64] = {0};
static void* s_saved_actor1_obj = NULL;
static char s_saved_actor1_name[64] = {0};

/* Capture the actor GameObject directly from the locator GameObject's child 0 */
static void capture_actor_from_locator(void* locator_go, int actor_idx) {
    if (!locator_go || !obj_ok(locator_go) || !p_go_get_transform || !p_tf_get_childCount ||
        !p_tf_GetChild || !p_comp_get_gameObject || !p_get_name) return;

    void* tf = p_go_get_transform(locator_go, NULL);
    if (!tf || !obj_ok(tf)) return;

    int cc = p_tf_get_childCount(tf, NULL);
    if (cc > 0) {
        void* child_tf = p_tf_GetChild(tf, 0, NULL);
        if (child_tf && obj_ok(child_tf)) {
            void* actor = p_comp_get_gameObject(child_tf, NULL);
            if (actor && obj_ok(actor)) {
                void* n_str = p_get_name(actor, NULL);
                char cur_name[64] = {0};
                if (n_str && obj_ok(n_str)) read_il2cpp_str(n_str, cur_name, sizeof(cur_name));
                if (actor_idx == 0) {
                    s_saved_actor0_obj = actor;
                    strncpy(s_saved_actor0_name, cur_name, sizeof(s_saved_actor0_name) - 1);
                    s_saved_actor0_name[sizeof(s_saved_actor0_name) - 1] = '\0';
                    LOGI("CAPTURED actor0 from locator: obj=%p name='%s'", s_saved_actor0_obj, s_saved_actor0_name);
                } else {
                    s_saved_actor1_obj = actor;
                    strncpy(s_saved_actor1_name, cur_name, sizeof(s_saved_actor1_name) - 1);
                    s_saved_actor1_name[sizeof(s_saved_actor1_name) - 1] = '\0';
                    LOGI("CAPTURED actor1 from locator: obj=%p name='%s'", s_saved_actor1_obj, s_saved_actor1_name);
                }
            }
        }
    }
}

static void sync_actor_name_for_track(void* track) {
    if (!obj_ok(track) || !p_il2cpp_string_new || !p_get_name || !p_set_name) return;
    void* f_str = *(void**)((char*)track + off_entityFullId);
    if (!f_str || !obj_ok(f_str)) return;
    char fullId[512] = {0};
    if (!read_il2cpp_str(f_str, fullId, sizeof(fullId)) || !fullId[0]) return;

    // Check Actor 0 (Player)
    if (strstr(fullId, "|Actor0_locator") && s_saved_actor0_obj && obj_ok(s_saved_actor0_obj)) {
        remap_track_for_redeco(track, fullId, s_saved_actor0_name);

        f_str = *(void**)((char*)track + off_entityFullId);
        if (f_str && obj_ok(f_str)) read_il2cpp_str(f_str, fullId, sizeof(fullId));

        char exp_name[64] = {0};
        if (extract_actor_expected_name(fullId, "|Actor0_locator", exp_name, sizeof(exp_name))) {
            void* cur_n_str = p_get_name(s_saved_actor0_obj, NULL);
            char cur_name[64] = {0};
            if (cur_n_str && obj_ok(cur_n_str)) read_il2cpp_str(cur_n_str, cur_name, sizeof(cur_name));
            if (strcmp(cur_name, exp_name) != 0) {
                void* new_name_str = p_il2cpp_string_new(exp_name);
                p_set_name(s_saved_actor0_obj, new_name_str, NULL);
                LOGI("MATINEE_ALIAS actor0: '%s' -> '%s' (orig '%s')", cur_name, exp_name, s_saved_actor0_name);
            }
        }
    }

    // Check Actor 1 (Enemy)
    if (strstr(fullId, "|Actor1_locator") && s_saved_actor1_obj && obj_ok(s_saved_actor1_obj)) {
        char exp_name[64] = {0};
        if (extract_actor_expected_name(fullId, "|Actor1_locator", exp_name, sizeof(exp_name))) {
            void* cur_n_str = p_get_name(s_saved_actor1_obj, NULL);
            char cur_name[64] = {0};
            if (cur_n_str && obj_ok(cur_n_str)) read_il2cpp_str(cur_n_str, cur_name, sizeof(cur_name));
            if (strcmp(cur_name, exp_name) != 0) {
                void* new_name_str = p_il2cpp_string_new(exp_name);
                p_set_name(s_saved_actor1_obj, new_name_str, NULL);
                LOGI("MATINEE_ALIAS actor1: '%s' -> '%s' (orig '%s')", cur_name, exp_name, s_saved_actor1_name);
            }
        }
    }
}

static void* hook_FixupEntity(void* track, void* a1, void* a2, void* a3, void* a4, void* a5, void* a6, void* a7) {
    char name[64] = {0};
    char fullId[512] = {0};
    if (obj_ok(track)) {
        void* n_str = *(void**)((char*)track + 0x18);
        void* f_str = *(void**)((char*)track + off_entityFullId);
        if (n_str && obj_ok(n_str)) read_il2cpp_str(n_str, name, sizeof(name));
        if (f_str && obj_ok(f_str)) read_il2cpp_str(f_str, fullId, sizeof(fullId));
    }

    sync_actor_name_for_track(track);

    void* r = orig_FixupEntity(track, a1, a2, a3, a4, a5, a6, a7);

    if (obj_ok(track)) {
        void* entity = *(void**)((char*)track + 0x58);
        if (entity) {
            if (strcmp(name, "Actor0_locator") == 0) {
                capture_actor_from_locator(entity, 0);
            } else if (strcmp(name, "Actor1_locator") == 0) {
                capture_actor_from_locator(entity, 1);
            }
        }
    }
    return r;
}

/* Background Initialization Thread */
static void* init_thread(void* arg) {
    void* handle = NULL;
    for (int retry = 0; retry < 50; retry++) {
        handle = dlopen("libil2cpp.so", RTLD_NOLOAD);
        if (handle) break;
        usleep(100000); // 100ms
    }
    if (!handle) return NULL;

    p_il2cpp_domain_get = (il2cpp_domain_get_fn)dlsym(handle, "il2cpp_domain_get");
    p_il2cpp_domain_assembly_open = (il2cpp_domain_assembly_open_fn)dlsym(handle, "il2cpp_domain_assembly_open");
    p_il2cpp_assembly_get_image = (il2cpp_assembly_get_image_fn)dlsym(handle, "il2cpp_assembly_get_image");
    p_il2cpp_class_from_name = (il2cpp_class_from_name_fn)dlsym(handle, "il2cpp_class_from_name");
    p_il2cpp_class_get_method_from_name = (il2cpp_class_get_method_from_name_fn)dlsym(handle, "il2cpp_class_get_method_from_name");
    p_il2cpp_class_get_field_from_name = (il2cpp_class_get_field_from_name_fn)dlsym(handle, "il2cpp_class_get_field_from_name");
    p_il2cpp_field_get_offset = (il2cpp_field_get_offset_fn)dlsym(handle, "il2cpp_field_get_offset");
    p_il2cpp_string_new = (il2cpp_string_new_fn)dlsym(handle, "il2cpp_string_new");
    p_il2cpp_string_chars = (il2cpp_string_chars_fn)dlsym(handle, "il2cpp_string_chars");
    p_il2cpp_string_length = (il2cpp_string_length_fn)dlsym(handle, "il2cpp_string_length");

    if (!p_il2cpp_domain_get || !p_il2cpp_class_from_name || !p_il2cpp_class_get_method_from_name) {
        return NULL;
    }

    void* domain = NULL;
    for (int retry = 0; retry < 50; retry++) {
        domain = p_il2cpp_domain_get();
        if (domain) break;
        usleep(100000);
    }
    if (!domain) return NULL;

    // 1. Resolve UnityEngine CoreModule APIs
    void* ass_core = p_il2cpp_domain_assembly_open(domain, "UnityEngine.CoreModule.dll");
    if (!ass_core) ass_core = p_il2cpp_domain_assembly_open(domain, "UnityEngine.CoreModule");
    if (ass_core) {
        void* img_core = p_il2cpp_assembly_get_image(ass_core);

        void* klass_go = p_il2cpp_class_from_name(img_core, "UnityEngine", "GameObject");
        if (klass_go) {
            void* m_gt = p_il2cpp_class_get_method_from_name(klass_go, "get_transform", 0);
            if (m_gt) p_go_get_transform = *(fn_get_transform*)m_gt;
        }

        void* klass_tf = p_il2cpp_class_from_name(img_core, "UnityEngine", "Transform");
        if (klass_tf) {
            void* m_gc = p_il2cpp_class_get_method_from_name(klass_tf, "GetChild", 1);
            if (m_gc) p_tf_GetChild = *(fn_get_child*)m_gc;
            void* m_gcc = p_il2cpp_class_get_method_from_name(klass_tf, "get_childCount", 0);
            if (m_gcc) p_tf_get_childCount = *(fn_get_child_count*)m_gcc;
        }

        void* klass_comp = p_il2cpp_class_from_name(img_core, "UnityEngine", "Component");
        if (klass_comp) {
            void* m_ggo = p_il2cpp_class_get_method_from_name(klass_comp, "get_gameObject", 0);
            if (m_ggo) p_comp_get_gameObject = *(fn_get_game_object*)m_ggo;
        }

        void* klass_obj = p_il2cpp_class_from_name(img_core, "UnityEngine", "Object");
        if (klass_obj) {
            void* m_get = p_il2cpp_class_get_method_from_name(klass_obj, "get_name", 0);
            void* m_set = p_il2cpp_class_get_method_from_name(klass_obj, "set_name", 1);
            if (m_get) p_get_name = *(fn_get_name*)m_get;
            if (m_set) p_set_name = *(fn_set_name*)m_set;
        }
    }

    // 2. Resolve MatineeActorTrack::FixupEntity
    void* ass = p_il2cpp_domain_assembly_open(domain, "Assembly-CSharp-firstpass.dll");
    if (!ass) ass = p_il2cpp_domain_assembly_open(domain, "Assembly-CSharp-firstpass");
    if (ass) {
        void* img = p_il2cpp_assembly_get_image(ass);
        void* klass_track = p_il2cpp_class_from_name(img, "EB.Animation.Matinee", "MatineeActorTrack");
        if (klass_track) {
            if (p_il2cpp_class_get_field_from_name && p_il2cpp_field_get_offset) {
                void* fld_fId = p_il2cpp_class_get_field_from_name(klass_track, "entityFullId");
                if (fld_fId) off_entityFullId = p_il2cpp_field_get_offset(fld_fId);
            }
            void* m_fixup = p_il2cpp_class_get_method_from_name(klass_track, "FixupEntity", 0);
            if (m_fixup) {
                void* fixup_ptr = *(void**)m_fixup;
                inline_hook(fixup_ptr, (void*)hook_FixupEntity, &orig_FixupEntity);
                LOGI("=== Matinee SP3 Native Hook SUCCESSFULLY INSTALLED ===");
            }
        }
    }

    return NULL;
}

__attribute__((constructor)) static void matinee_hook_init(void) {
    pthread_t th;
    pthread_create(&th, NULL, init_thread, NULL);
    pthread_detach(th);
}
#endif /* __aarch64__ */

#endif /* LIB_ANDROID_SIGNAL */

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
