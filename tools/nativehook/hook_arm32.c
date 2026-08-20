// TFTF native inline-hook lib, armeabi-v7a (32-bit ARM) sibling of hook.c.
//
// hook.c is the arm64 hook: it carries both the behaviour fixes AND the EB.Dot.*
// key-logging instrumentation that the data-authoring loop is built on. This file
// carries ONLY the behaviour fixes, because the authoring loop runs on arm64 (the
// two builds parse identical JSON, so keys discovered there apply here unchanged).
// The 32-bit build is for playing on a 32-bit device, not for authoring.
//
// Build (NDK r26): armv7a-linux-androideabi21-clang -shared -O2 -fPIC -Wall -Wextra \
//                    -Wl,-soname,libdothook.so -o libdothook-armeabi-v7a.so \
//                    hook_arm32.c inapk_server.c -llog
// Keep the soname as libdothook.so: libil2cpp.so has that name in its DT_NEEDED entry.
//
// Two things differ from the arm64 hook, and both bite silently if forgotten:
//
//   1. ADDRESSES. Every RVA below is the armv7 address of the same method, taken
//      from `patches/abi_map.lbl method <arm64 rva>`. Do not copy an address from
//      hook.c. The arm64 comment for each fix explains WHY it exists; that
//      reasoning is unchanged, so it is not repeated here -- read hook.c for it.
//
//   2. FIELD OFFSETS. A managed object's header is 8 bytes here instead of 16, and
//      every reference field is 4 bytes instead of 8, so every offset shifts.
//      They come from `patches/abi_map.lbl fields <Type>`; the arm64 value is kept
//      in a comment next to each one so the pair can be re-checked.
//
// PORT STATUS vs hook.c (arm64) -- READ BEFORE PORTING ANYTHING:
//
//   3. SETACTFIX. hook_11 carries the maxQueuedActionTime fallback change: keep
//      the window computed by the game when it exists, and use
//      SETACT_FALLBACK_WINDOW only when it does not. The real source of that
//      window is bcg-combat.maxQueuedActionTime, authored in Server/gamedata.lbl;
//      keep SETACT_FALLBACK_WINDOW in step with it. This armv7 source change has
//      not been compiled or run on a 32-bit device. It is verified on arm64 only.
//
//   4. TSHIDE. The arm64 squad-screen-occlusion workaround in hook.c slots
//      122-132 is deliberately not ported. It hides the base buildings that
//      bleed into the pre-battle squad screen and restores the ones it hid on
//      exit. Porting needs 11 armv7 RVAs and armv7 field offsets that have not
//      been harvested, and no 32-bit device or AVD was available to verify them.
//      The arm64 BOTS-roster counterpart, RSHIDE slots 133 RSENTER, 134 RSEXIT,
//      and 144 RSHOME, is likewise deliberately deferred: its armv7 lifecycle
//      RVAs and live device verification have not been collected. A 32-bit build
//      therefore still shows base buildings bleeding into both the pre-battle
//      squad screen and the BOTS roster/detail pages.
//
//      A porter must translate every arm64 RVA with
//      `patches/abi_map.lbl method <arm64 rva>` and every field offset with
//      `patches/abi_map.lbl fields <Type>`, then re-verify live. The firing
//      behaviour below was measured empirically on arm64; do not assume these
//      lifecycle methods fire the same way on armv7, or at the same entry/exit
//      point:
//        122 TSINIT  TeamSelectPresentation.Init @0xF9D614
//        123 TSPLAT  TeamSelectPresentation.SetupPlatform @0xF9E6DC (hide)
//        124 TSINTRO TeamSelectPresentation.OnIntroTransitionEnd @0xF9F9A8
//        125 TSSHOW  BaseBoard.ResumeBoard @0xA6FAF0 (gated restore)
//        126 TSSHOWW BaseBoard.OnWindowEntered @0xA6FB54 (gated restore)
//        127 TSDOWN  TeamSelectPresentation.TearDown @0xF97D04 (never fires)
//        128 TSOUTRO TeamSelectPresentation.OnOutroTransitionBegin @0xF9FFE4
//                    (fires on exit: restore)
//        129 TSOUTBG TeamSelectPresentation.OnOutroBlurredBackgroundUp @0xFA06D0
//                    (never fires; log-only)
//        130 TSBACK  TeamSelectPresentation.OnBackClicked @0xF97B10
//                    (fires on exit: restore)
//        131 TSPODD  TeamSelectPodium.Deactivate @0xF9D200 (never fires; log-only)
//        132 TSPODC  TeamSelectPodium.Cleanup @0xF9CFE8
//                    (fires at entry; log-only)
//
// The library is ARM mode (A32), fixed 4-byte instructions -- NOT Thumb -- so the
// same "overwrite the prologue and relocate it" technique as arm64 works, with an
// A32 relocator. Like the arm64 hook this is a pure byte overwrite installed before
// the targets first execute, so an ARM-translating emulator picks up the patched
// bytes when it lazily translates the block. Frida still does not work there.
#include <android/log.h>
#include <stdarg.h>
#include <setjmp.h>
#include <signal.h>
#include <ucontext.h>
#include <stdint.h>
#include <stddef.h>
#include <string.h>
#include <stdio.h>
#include <stdlib.h>
#include <unistd.h>
#include <sys/mman.h>
#include <pthread.h>
#include <link.h>
#include <dlfcn.h>
#include "inapk_server.h"

static void flog(const char* fmt, ...);
static uintptr_t g_base;

// SIGSEGV/SIGBUS guard so a bad read inside a hook can't crash the game.
static __thread sigjmp_buf g_jb;
static __thread volatile int g_prot;
static struct sigaction g_oldsegv, g_oldbus;
static void seg_handler(int sig, siginfo_t* si, void* uc){
    if (g_prot) siglongjmp(g_jb, 1);
    if (uc && g_base) {
        ucontext_t* u = (ucontext_t*)uc;
        uintptr_t pc = (uintptr_t)u->uc_mcontext.arm_pc;   // arm64: uc_mcontext.pc
        uintptr_t fa = (uintptr_t)(si ? si->si_addr : 0);
        if (pc > g_base && pc - g_base < 0x4000000)
            flog("FAULT sig=%d pc_rva=0x%lx faultaddr=0x%lx", sig, (long)(pc - g_base), (long)fa);
    }
    struct sigaction* o = (sig==SIGBUS)?&g_oldbus:&g_oldsegv;     // chain to game's handler
    if (o->sa_flags & SA_SIGINFO) { if(o->sa_sigaction) o->sa_sigaction(sig,si,uc); }
    else if (o->sa_handler && o->sa_handler!=SIG_DFL && o->sa_handler!=SIG_IGN) o->sa_handler(sig);
    else { signal(sig, SIG_DFL); raise(sig); }
}
#define PROTECT(stmt) do { g_prot=1; if (sigsetjmp(g_jb,1)==0) { stmt } g_prot=0; } while(0)
#define LOG(...) do { __android_log_print(ANDROID_LOG_ERROR, "TFTFHOOK", __VA_ARGS__); flog(__VA_ARGS__); } while(0)

static FILE* g_f = NULL;
static void flog(const char* fmt, ...){
    if (!g_f) g_f = fopen("/data/data/com.kabam.bigrobot/files/dotkeys.log", "a");
    if (!g_f) return;
    va_list ap; va_start(ap, fmt); vfprintf(g_f, fmt, ap); va_end(ap);
    fputc('\n', g_f); fflush(g_f);
}

// A32 passes only r0-r3 in registers; anything past that comes off the stack. Every
// hooked method here reads at most 5 arguments, and a pass-through forwards them in
// exactly the layout the callee expects, so the wide signature stays safe.
typedef void* (*fn8)(void*,void*,void*,void*,void*,void*,void*,void*);
typedef void* (*strnew_t)(const char*);
typedef void* (*arraynew_t)(void*, size_t);
static strnew_t g_strnew = NULL;
static arraynew_t g_arraynew = NULL;
static void* g_empty_tags = NULL;   // shared empty string[] (see hook.c slot 57)

// Managed pointers are 4-byte aligned here, not 8.
#define PLAUSIBLE(p) ((uintptr_t)(p) >= 0x100000 && !((uintptr_t)(p) & 3))

// ---------------------------------------------------------------------------
// Hook table. `rva` is the armv7 address; the arm64 address is kept alongside so
// `patches/abi_map.lbl method <a64>` can re-derive it after any rebuild.
// ---------------------------------------------------------------------------
static struct { uint32_t rva; const char* tag; fn8 orig; } H[] = {
    { 0x132EF40, "fixXlate",    0 },  // 0  a64 0x1593888  EB.Sparx.XlateManager.Connect
    { 0x941F34,  "fixQuestL",   0 },  // 1  a64 0xD64370   Legacy.QuestsManager.Connect
    { 0x9499EC,  "fixQuestN",   0 },  // 2  a64 0xD6A1B0   Quests.QuestsManager.Connect
    { 0x998728,  "FIXFIGHT",    0 },  // 3  a64 0xDAB16C   PlayerAttributes.Init
    { 0xFB4D84,  "FIXHS",       0 },  // 4  a64 0x12A9D94  HashSet<T>..ctor(collection,comparer)
    { 0xCBF108,  "FORCEUNLK",   0 },  // 5  a64 0x103A46C  QuestSummary.get_unlocked
    { 0x7BB650,  "FORCEACT",    0 },  // 6  a64 0xC2ADC8   Act.UpdateProgression
    { 0x86420C,  "FORCELOCK",   0 },  // 7  a64 0xCB2954   QuestSelectPanelBase.get_isLocked
    { 0x8DCD38,  "FORCECHAP",   0 },  // 8  a64 0xD13E78   Chapter.UpdateProgression
    { 0x8DD448,  "FORCECHAPSD", 0 },  // 9  a64 0xD14470   ChapterPanel.SetData
    { 0x12B082C, "FIXWRAPMI",   0 },  // 10 a64 0x152B570  SafeAction.<Wrap>b__0<object>
    { 0x907DD8,  "SETACTFIX",   0 },  // 11 a64 0xD35130   PlayerInput.QueuedAction.SetAction
};
#define NH (int)(sizeof(H)/sizeof(H[0]))

// ---- field offsets (arm64 value in the comment; see patches/abi_map.lbl fields) ----
#define OFF_SUBSYSTEM_STATE   0x0C   // a64 0x18  SubSystem.State
#define OFF_ACT_UNLOCKED      0x18   // a64 0x30  Act.unlocked   (Chapter.unlocked too)
#define OFF_ACT_COMPLETED     0x19   // a64 0x31  Act.completed  (Chapter.completed too)
#define OFF_FIGHTERDATA_BP    0x20   // a64 0x40  FighterData.Blueprint
#define OFF_BLUEPRINT_TAGS    0x68   // a64 0xB8  BCGBlueprintBase.Tags
#define OFF_QUEUEDACTION_TS   0x0C   // a64 0x14  PlayerInput.QueuedAction.TimeStamp (float)

// Lazily build the shared empty string[]. Offset-free: the klass pointer is the
// first word of any managed object in both ABIs.
static void ensure_empty_tags(void){
    if (g_empty_tags || !g_arraynew || !g_strnew) return;
    void* s = g_strnew("");
    if (s) { void* strclass = *(void**)s; if (strclass) g_empty_tags = g_arraynew(strclass, 0); }
}
static void fix_blueprint_tags(void* bpv){
    uintptr_t bp = (uintptr_t)bpv;
    if (!g_empty_tags || !PLAUSIBLE(bp)) return;
    void** tags = (void**)(bp + OFF_BLUEPRINT_TAGS);
    if (*tags == NULL) *tags = g_empty_tags;
}

// Run the original Connect, then force this subsystem to Connected(2). XlateManager
// waits on dead-CDN translations and QuestsManager on a quest fetch, so offline they
// never finish and the Hub sits at "LOGGING IN..." forever.
static int g_fixed[3];
#define MKFIX(i) \
  static void* hook_##i(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){ \
    void* r = H[i].orig(a0,a1,a2,a3,a4,a5,a6,a7); \
    PROTECT( if(PLAUSIBLE(a0)){ *(int*)((char*)a0+OFF_SUBSYSTEM_STATE)=2; \
        if(!g_fixed[i]){g_fixed[i]=1; flog("%s -> forced Connected", H[i].tag);} } ); \
    return r; }
MKFIX(0) MKFIX(1) MKFIX(2)

// PlayerAttributes.Init(this,owner,manager,fighterData=a3,opponentFighterData=a4):
// give both fighters' blueprints a non-null Tags before the HashSet ctor sees them.
static void* hook_3(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    PROTECT(
        ensure_empty_tags();
        void* bp1 = PLAUSIBLE(a3) ? *(void**)((uintptr_t)a3 + OFF_FIGHTERDATA_BP) : 0;
        void* bp2 = PLAUSIBLE(a4) ? *(void**)((uintptr_t)a4 + OFF_FIGHTERDATA_BP) : 0;
        fix_blueprint_tags(bp1);
        fix_blueprint_tags(bp2);
        flog("FIXFIGHT empty=%p bp1=%p bp2=%p", g_empty_tags, bp1, bp2);
    );
    return H[3].orig(a0,a1,a2,a3,a4,a5,a6,a7);
}

// HashSet<T>..ctor(this,collection,comparer): substitute the shared empty string[]
// for a null collection so it builds empty instead of throwing.
// NOTE (32-bit only): arm64 hooks the HashSet<string> body, but this build shares ONE
// gshared body across every reference-type instantiation, so this hook sees all of
// them. It still only rewrites a NULL collection, turning a guaranteed throw into an
// empty set, so the wider reach is harmless.
static int g_fixhs_logged = 0;
static void* hook_4(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    if (a1 == NULL) {
        PROTECT( ensure_empty_tags();
                 if(!g_fixhs_logged){g_fixhs_logged=1; flog("FIXHS null-collection -> empty (empty=%p)", g_empty_tags);} );
        if (g_empty_tags) a1 = g_empty_tags;
    }
    return H[4].orig(a0,a1,a2,a3,a4,a5,a6,a7);
}

// QuestSummary.get_unlocked -> true (offline never receives quest progression).
static void* hook_5(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    static int n = 0;
    if (n < 3) { PROTECT( flog("FORCEUNLK -> true"); ); n++; }
    return (void*)1;
}

// Act.UpdateProgression / Chapter.UpdateProgression: run the original, then force
// unlocked=1 / completed=0 so the STORY act and chapter render playable.
#define MKFORCE(i,what) \
  static void* hook_##i(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){ \
    void* r = H[i].orig(a0,a1,a2,a3,a4,a5,a6,a7); \
    PROTECT( if(PLAUSIBLE(a0)){ static int n=0; \
        unsigned char bu=*(unsigned char*)((uintptr_t)a0+OFF_ACT_UNLOCKED); \
        unsigned char bc=*(unsigned char*)((uintptr_t)a0+OFF_ACT_COMPLETED); \
        *(unsigned char*)((uintptr_t)a0+OFF_ACT_UNLOCKED)  = 1; \
        *(unsigned char*)((uintptr_t)a0+OFF_ACT_COMPLETED) = 0; \
        if(n<4){ flog(what " unlocked %d->1 completed %d->0", bu, bc); n++; } } ); \
    return r; }
MKFORCE(6,"FORCEACT") MKFORCE(8,"FORCECHAP")

// QuestSelectPanelBase.get_isLocked -> false. Replaces the original outright.
static void* hook_7(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    static int n = 0;
    if (n < 4) { PROTECT( flog("FORCELOCK -> false"); ); n++; }
    return (void*)0;
}

// ChapterPanel.SetData(this, chapterIndex=a1, chapterData=a2): force the chapter the
// panel is about to bind unlocked BEFORE the original runs.
static void* hook_9(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    PROTECT( if(PLAUSIBLE(a2)){ static int n=0;
        unsigned char bu=*(unsigned char*)((uintptr_t)a2+OFF_ACT_UNLOCKED);
        *(unsigned char*)((uintptr_t)a2+OFF_ACT_UNLOCKED)  = 1;
        *(unsigned char*)((uintptr_t)a2+OFF_ACT_COMPLETED) = 0;
        if(n<4){ flog("FORCECHAPSD unlocked %d->1", bu); n++; } } );
    return H[9].orig(a0,a1,a2,a3,a4,a5,a6,a7);
}

// SafeAction.<>c__DisplayClass1_0<object>.<Wrap>b__0(this, obj, MethodInfo*): the
// bot-select delegate chain does not thread the gshared MethodInfo through, so it
// arrives null and the RGCTX load faults. Cache the first non-null one and reuse it.
// The hidden MethodInfo is the last parameter in both ABIs, so it is a2 here too --
// it just arrives in r2 instead of x2.
static void* g_wrap_mi = NULL;
static void* hook_10(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    if (a2 != NULL) { if (!g_wrap_mi) { g_wrap_mi = a2; PROTECT( flog("FIXWRAPMI cached mi=%p", a2); ); } }
    else if (g_wrap_mi) { a2 = g_wrap_mi; PROTECT( static int n=0; if(n<3){n++; flog("FIXWRAPMI substituted mi"); } ); }
    else { PROTECT( flog("FIXWRAPMI null mi and none cached -> skipping call"); ); return NULL; }
    return H[10].orig(a0,a1,a2,a3,a4,a5,a6,a7);
}

// The combat game clock. `abi_map.lbl method` cannot help here: the clock is reached
// through a GOT slot, i.e. a DATA address, and the two builds place those differently.
// It was instead re-derived from this binary, which is more robust than translating an
// address anyway -- QueuedAction.HasAction (armv7 0x907EC0) has to read the very clock
// it compares TimeStamp against, so the chain is spelled out in its own code:
//
//     ldr r0,[pc,#0x3c] ; ldr r0,[pc,r0]   -> r0 = *(g_base + 0x2826E00)   deref 1
//     ldr r0,[r0]                                                          deref 2
//     ldr r0,[r0,#0x5c]                                                    deref 3
//     ldr r4,[r0]                                                          deref 4
//     vldr s0,[r4,#0xc]                    -> now
//     vcmpe.f32 s16, s0 ; movwgt r0,#1     -> return TimeStamp > now
//
// The two pc-relative literals resolve to GOT slot 0x2826E00, and SetAction's own copy
// of the chain resolves to the same slot, which is the cross-check. The arm64 build
// reads it as adrp/ldr from 0x2c1a928 and then the identical four derefs with 0xb8/0x18
// where this one uses 0x5c/0xc -- the usual pointer-width halving. Both slots live in
// `.got` in their respective binaries.
#define GOT_GAMECLOCK   0x2826E00   // a64 0x2c1a928
#define OFF_CLOCK_NEXT  0x5C        // a64 0xB8
#define OFF_CLOCK_NOW   0x0C        // a64 0x18
static float game_clock(void){
    if (!g_base) return -1.f;
    uintptr_t p = g_base + GOT_GAMECLOCK;
    p = *(uintptr_t*)p;                  if (!PLAUSIBLE(p)) return -1.f;
    p = *(uintptr_t*)p;                  if (!PLAUSIBLE(p)) return -1.f;
    p = *(uintptr_t*)(p + OFF_CLOCK_NEXT); if (!PLAUSIBLE(p)) return -1.f;
    p = *(uintptr_t*)p;                  if (!PLAUSIBLE(p)) return -1.f;
    return *(float*)(p + OFF_CLOCK_NOW);
}
#define SETACT_FALLBACK_WINDOW 0.2f  // Mirrors bcg-combat maxQueuedActionTime in Server/gamedata.lbl; keep in step.

// PlayerInput.QueuedAction.SetAction(this, action) @0x907DD8. Before maxQueuedActionTime was
// authored, a tap fully registered offline (OnReleaseAttackInput -> SetAction(Attack) ran), but
// SetAction stored TimeStamp = now + 0: the buffered-input window resolved to 0 because its
// config had not loaded. HasAction() (TimeStamp > now) was NEVER true, so Simulate never
// consumed the queued action and the FTE light-attack counter stayed 0/4. The root cause is now
// fixed in server data: bcg-combat authors maxQueuedActionTime = 0.2. This hook remains only as
// a safety net if that config has not arrived when combat starts. It keeps the original's usable
// TimeStamp window and substitutes the matching 0.2s fallback only when the window is missing.
static void* hook_11(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    void* r = H[11].orig(a0,a1,a2,a3,a4,a5,a6,a7);
    PROTECT({
        uintptr_t q = (uintptr_t)a0;
        float clk = game_clock();
        if (PLAUSIBLE(q) && clk >= 0.f) {
            float ts = *(float*)(q + OFF_QUEUEDACTION_TS);
            static int diagnostics = 0;
            if (ts - clk > 0.01f) {
                if (diagnostics < 4) { diagnostics++; flog("SETACTFIX config window=%.3f (kept)", ts - clk); }
            } else {
                *(float*)(q + OFF_QUEUEDACTION_TS) = clk + SETACT_FALLBACK_WINDOW;
                if (diagnostics < 4) { diagnostics++; flog("SETACTFIX no config window, fallback=%.3f", (float)SETACT_FALLBACK_WINDOW); }
            }
        }
    });
    return r;
}

static void* handlers[] = { hook_0,hook_1,hook_2,hook_3,hook_4,hook_5,
                            hook_6,hook_7,hook_8,hook_9,hook_10,hook_11 };

// ---------------------------------------------------------------------------
// A32 inline-hook engine
// ---------------------------------------------------------------------------
// `ldr pc,[pc,#-4]` + literal: 8 bytes, clobbers no register, and works for any
// 32-bit target (A32's branch range is only +-32MB, which libil2cpp exceeds).
#define A32_LDR_PC_LIT 0xE51FF004u

static void write_jump(uint8_t* dst, void* target){
    uint32_t* p = (uint32_t*)dst;
    p[0] = A32_LDR_PC_LIT;
    p[1] = (uint32_t)(uintptr_t)target;
}
#define JUMP_BYTES 8
#define RELOC_INSTRS (JUMP_BYTES / 4)

// Does this instruction read or write PC in a way that would change meaning once the
// instruction is executed from the trampoline instead of its original address?
//
// The register fields sit at different bit positions per encoding class, so this has
// to decode by class -- scanning every nibble for 0xF is wrong and rejects ordinary
// instructions: `push {r4-r8,sb,sl,fp,lr}` (0xE92D4FF0) carries a register LIST whose
// bits 8-11 happen to read 0xF.
static int uses_pc(uint32_t in){
    uint32_t rn = (in >> 16) & 0xF, rd = (in >> 12) & 0xF;
    uint32_t rs = (in >> 8) & 0xF,  rm = in & 0xF;
    // Hint space (NOP/YIELD/WFE/...) sits in the MSR-immediate encoding with the Rd
    // field reading 0xF. It touches nothing and relocates verbatim.
    if ((in & 0x0FF0F000) == 0x0320F000) return 0;
    switch ((in >> 26) & 3) {
    case 0:                                   // data-processing / misc
        if (rn == 15 || rd == 15) return 1;
        if (!(in & 0x02000000)) {             // register operand (not immediate)
            if (rm == 15) return 1;
            if ((in & 0x90) == 0x10 && rs == 15) return 1;   // register-shifted register
        }
        return 0;
    case 1:                                   // load/store single
        if (rn == 15 || rd == 15) return 1;
        if ((in & 0x02000000) && rm == 15) return 1;         // register offset
        return 0;
    case 2:                                   // block transfer (branches handled above)
        if (!(in & 0x02000000)) {             // LDM/STM
            if (rn == 15) return 1;
            if (in & 0x8000) return 1;        // PC in the register list (e.g. pop {..,pc})
        }
        return 0;
    default:                                  // coprocessor / VFP, incl. vldr [pc,#imm]
        return rn == 15;
    }
}

// Relocate the RELOC_INSTRS prologue instructions we are about to overwrite into the
// trampoline, rewriting anything PC-relative. A32 reads PC as (instruction address +
// 8), which is what every `pc` below accounts for.
//
// Returns the trampoline length in bytes, or -1 if an instruction uses PC in a form
// this relocator does not rewrite -- in that case the caller refuses to hook rather
// than silently corrupting the function.
static int relocate(uint8_t* tr, uint8_t* src, int ninstr){
    int o = 0;
    for (int i = 0; i < ninstr; i++){
        uint32_t in = *(uint32_t*)(src + i*4);
        uint32_t pc = (uint32_t)(uintptr_t)(src + i*4) + 8;
        uint32_t cond = in >> 28;

        if ((in & 0x0E000000) == 0x0A000000){          // B / BL (cond, imm24)
            int32_t off = (int32_t)(in << 8) >> 6;     // sign-extend imm24, x4
            uint32_t tgt = pc + off;
            if (in & 0x01000000){                      // BL: needs the link register set
                // ldr ip,[pc,#4] ; blx ip ; b over the literal ; .word tgt
                *(uint32_t*)(tr+o) = (cond<<28) | 0x059FC004; o+=4;   // ldr<c> ip,[pc,#4]
                *(uint32_t*)(tr+o) = (cond<<28) | 0x012FFF3C; o+=4;   // blx<c> ip
                *(uint32_t*)(tr+o) = 0xEA000000;             o+=4;   // b  #+0 (past .word)
                *(uint32_t*)(tr+o) = tgt;                    o+=4;
            } else {
                // ldr<c> pc,[pc,#0] ; b over the literal ; .word tgt
                *(uint32_t*)(tr+o) = (cond<<28) | 0x051FF000; o+=4;   // ldr<c> pc,[pc,#0]
                *(uint32_t*)(tr+o) = 0xEA000000;             o+=4;   // b #+0 (cond false path)
                *(uint32_t*)(tr+o) = tgt;                    o+=4;
            }
        } else if ((in & 0x0F7F0000) == 0x051F0000){   // LDR Rd,[pc,#+/-imm12]
            uint32_t rd  = (in >> 12) & 0xF;
            uint32_t imm = in & 0xFFF;
            uint32_t lit = (in & 0x00800000) ? pc + imm : pc - imm;
            if (rd == 15) return -1;                   // ldr pc,[pc,..] -- leave it alone
            // ldr Rd,[pc,#0] ; b over ; .word lit ; ldr Rd,[Rd]
            *(uint32_t*)(tr+o) = (cond<<28) | 0x059F0000 | (rd<<12); o+=4;
            *(uint32_t*)(tr+o) = 0xEA000000;                         o+=4;
            *(uint32_t*)(tr+o) = lit;                                o+=4;
            *(uint32_t*)(tr+o) = (cond<<28) | 0x05900000 | (rd<<16) | (rd<<12); o+=4;
        } else if ((in & 0x0FEF0FF0) == 0x008F0000 && ((in >> 12) & 0xF) != 15){
            // ADD Rd,pc,Rm, unshifted (the GOT-relative pattern every il2cpp prologue
            // opens with). The mask pins Rn=pc and the shift field to zero but leaves
            // Rd and Rm free.
            uint32_t rd = (in >> 12) & 0xF;
            uint32_t rm = in & 0xF;
            if (rd == 12 || rm == 12) return -1;       // would clobber our scratch
            // ldr ip,[pc,#0] ; b over ; .word pc ; add Rd,ip,Rm
            *(uint32_t*)(tr+o) = 0xE59FC000; o+=4;
            *(uint32_t*)(tr+o) = 0xEA000000; o+=4;
            *(uint32_t*)(tr+o) = pc;         o+=4;
            *(uint32_t*)(tr+o) = (cond<<28) | 0x008C0000 | (rd<<12) | rm; o+=4;
        } else if (uses_pc(in)){
            return -1;                                 // some other PC use: refuse
        } else {
            *(uint32_t*)(tr+o) = in; o+=4;             // position-independent: copy
        }
    }
    return o;
}

static int inline_hook(void* target, void* handler, fn8* orig_out, const char* tag){
    uint8_t* t = (uint8_t*)target;
    uintptr_t pg = (uintptr_t)t & ~0xFFFUL;
    if (mprotect((void*)pg, 0x2000, PROT_READ|PROT_WRITE|PROT_EXEC) != 0) {
        LOG("%s: mprotect fail %p", tag, t); return -1;
    }
    uint8_t* tr = (uint8_t*)mmap(NULL, 256, PROT_READ|PROT_WRITE|PROT_EXEC,
                                 MAP_PRIVATE|MAP_ANONYMOUS, -1, 0);
    if (tr == MAP_FAILED) { LOG("%s: mmap fail", tag); return -1; }
    int trlen = relocate(tr, t, RELOC_INSTRS);
    if (trlen < 0) {
        LOG("%s: prologue at %p uses PC in an unhandled form -- NOT hooked", tag, t);
        munmap(tr, 256);
        return -1;
    }
    write_jump(tr + trlen, t + JUMP_BYTES);
    __builtin___clear_cache((char*)tr, (char*)tr + trlen + JUMP_BYTES);
    *orig_out = (fn8)tr;
    write_jump(t, handler);
    __builtin___clear_cache((char*)t, (char*)t + JUMP_BYTES);
    LOG("%s: hooked %p tramp=%p (%d relocated bytes)", tag, t, tr, trlen);
    return 0;
}

// Overwrite a single A32 instruction in libil2cpp, for fixes that are a branch rewrite
// rather than a function hook. `expect` is the instruction the RE was done against; a
// mismatch means this is not the binary the offset was derived from, so refuse rather
// than corrupt a function -- same policy as relocate() returning -1.
static int poke32(uint32_t rva, uint32_t expect, uint32_t insn, const char* tag){
    uintptr_t a  = g_base + rva;
    uintptr_t pg = a & ~0xFFFUL;
    if (mprotect((void*)pg, 0x2000, PROT_READ|PROT_WRITE|PROT_EXEC) != 0){
        LOG("%s: mprotect fail rva=0x%x", tag, rva); return -1;
    }
    uint32_t was = *(uint32_t*)a;
    if (was != expect){
        LOG("%s: rva=0x%x holds %08x, expected %08x -- NOT poked", tag, rva, was, expect);
        return -1;
    }
    *(uint32_t*)a = insn;
    __builtin___clear_cache((char*)a, (char*)a + 4);
    LOG("%s: poked 0x%x  %08x -> %08x", tag, rva, was, insn);
    return 0;
}

static int find_cb(struct dl_phdr_info* info, size_t sz, void* data){
    if (info->dlpi_name && strstr(info->dlpi_name, "libil2cpp.so")) {
        g_base = (uintptr_t)info->dlpi_addr; return 1;
    }
    return 0;
}

static void* installer(void* arg){
    for (int i = 0; i < 1200; i++) {           // up to 60s
        g_base = 0; dl_iterate_phdr(find_cb, NULL);
        if (g_base) break;
        usleep(50000);
    }
    if (!g_base) { LOG("libil2cpp.so NOT found"); return NULL; }
    LOG("libil2cpp.so base=%p", (void*)g_base);
    g_strnew = (strnew_t)dlsym(RTLD_DEFAULT, "il2cpp_string_new");
    if (!g_strnew) { void* h = dlopen("libil2cpp.so", RTLD_NOLOAD); if (h) g_strnew = (strnew_t)dlsym(h, "il2cpp_string_new"); }
    g_arraynew = (arraynew_t)dlsym(RTLD_DEFAULT, "il2cpp_array_new");
    if (!g_arraynew) { void* h = dlopen("libil2cpp.so", RTLD_NOLOAD); if (h) g_arraynew = (arraynew_t)dlsym(h, "il2cpp_array_new"); }
    LOG("il2cpp_string_new=%p il2cpp_array_new=%p", (void*)g_strnew, (void*)g_arraynew);
    int ok = 0;
    for (int i = 0; i < NH; i++)
        if (inline_hook((void*)(g_base + H[i].rva), handlers[i], &H[i].orig, H[i].tag) == 0) ok++;
    LOG("install done (%d/%d hooks)", ok, NH);
    // FIXSYN: BCGBlueprintBase.get_SynergyBonuses (armv7 0x7A3DC8, a64 0xC17198) throws
    // NullReferenceException on a null _synergyBonuses (field 0x7C here, a64 0xE0), which
    // is always the offline state; see hook.c for why. arm64 fixes this by re-pointing a
    // `cbz` from the throw block to the empty-list return. ARM32 has no throw block to
    // re-point -- it emits the il2cpp null-check as a CALL that only falls through:
    //
    //     0x7A3EA4  ldr r4,[r6,#0x7c]     ; this->_synergyBonuses
    //     0x7A3EA8  cmp r4,#0
    //     0x7A3EAC  bne 0x7A3EB4          ; non-null: run the loop
    //     0x7A3EB0  bl  0x4F1EA4          ; null: throw (noreturn)
    //
    // so 0x7A3EB0 is reached ONLY when the field is null, and nothing else branches to it.
    // Overwriting that one call with a jump to the empty-list return is therefore exactly
    // the arm64 fix. The return is at 0x7A3FB8: `ldr r0,[sp,#4]` (the fresh List<string>
    // built at 0x7A3EA0, before the null-check) followed by the epilogue. Jumping straight
    // there also skips the enumerator's Dispose at 0x7A3FA4, which is correct -- we skip
    // its construction too, and the slot was zeroed at function entry.
    poke32(0x7A3EB0, 0xEBF537FB, 0xEA000040, "FIXSYN");   // bl 0x4F1EA4 -> b 0x7A3FB8
    return NULL;
}

static void inapk_log(const char* fmt, ...){
    char line[512]; va_list ap;
    va_start(ap, fmt); vsnprintf(line, sizeof line, fmt, ap); va_end(ap);
    LOG("%s", line);
}

__attribute__((constructor))
static void init(void){
    struct sigaction sa; memset(&sa, 0, sizeof sa);
    sa.sa_sigaction = seg_handler; sa.sa_flags = SA_SIGINFO | SA_ONSTACK;
    sigemptyset(&sa.sa_mask);
    sigaction(SIGSEGV, &sa, &g_oldsegv);
    sigaction(SIGBUS,  &sa, &g_oldbus);
    LOG("TFTFHOOK (armv7) loaded (segv-guarded)");
    tftf_server_set_logger(inapk_log);
    int inapk_rc = tftf_server_start_from_apk();
    LOG("in-apk server start: %d", inapk_rc);
    pthread_t th; pthread_create(&th, NULL, installer, NULL);
}
