// TFTF native inline-hook lib: logs every key the game reads via EB.Dot.*
// Pure static byte-overwrite inline hook (NOT Frida/Gum) installed BEFORE the
// target funcs are first executed -> libnb's lazy translation picks up the
// patched bytes. Logs to logcat tag "TFTFHOOK".
//
// Build (NDK r26): aarch64-linux-android28-clang -shared -O2 -fPIC -o libtftfhook.so hook.c -llog
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

// forward decls (used by seg_handler below, defined later)
static void flog(const char* fmt, ...);
static uintptr_t g_base;
// SIGSEGV/SIGBUS guard so a bad read inside a hook can't crash the game.
static __thread sigjmp_buf g_jb;
static __thread volatile int g_prot;
static struct sigaction g_oldsegv, g_oldbus;
static void seg_handler(int sig, siginfo_t* si, void* uc){
    if (g_prot) siglongjmp(g_jb, 1);
    // Real game fault (il2cpp null-check reads offset 0 -> SIGSEGV -> Unity converts to
    // managed NullReferenceException). Log the faulting PC's RVA so we can pinpoint which
    // instruction (hence which null field/callee) throws. Then chain to the game handler.
    if (uc && g_base) {
        ucontext_t* u = (ucontext_t*)uc;
        uintptr_t pc = (uintptr_t)u->uc_mcontext.pc;
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

typedef void* (*fn8)(void*,void*,void*,void*,void*,void*,void*,void*);
typedef void* (*fn1)(void*);
typedef void* (*strnew_t)(const char*);   // il2cpp_string_new
typedef void* (*arraynew_t)(void*, size_t); // il2cpp_array_new(elementClass, len)
static uintptr_t g_base;            // libil2cpp base (set in installer)
static strnew_t g_strnew = NULL;    // il2cpp_string_new (dlsym'd in installer)
static arraynew_t g_arraynew = NULL; // il2cpp_array_new (dlsym'd in installer)
// A shared, empty string[] used to fill blueprint.Tags (List<string> @0xB8) when the
// login parser leaves it null. The offline getLoginData JSON carries no `tags` key
// (confirmed via the ==BP== field-reader log: the ctor reads a/c/cl/e/g/... but never
// tags), so Tags is ALWAYS null. Combat's PlayerAttributes.Init does
// `new HashSet<string>(blueprint.Tags)` and throws ArgumentNullException on the null
// collection -> the "unknown error" dialog right as the FTE intro fight loads. An empty
// string[] is a valid IEnumerable<string>; PlayerAttributes.Init only enumerates Tags
// (into the BlueprintTags HashSet it then uses), so a shared empty array is safe and
// makes the fight load. Created lazily on the il2cpp thread inside the blueprint ctor.
static void* g_empty_tags = NULL;   // cached empty string[] (Il2CppArray*)
#define RVA_GET_TEXPATH 0xC16F14    // TFBCGBlueprintBase.get_TexturePath(this)->string
#define RVA_LOADTEX     0xE910DC    // HeroPortrait.LoadTexture(this,path)
#define RVA_TOGGLEVIS   0xE8FF84    // HeroPortrait.ToggleTextureVisibility(this,bEnabled)
#define RVA_SET_ALPHA   0xE85F70    // HeroPortrait.set_alpha(this, float value)  [value in s0]
typedef void (*fnf)(void*, float);  // arm64: this->x0, float->s0

// (rva, tag, jp) accessors. jp=0: key = arg0 (Il2CppString) [EB.Dot.* slow path].
// jp=1: arg0 = JSONPath* struct, key = *(arg0+0x8) (_SinglePath) [EB.Fast.Dot.*].
static struct { uint32_t rva; const char* tag; int jp; fn8 orig; } H[] = {
    { 0x144F534, "S",  0, 0 },   // 0 EB.Dot.String
    { 0x1451998, "O",  0, 0 },   // 1 EB.Dot.Object
    { 0x1460038, "F",  0, 0 },   // 2 EB.Dot.Find
    { 0x145FEA8, "I",  0, 0 },   // 3 EB.Dot.Integer
    { 0x14620B0, "L",  0, 0 },   // 4 EB.Dot.Long
    { 0x1464720, "fI", 1, 0 },   // 5 EB.Fast.Dot.Integer
    { 0x1451344, "fS", 1, 0 },   // 6 EB.Fast.Dot.String
    { 0x146428C, "fO", 1, 0 },   // 7 EB.Fast.Dot.Object
    { 0x1464A24, "fG", 1, 0 },   // 8 EB.Fast.Dot.Single
    { 0x1464800, "fB", 1, 0 },   // 9 EB.Fast.Dot.Bool
    { 0x1463DFC, "fF", 1, 0 },   // 10 EB.Fast.Dot.Find
    { 0x1464E0C, "fSL",1, 0 },   // 11 EB.Fast.Dot.StringList
    { 0xA62348,  "==HERO==",  2, 0 },   // 12 BCGUserHeroBase.ctor
    { 0xC15708,  "==BP==",    2, 0 },   // 13 BCGBlueprintBase.ctor
    { 0xB030A8,  "==BPtf==",  2, 0 },   // 14 TFBCGBlueprintBase.ctor
    { 0xC175E4,  "==CHAR==",  2, 0 },   // 15 BCGCharacterData.ctor
    { 0xB034E4,  "==CHARtf==",2, 0 },   // 16 TFBCGCharacterData.ctor
    { 0x158C87C, "LOADSCR",   3, 0 },   // 17 WindowManager.ShowLoadingScreen(show=a1,reason=a2)
    { 0xC5F728,  ">>HomeFlow.Enter", 2, 0 }, // 18
    { 0x15E2B90, ">>StartBranch",    2, 0 }, // 19 TutorialManager.StartBranch
    { 0x1361518, ">>DownloadAll",    2, 0 }, // 20 ODRManager.DownloadAllCoroutine
    { 0xFC35E4,  "CONNLIST",  5, 0 }, // 21 Hub.SubSystemConnecting -> dump connecting list (stuck subsystems)
    // FIX: these subsystems never finish connecting offline (XlateManager waits on
    // dead-CDN translations; QuestsManager on quest fetch). Run their Connect, then
    // force state=Connected(2) (offset 0x18) so the Hub stops waiting and the frontend loads.
    { 0x1593888, "fixXlate",  6, 0 }, // 22 EB.Sparx.XlateManager.Connect
    { 0xD64370,  "fixQuestL", 6, 0 }, // 23 Legacy.QuestsManager.Connect
    { 0xD6A1B0,  "fixQuestN", 6, 0 }, // 24 Quests.QuestsManager.Connect
    { 0x15E29B8, "STARTTUT",  7, 0 }, // 25 TutorialManager.StartTutorial(this,tutorialId=a1,cb) -> log tutorialId
    { 0x15E2A9C, "ESBRANCH",  7, 0 }, // 26 TutorialManager.EarlyStartBranch(this,tutorialId=a1,...)
    { 0x15E2C84, "COMPTUT",   7, 0 }, // 27 TutorialManager.CompleteTutorial(this,tutorialId=a1,...)
    { 0xC210D4,  "GETENT",    9, 0 }, // 28 BCGHelper.GetEntities(key,modes) -> log returned hero count
    { 0xC1B364,  "GETBP",     0, 0 }, // 29 BCGHelper.GetBlueprint(blueprintId=a0) -> log id
    { 0xC20C70,  "GBPC",      0, 0 }, // 30 BCGHelper.GetBlueprintForCharacter(characterId=a0,rarity) -> log id
    // ---- roster grid diagnostics (2026-07-14 session 2) ----
    { 0xC5ADE4,  "ASF",      23, 0 }, // 31 HeroesScreen.ApplySortingAndFilter -> _entities + IList ret count
    { 0xC5B0E4,  "GSH",      23, 0 }, // 32 HeroesScreen.GetSortedHeroes -> _entities + List ret count
    { 0xC5BC3C,  "OGII",      2, 0 }, // 33 HeroesScreen.OnGridItemInitialized (per-tile marker)
    { 0xE7CDF4,  "DSREADY",   2, 0 }, // 34 Grid.onDynamicScrollReady (fires when scrollview ready)
    { 0xE7D7A0,  "GIDA",      2, 0 }, // 35 Grid.GridItemDataAssignmentCallback (per-item data assign)
    { 0x165AD44, "IPS",      21, 0 }, // 36 DynamicScrollView.CalculateItemsPerScreen -> int ret
    { 0x165AF78, "CSVD",     22, 0 }, // 37 DynamicScrollView.CacheScrollViewDimensions -> Rect+bool
    { 0x165A01C, "MPS",      21, 0 }, // 38 DynamicScrollView.GetMaxPoolSize -> int ret
    { 0x1659CFC, "CREATEITEM",2, 0 }, // 39 DynamicScrollView.CreateItem (per-item instantiate marker)
    { 0x165960C, "DSVINIT",   2, 0 }, // 40 DynamicScrollView.Initialize (marker)
    { 0xE7C4C4,  "GRIDINIT",  2, 0 }, // 41 Grid.Initialize(prefab,...) (marker)
    { 0xC57578,  "SETSCR",    2, 0 }, // 42 HeroesScreen.SetScreenType(type,force,onReady) marker
    { 0xC584CC,  "OWNS",      8, 0 }, // 43 HeroesScreen.userOwnsBot(this,bp) -> bp + bool ret
    // ---- texture-load diagnostics (2026-07-15 session 3): is the portrait a static
    //      path-texture load (that fails offline) or a live camera render? ----
    { 0xE910DC, "TEXPATH",  30, 0 }, // 44 HeroPortrait.LoadTexture(this,path) -> log path string (a1)
    { 0xC21AC4, "==HEROBASE==",2, 0 }, // 45 BCGHeroBase..ctor(IDictionary) marker (brackets its fast-dot keys; drives the login `heroes` map). Author that map -> mHeroBase resolves -> tiles render.
    { 0xE916B4, "TEXDONE",  31, 0 }, // 46 HeroPortrait.OnHeroTextureLoaded -> did it fire? path set? loaded flag
    { 0xC58598, "SHOWGRID",  2, 0 }, // 47 HeroesScreen.ShowGridContainer (marker)
    { 0xC59EE4, "ANIMNEW",   2, 0 }, // 48 HeroesScreen.AnimateNewEntities (grid reveal tween)
    { 0xC59E90, "INTRODONE", 2, 0 }, // 49 HeroesScreen.OnIntroTransitionComplete (marker)
    { 0x1991FD0,"SETPATH",  30, 0 }, // 50 UITextureRef.set_baseTexturePath(this,value) -> log value (a1)
    { 0x1992610,"UITLOAD",   2, 0 }, // 51 UITextureRef.LoadTexture(paths) (marker)
    { 0x14641FC,"FDS2",     34, 0 }, // 52 EB.Fast.Dot.String(name,altPath,data,def) -> log both JSONPath keys
    // ---- BCGHeroBase ctor field readers (2026-07-15 session 4): the 3 fast-dot
    //      variants the ctor uses that weren't otherwise hooked. jp=1 -> log key at
    //      *(arg0+8). Prefixed "HB " when g_inhb (set by slot 45 bracket). Together
    //      with slots 5/6/8 (fI/fS/fG) these cover ALL BCGHeroBase field key reads.
    { 0x1451394,"hbF",       1, 0 }, // 53 fast-dot float w/ default (BCGHeroBase floats @0x24/0x28/0x2c/0x30/0x38)
    { 0x1464770,"hbI",       1, 0 }, // 54 fast-dot int w/ default (BCGHeroBase ints @0x18/0x1c/0x20)
    { 0x1464ccc,"hbL",       1, 0 }, // 55 fast-dot list reader (BCGHeroBase collections @0x58/0x68/0x78/0x80)
    { 0xDAB16C, "FIXFIGHT",  2, 0 }, // 56 PlayerAttributes.Init -> fill both fighters' blueprint.Tags (combat fix)
    { 0x12A9D94,"FIXHS",     2, 0 }, // 57 HashSet<T>..ctor(collection=a1,comparer=a2) -> null collection => empty array
    // ---- combat-input fix (2026-07-16 session 7): make the FTE light attack land ----
    { 0xD35130, "SETACTFIX", 98, 0 }, // 58 PlayerInput.QueuedAction.SetAction — restore the buffered-input window
};
#define NH (int)(sizeof(H)/sizeof(H[0]))

// Set only while inside BCGHeroBase..ctor (slot 45 brackets it). When set, every
// key read is prefixed "HB " so the ctor's exact field keys can be grepped out of
// the flood of general parse keys. Drives authoring login `heroes` BCGHeroBase JSON.
static volatile int g_inhb = 0;
static void log_key(const char* tag, void* s) {
    if (!g_f) return;
    uintptr_t p = (uintptr_t)s;
    if (p < 0x100000 || (p & 7)) return;     // not a plausible 8-aligned heap object (avoids tagged/boxed values like 0x1)
    int32_t len = *(int32_t*)((char*)s + 0x10);
    if (len < 0 || len > 300) return;
    uint16_t* ch = (uint16_t*)((char*)s + 0x14);
    char buf[320]; int i;
    for (i = 0; i < len; i++) buf[i] = (ch[i] < 128) ? (char)ch[i] : '?';
    buf[len] = 0;
    // direct file write (fast path, no logcat per-key)
    if (g_inhb) fputs("HB ", g_f);
    fputs(tag, g_f); fputc(' ', g_f); fputs(buf, g_f); fputc('\n', g_f);
    static int n = 0; if ((++n & 63) == 0) fflush(g_f);   // flush every 64 keys (survive crash)
}
static void flush_keys(void){ if(g_f) fflush(g_f); }

// one thunk per slot. jp=0: key=arg0 (Il2CppString). jp=1: arg0=JSONPath*, key=*(arg0+8).
// jp=2: a ctor marker, just emit the tag (brackets the field reads that follow).
#define MKHOOK(i) \
  void* hook_##i(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){ \
    PROTECT( \
    if (H[i].jp == 2) flog("%s", H[i].tag); \
    else if (H[i].jp == 3) { char b[64]; void* r=a2; int n=0; \
      if((uintptr_t)r>=0x100000 && !((uintptr_t)r&7)){int32_t l=*(int32_t*)((char*)r+0x10); uint16_t*c=(uint16_t*)((char*)r+0x14); if(l>=0&&l<60){for(;n<l;n++)b[n]=(char)c[n];}} b[n]=0; \
      flog("%s show=%ld reason=%s", H[i].tag, (long)a1, b); } \
    else if (H[i].jp == 4) { char nm[40]; nm[0]=0; uintptr_t s=(uintptr_t)a0; uintptr_t cls=0; \
      if(s>=0x100000 && !(s&7)){ cls=*(uintptr_t*)s; if(cls>=0x100000 && !(cls&7)){ char* p=*(char**)(cls+0x10); \
        if((uintptr_t)p>=0x100000){ int k=0; for(;k<38;k++){ char ch=p[k]; if(ch<=0||ch>=127){break;} nm[k]=ch; } nm[k]=0; } } } \
      flog("%s cls=%lx %s = %ld", H[i].tag, cls, nm, (long)a1); } \
    else if (H[i].jp == 7) log_key(H[i].tag, a1); \
    else { void* k = H[i].jp ? (a0 ? *(void**)((char*)a0 + 8) : 0) : a0; log_key(H[i].tag, k); } \
    ); \
    return H[i].orig(a0,a1,a2,a3,a4,a5,a6,a7); }
MKHOOK(0) MKHOOK(1) MKHOOK(2) MKHOOK(3) MKHOOK(4) MKHOOK(5) MKHOOK(6) MKHOOK(7) MKHOOK(8)
MKHOOK(9) MKHOOK(10) MKHOOK(11) MKHOOK(12) MKHOOK(14) MKHOOK(15) MKHOOK(16)
MKHOOK(17) MKHOOK(18) MKHOOK(19) MKHOOK(20)
MKHOOK(25) MKHOOK(26) MKHOOK(27) MKHOOK(29) MKHOOK(30)
// marker slots (jp=2): log tag once per call
MKHOOK(33) MKHOOK(34) MKHOOK(35) MKHOOK(39) MKHOOK(40) MKHOOK(41) MKHOOK(42)
MKHOOK(47) MKHOOK(48) MKHOOK(49) MKHOOK(51)
// slots 53/54/55: BCGHeroBase ctor field readers (jp=1 key logging, HB-prefixed via g_inhb)
MKHOOK(53) MKHOOK(54) MKHOOK(55)
// Read the combat game-clock singleton (same chain OnReceive/OnRelease/HasAction/SetAction use):
//   [g_base+0x2c1a928] -> [.] -> [.+0xb8] -> [.] -> float @0x18
static float game_clock(void){
    if(!g_base) return -1.f;
    uintptr_t p = g_base + 0x2c1a928;
    p = *(uintptr_t*)p; if(p<0x100000||(p&7)) return -1.f;
    p = *(uintptr_t*)p; if(p<0x100000||(p&7)) return -1.f;
    p = *(uintptr_t*)(p+0xb8); if(p<0x100000||(p&7)) return -1.f;
    p = *(uintptr_t*)p; if(p<0x100000||(p&7)) return -1.f;
    return *(float*)(p+0x18);
}
// slot 58 FIX: PlayerInput.QueuedAction.SetAction(this=QueuedAction, action) @0xD35130.
// A tap fully registers offline (OnReleaseAttackInput -> SetAction(Attack) runs), but SetAction
// stores TimeStamp = now + 0: the buffered-input window it would add resolves to 0 because the
// config it reads never loads offline. HasAction() (@0xD351F8) returns TimeStamp > now, so with
// TimeStamp == now it is NEVER true -> PlayerInput.Simulate (@0x1180F88) never consumes the
// queued action -> ExecuteAction never runs -> the queued light attack never lands and the FTE
// "LIGHT ATTACK / TAP RIGHT" counter stays 0/4 (blocking the whole intro fight tutorial).
// Restore the window: after the original SetAction, set QueuedAction.TimeStamp (this+0x14) =
// now + 0.5s so HasAction() stays true for ~0.5s. Simulate then executes the action once and
// ExecuteAction's ClearAction (@0xD35264) resets Action=0/TimeStamp=-1, so it can't re-trigger.
// Applies to every queued action (attack/block/dash/special, both fighters) — that IS the
// intended input-buffer semantics. With this the light attack lands, the counter reaches 4/4,
// and the FTE advances to the medium-attack step.
void* hook_58(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    void* r = H[58].orig(a0,a1,a2,a3,a4,a5,a6,a7);
    PROTECT( uintptr_t q=(uintptr_t)a0; float clk=game_clock();
        if(q>=0x100000 && !(q&7) && clk>=0){ *(float*)(q+0x14) = clk + 0.5f; } );
    return r;
}
// ---- texture-load diagnostics (slots 44,46,48,49,50) ----
// helper: read an Il2CppString at ptr into buf (returns 1 if a plausible string)
static int read_str(void* s, char* buf, int cap){
    buf[0]=0; uintptr_t p=(uintptr_t)s; if(p<0x100000 || (p&7)) return 0;
    int32_t len=*(int32_t*)(p+0x10); if(len<0||len>cap-1) return 0;
    uint16_t* ch=(uint16_t*)(p+0x14); int i; for(i=0;i<len;i++) buf[i]=(ch[i]<128)?(char)ch[i]:'?'; buf[len]=0; return 1;
}
// slot 44 TEXPATH: HeroPortrait.LoadTexture(this=a0, path=a1) ; slot 50 SETPATH: set_baseTexturePath(this=a0,value=a1)
// both: jp=30 -> log the a1 string.
void* hook_44(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    PROTECT( char b[300]; if(read_str(a1,b,sizeof b)) flog("TEXPATH %s", b); else flog("TEXPATH <null/empty>"); );
    return H[44].orig(a0,a1,a2,a3,a4,a5,a6,a7);
}
void* hook_50(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    PROTECT( char b[300]; if(read_str(a1,b,sizeof b)) flog("SETPATH %s", b); else flog("SETPATH <null/empty>"); );
    return H[50].orig(a0,a1,a2,a3,a4,a5,a6,a7);
}
// slot 46 TEXDONE: HeroPortrait.OnHeroTextureLoaded(this=a0) -> the load-complete gate.
// _portraitTexture@0x260 -> UITextureRef._baseTexturePath@0x290 (string) ; _heroTextureLoaded@0x140.
void* hook_46(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    PROTECT( uintptr_t hp=(uintptr_t)a0; int before=-1; char b[300]; b[0]=0;
        if(hp>=0x100000 && !(hp&7)){ before=*(unsigned char*)(hp+0x140);
            uintptr_t tex=*(uintptr_t*)(hp+0x260);
            if(tex>=0x100000 && !(tex&7)){ void* pth=*(void**)(tex+0x290); if(!read_str(pth,b,sizeof b)) strcpy(b,"<empty>"); } }
        flog("TEXDONE fired loadedWas=%d basePath=%s", before, b); );
    void* r = H[46].orig(a0,a1,a2,a3,a4,a5,a6,a7);
    PROTECT( uintptr_t hp=(uintptr_t)a0; int after=-1; if(hp>=0x100000 && !(hp&7)) after=*(unsigned char*)(hp+0x140);
        flog("TEXDONE loadedNow=%d", after); );
    return r;
}
// slot 52 FDS2: EB.Fast.Dot.String(name=a0, altPath=a1, data=a2, def=a3). Log both JSONPath keys
// (_SinglePath at [jsonpath+8]). Only log when data resolves to a blueprint-ish read; log a sample.
static int g_fds2=0;
void* hook_52(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    PROTECT( if((g_fds2++ % 1)==0){ char k1[80]; char k2[80];
        void* p1=(a0?*(void**)((char*)a0+8):0); void* p2=(a1?*(void**)((char*)a1+8):0);
        if(!read_str(p1,k1,sizeof k1)) strcpy(k1,"?"); if(!read_str(p2,k2,sizeof k2)) strcpy(k2,"?");
        flog("FDS2 name='%s' alt='%s'", k1, k2); } );
    return H[52].orig(a0,a1,a2,a3,a4,a5,a6,a7);
}
// slot 45 HEROBASE: BCGHeroBase..ctor(this=a0, IDictionary=a1). Bracket the ctor with
// g_inhb so slots 5/6/8/53/54/55 tag every field key read inside it as "HB <tag> <key>".
// One ==HEROBASE== marker pair per parsed (blueprint,rank) BCGHeroBase entry.
void* hook_45(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    PROTECT( flog("==HEROBASE== begin"); );
    g_inhb = 1;
    void* r = H[45].orig(a0,a1,a2,a3,a4,a5,a6,a7);
    g_inhb = 0;
    PROTECT( flog("==HEROBASE== end"); );
    return r;
}
// Lazily build the shared empty string[] (an Il2CppArray that is a valid IEnumerable<string>).
// Must be called on the il2cpp/managed thread (both callers below are). String class is read
// off a freshly-made il2cpp string (its Il2CppObject.klass).
static void ensure_empty_tags(void){
    if (g_empty_tags || !g_arraynew || !g_strnew) return;
    void* s = g_strnew("");
    if (s) { void* strclass = *(void**)s; if (strclass) g_empty_tags = g_arraynew(strclass, 0); }
}
// Fill BCGBlueprintBase.Tags (List<string> @0xB8) with the shared empty string[] when null.
static void fix_blueprint_tags(void* bpv){
    uintptr_t bp = (uintptr_t)bpv;
    if (!g_empty_tags || bp < 0x100000 || (bp & 7)) return;
    void** tags = (void**)(bp + 0xB8);
    if (*tags == NULL) *tags = g_empty_tags;
}
// slot 13 ==BP==: BCGBlueprintBase..ctor(this=a0, IDictionary=a1). Run the original ctor,
// then fill the never-parsed Tags field so the login-parsed blueprints carry a non-null Tags
// (helps any path that reads Tags directly). The offline JSON has no `tags` key (confirmed via
// the ==BP== field-reader log), so Tags would otherwise stay null forever.
void* hook_13(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    void* r = H[13].orig(a0,a1,a2,a3,a4,a5,a6,a7);
    PROTECT( ensure_empty_tags(); fix_blueprint_tags(a0); );
    return r;
}
// slot 56 FIXFIGHT: PlayerAttributes.Init(this=a0, owner=a1, manager=a2, fighterData=a3,
// opponentFighterData=a4). At dac178 it does `new HashSet<string>(this._blueprint.Tags)` and
// throws ArgumentNullException when Tags is null -> "unknown error" as the fight loads. The
// combat FighterData.Blueprint is NOT always the login-parsed instance the slot-13 hook fixed,
// so patch the ACTUAL fighters' blueprints here: fighterData.Blueprint (fd+0x40) and
// opponentFighterData.Blueprint. This is the fix that lets the FTE intro fight start.
void* hook_56(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    PROTECT(
        ensure_empty_tags();
        uintptr_t fd = (uintptr_t)a3;
        uintptr_t ofd = (uintptr_t)a4;
        void* bp1 = (fd>=0x100000 && !(fd&7)) ? *(void**)(fd + 0x40) : 0;
        void* bp2 = (ofd>=0x100000 && !(ofd&7)) ? *(void**)(ofd + 0x40) : 0;
        void* t1a = (bp1 && ((uintptr_t)bp1>=0x100000) && !((uintptr_t)bp1&7)) ? *(void**)((uintptr_t)bp1+0xB8) : (void*)-1;
        void* t2a = (bp2 && ((uintptr_t)bp2>=0x100000) && !((uintptr_t)bp2&7)) ? *(void**)((uintptr_t)bp2+0xB8) : (void*)-1;
        fix_blueprint_tags(bp1);
        fix_blueprint_tags(bp2);
        void* t1b = (bp1 && ((uintptr_t)bp1>=0x100000) && !((uintptr_t)bp1&7)) ? *(void**)((uintptr_t)bp1+0xB8) : (void*)-1;
        void* t2b = (bp2 && ((uintptr_t)bp2>=0x100000) && !((uintptr_t)bp2&7)) ? *(void**)((uintptr_t)bp2+0xB8) : (void*)-1;
        flog("FIXFIGHT empty=%p bp1=%p tags:%p->%p  bp2=%p tags:%p->%p", g_empty_tags, bp1, t1a, t1b, bp2, t2a, t2b);
    );
    return H[56].orig(a0,a1,a2,a3,a4,a5,a6,a7);
}
// slot 57 FIXHS: HashSet<T>..ctor(this=a0, collection=a1, comparer=a2). The (IEnumerable,
// IEqualityComparer) ctor throws ArgumentNullException when collection is null. Offline, the
// fighters' blueprint.Tags reaches here null (the parser never authors `tags`), killing the
// FTE intro fight. Substitute the shared empty string[] for a null collection -> the HashSet
// is simply built empty (correct, harmless), and the fight loads. Only rewrites null; real
// collections pass through untouched.
static int g_fixhs_logged = 0;
void* hook_57(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    if (a1 == NULL) {
        PROTECT( ensure_empty_tags(); if(!g_fixhs_logged){g_fixhs_logged=1; flog("FIXHS null-collection -> empty (empty=%p)", g_empty_tags);} );
        if (g_empty_tags) a1 = g_empty_tags;
    }
    return H[57].orig(a0,a1,a2,a3,a4,a5,a6,a7);
}
// jp=20 list-count return: sz = ((List)r)._size @ r+0x18
#define MKRET_LIST(i) \
  void* hook_##i(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){ \
    void* r = H[i].orig(a0,a1,a2,a3,a4,a5,a6,a7); \
    PROTECT( int sz=-1; if((uintptr_t)r>=0x100000 && !((uintptr_t)r&7)) sz=*(int*)((char*)r+0x18); \
      flog("%s count=%d", H[i].tag, sz); ); \
    return r; }
// jp=21 int return: value in low 32 bits of x0
#define MKRET_INT(i) \
  void* hook_##i(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){ \
    void* r = H[i].orig(a0,a1,a2,a3,a4,a5,a6,a7); \
    PROTECT( flog("%s ret=%d", H[i].tag, (int)(intptr_t)r); ); \
    return r; }
MKRET_INT(36) MKRET_INT(38)
// jp=23: HeroesScreen ASF/GSH -> read _entities (List @ this+0x158, count @ +0x18) + return list count
#define MKENT(i) \
  void* hook_##i(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){ \
    void* r = H[i].orig(a0,a1,a2,a3,a4,a5,a6,a7); \
    PROTECT( int ent=-1; uintptr_t s=(uintptr_t)a0; \
      if(s>=0x100000 && !(s&7)){ uintptr_t el=*(uintptr_t*)(s+0x158); if(el>=0x100000 && !(el&7)) ent=*(int*)(el+0x18); } \
      int sz=-1; if((uintptr_t)r>=0x100000 && !((uintptr_t)r&7)) sz=*(int*)((char*)r+0x18); \
      flog("%s _entities=%d ret=%d", H[i].tag, ent, sz); ); \
    return r; }
MKENT(31) MKENT(32)
// jp=8: userOwnsBot(this=a0, bp=a1) -> log bp string + bool ret
void* hook_43(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    void* r = H[43].orig(a0,a1,a2,a3,a4,a5,a6,a7);
    PROTECT( char b[64]; b[0]=0; uintptr_t s=(uintptr_t)a1;
        if(s>=0x100000 && !(s&7)){ int32_t l=*(int32_t*)(s+0x10); uint16_t*c=(uint16_t*)(s+0x14);
            if(l>=0&&l<60){for(int i=0;i<l;i++)b[i]=(char)c[i];b[l]=0;} }
        flog("OWNS bp=%s ret=%d", b, (int)(intptr_t)r); );
    return r;
}
// jp=22 CacheScrollViewDimensions: log this->scrollViewArea (Rect @ +0x70) + bool ret + the
// grid's UIPanel state (scrollViewPanel @ this+0x60): mAlpha@0x128, mClipping@0x12c,
// mClipRange@0x130 (Vector4), widgets/drawCalls BetterList counts (@0xb8/@0xc0, size@+0x0c).
void* hook_37(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    void* r = H[37].orig(a0,a1,a2,a3,a4,a5,a6,a7);
    PROTECT( uintptr_t s=(uintptr_t)a0; float x=0; float y=0; float w=0; float h=0;
        if(s>=0x100000 && !(s&7)){ x=*(float*)(s+0x70); y=*(float*)(s+0x74); w=*(float*)(s+0x78); h=*(float*)(s+0x7C); }
        flog("CSVD ret=%d area x=%.1f y=%.1f w=%.1f h=%.1f", (int)(intptr_t)r, x, y, w, h);
        uintptr_t p = (s>=0x100000 && !(s&7)) ? *(uintptr_t*)(s+0x60) : 0;
        if(p>=0x100000 && !(p&7)){
            float al=*(float*)(p+0x128); int clip=*(int*)(p+0x12c);
            float cx=*(float*)(p+0x130); float cy=*(float*)(p+0x134); float cw=*(float*)(p+0x138); float ch=*(float*)(p+0x13c);
            int nw=-1; uintptr_t wl=*(uintptr_t*)(p+0xb8); if(wl>=0x100000 && !(wl&7)) nw=*(int*)(wl+0x18);
            int nd=-1; uintptr_t dl=*(uintptr_t*)(p+0xc0); if(dl>=0x100000 && !(dl&7)) nd=*(int*)(dl+0x18);
            flog("  PANEL alpha=%.3f clipping=%d clipRange=(%.1f,%.1f,%.1f,%.1f) widgets=%d drawCalls=%d", al, clip, cx,cy,cw,ch, nw, nd);
        } );
    return r;
}
static void rdname(uintptr_t sub, char* nm){ nm[0]=0; if(sub<0x100000||(sub&7))return; uintptr_t cls=*(uintptr_t*)sub;
    if(cls<0x100000||(cls&7))return; char* p=*(char**)(cls+0x10); if((uintptr_t)p<0x100000)return;
    int j=0; for(;j<38;j++){char ch=p[j]; if(ch<=0||ch>=127)break; nm[j]=ch;} nm[j]=0; }
// Hub.SubSystemConnecting: dump the connecting list (list@+0x268, _items@+0x10, _size@+0x18,
// item data @ _items+0x20+8*k). Subsystems still connecting (stuck) remain here.
void* hook_21(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    static int n=0;
    if((n++ % 120)==0 && g_f) PROTECT({
        uintptr_t hub=(uintptr_t)a0;
        if(hub>=0x100000){ uintptr_t list=*(uintptr_t*)(hub+0x268);
            if(list>=0x100000){ int size=*(int*)(list+0x18); uintptr_t items=*(uintptr_t*)(list+0x10);
                flog("== CONNECTING size=%d ==", size);
                if(items>=0x100000 && size>0 && size<80) for(int k=0;k<size;k++){
                    uintptr_t sub=*(uintptr_t*)(items+0x20+k*8);
                    if(sub>=0x100000){ int st=*(int*)(sub+0x18); char nm[40]; rdname(sub,nm); flog("  STUCK %s st=%d", nm, st); }
                } } }
    });
    return H[21].orig(a0,a1,a2,a3,a4,a5,a6,a7);
}
// jp=6 FIX: run the original Connect, then force this subsystem to Connected(2).
static int g_fixed_x=0,g_fixed_ql=0,g_fixed_qn=0;
#define MKFIX(i,flag) \
  void* hook_##i(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){ \
    void* r = H[i].orig(a0,a1,a2,a3,a4,a5,a6,a7); \
    PROTECT( if((uintptr_t)a0>=0x100000 && !((uintptr_t)a0&7)){ *(int*)((char*)a0+0x18)=2; if(!flag){flag=1; flog("%s -> forced Connected", H[i].tag);} } ); \
    return r; }
MKFIX(22,g_fixed_x) MKFIX(23,g_fixed_ql) MKFIX(24,g_fixed_qn)
// GetEntities(key=a0, modes=a1) -> List. Log the entity-type key + returned list _size@0x18.
void* hook_28(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    void* r = H[28].orig(a0,a1,a2,a3,a4,a5,a6,a7);
    PROTECT( char k[40]; k[0]=0; uintptr_t s=(uintptr_t)a0;
        if(s>=0x100000 && !(s&7)){ int32_t l=*(int32_t*)(s+0x10); uint16_t*c=(uint16_t*)(s+0x14);
            if(l>=0&&l<38){for(int i=0;i<l;i++)k[i]=(char)c[i];k[l]=0;} }
        int sz=-1; if((uintptr_t)r>=0x100000 && !((uintptr_t)r&7)) sz=*(int*)((char*)r+0x18);
        flog("GETENT key=%s count=%d", k, sz); );
    return r;
}
static void* handlers[] = { hook_0,hook_1,hook_2,hook_3,hook_4,hook_5,hook_6,hook_7,hook_8,
    hook_9,hook_10,hook_11,hook_12,hook_13,hook_14,hook_15,hook_16,hook_17,hook_18,hook_19,hook_20,hook_21,
    hook_22,hook_23,hook_24,hook_25,hook_26,hook_27,hook_28,hook_29,hook_30,
    hook_31,hook_32,hook_33,hook_34,hook_35,hook_36,hook_37,hook_38,hook_39,hook_40,hook_41,hook_42,hook_43,
    hook_44,hook_45,hook_46,hook_47,hook_48,hook_49,hook_50,hook_51,hook_52,
    hook_53,hook_54,hook_55,hook_56,hook_57,hook_58 };

static void write_jump(uint8_t* dst, void* target){
    uint32_t* p = (uint32_t*)dst;
    p[0] = 0x58000051;          // ldr x17, #8
    p[1] = 0xD61F0220;          // br  x17
    *(uint64_t*)(dst + 8) = (uint64_t)target;
}

// Relocate up to 4 prologue instrs from src(orig) into a trampoline tr, fixing
// PC-relative forms (b/bl/b.cond/cbz/cbnz/tbz/tbnz/adr/adrp/ldr-literal). Returns
// trampoline length in bytes. Conditional/compare branches that leave the patched
// region are converted to "<cond> over an absolute jump".
static int relocate(uint8_t* tr, uint8_t* src, int ninstr){
    int o = 0; // output byte cursor
    for (int i = 0; i < ninstr; i++){
        uint32_t in = *(uint32_t*)(src + i*4);
        uint64_t pc = (uint64_t)(src + i*4);
        uint32_t op = in >> 24;
        if ((in & 0x7C000000) == 0x14000000){ // B / BL (imm26)
            int64_t off = (int64_t)(in << 6) >> 4; uint64_t tgt = pc + off;
            uint32_t link = in & 0x80000000;
            // emit: ldr x16,#8 ; (br|blr) x16 ; .quad tgt
            *(uint32_t*)(tr+o)=0x58000050; o+=4;
            *(uint32_t*)(tr+o)= link?0xD63F0200:0xD61F0200; o+=4;
            *(uint64_t*)(tr+o)=tgt; o+=8;
        } else if ((in & 0xFF000010) == 0x54000000){ // B.cond (imm19)
            int64_t off = (int64_t)((in>>5)&0x7FFFF); off=(off<<45)>>43; uint64_t tgt=pc+off;
            uint32_t cond = in & 0xF;
            // b.<inv> +0x14 ; ldr x16,#8 ; br x16 ; .quad tgt
            *(uint32_t*)(tr+o)=0x54000000 | (0x14>>2<<5) | (cond^1); o+=4;
            *(uint32_t*)(tr+o)=0x58000050; o+=4; *(uint32_t*)(tr+o)=0xD61F0200; o+=4; *(uint64_t*)(tr+o)=tgt; o+=8;
        } else if ((in & 0x7E000000) == 0x34000000){ // CBZ/CBNZ (imm19)
            int64_t off=(int64_t)((in>>5)&0x7FFFF); off=(off<<45)>>43; uint64_t tgt=pc+off;
            uint32_t inv = in ^ 0x01000000;            // flip Z/NZ
            // <cbz->cbnz> Rt, +0x14 ; ldr x16,#8 ; br x16 ; .quad tgt
            *(uint32_t*)(tr+o)=(inv & 0xFF00001F) | (0x14>>2<<5); o+=4;
            *(uint32_t*)(tr+o)=0x58000050; o+=4; *(uint32_t*)(tr+o)=0xD61F0200; o+=4; *(uint64_t*)(tr+o)=tgt; o+=8;
        } else if ((in & 0x7E000000) == 0x36000000){ // TBZ/TBNZ (imm14)
            int64_t off=(int64_t)((in>>5)&0x3FFF); off=(off<<50)>>48; uint64_t tgt=pc+off;
            uint32_t inv = in ^ 0x01000000;
            *(uint32_t*)(tr+o)=(inv & 0xFFF8001F) | (0x14>>2<<5); o+=4;
            *(uint32_t*)(tr+o)=0x58000050; o+=4; *(uint32_t*)(tr+o)=0xD61F0200; o+=4; *(uint64_t*)(tr+o)=tgt; o+=8;
        } else if ((in & 0x9F000000) == 0x10000000){ // ADR (imm)
            uint32_t rd=in&0x1F; int64_t imm=(((in>>5)&0x7FFFF)<<2)|((in>>29)&3); imm=(imm<<43)>>43;
            uint64_t tgt=pc+imm;
            // ldr Rd,#8 ; b #0xc ; .quad tgt
            *(uint32_t*)(tr+o)=0x58000040|rd; o+=4; *(uint32_t*)(tr+o)=0x14000003; o+=4; *(uint64_t*)(tr+o)=tgt; o+=8;
        } else if ((in & 0x9F000000) == 0x90000000){ // ADRP
            uint32_t rd=in&0x1F; int64_t imm=(((in>>5)&0x7FFFF)<<2)|((in>>29)&3); imm=(imm<<43)>>43; imm<<=12;
            uint64_t tgt=(pc & ~0xFFFULL)+imm;
            *(uint32_t*)(tr+o)=0x58000040|rd; o+=4; *(uint32_t*)(tr+o)=0x14000003; o+=4; *(uint64_t*)(tr+o)=tgt; o+=8;
        } else if ((in & 0x3B000000) == 0x18000000){ // LDR (literal)
            uint32_t rt=in&0x1F; int64_t off=(int64_t)((in>>5)&0x7FFFF); off=(off<<45)>>43; uint64_t tgt=pc+off;
            int is64 = (in>>30)&1;
            // ldr Rt,#8 ; b #0xc ; .quad &literal ; then deref: load addr then value
            // simpler: load address into Rt then load [Rt]
            *(uint32_t*)(tr+o)=0x58000040|rt; o+=4; *(uint32_t*)(tr+o)=0x14000003; o+=4; *(uint64_t*)(tr+o)=tgt; o+=8;
            *(uint32_t*)(tr+o)= is64 ? (0xF9400000|rt|(rt<<5)) : (0xB9400000|rt|(rt<<5)); o+=4; // ldr Rt,[Rt]
        } else {
            *(uint32_t*)(tr+o)=in; o+=4; // position-independent: copy verbatim
        }
    }
    return o;
}

extern void* handlers[];
static int inline_hook(void* target, void* handler, fn8* orig_out){
    uint8_t* t = (uint8_t*)target;
    uint32_t first = *(uint32_t*)t;
    uintptr_t pg = (uintptr_t)t & ~0xFFFUL;
    if (mprotect((void*)pg, 0x2000, PROT_READ|PROT_WRITE|PROT_EXEC) != 0) { LOG("mprotect fail %p", t); return -1; }
    uint8_t* tr = (uint8_t*)mmap(NULL, 256, PROT_READ|PROT_WRITE|PROT_EXEC, MAP_PRIVATE|MAP_ANONYMOUS, -1, 0);
    if (tr == MAP_FAILED) { LOG("mmap fail"); return -1; }
    int trlen = relocate(tr, t, 4);     // relocate 4 prologue instrs (PC-relative fixed up)
    write_jump(tr + trlen, t + 16);     // jump back to target+16
    __builtin___clear_cache((char*)tr, (char*)tr + trlen + 16);
    *orig_out = (fn8)tr;
    write_jump(t, handler);         // patch target -> handler
    __builtin___clear_cache((char*)t, (char*)t + 16);
    LOG("hooked %p (first was %08x) tramp=%p", t, first, tr);
    return 0;
}

static int find_cb(struct dl_phdr_info* info, size_t sz, void* data){
    if (info->dlpi_name && strstr(info->dlpi_name, "libil2cpp.so")) { g_base = (uintptr_t)info->dlpi_addr; return 1; }
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
    for (int i = 0; i < NH; i++)
        inline_hook((void*)(g_base + H[i].rva), handlers[i], &H[i].orig);
    LOG("install done (%d hooks)", NH);
    return NULL;
}

__attribute__((constructor))
static void init(void){
    struct sigaction sa; memset(&sa, 0, sizeof sa);
    sa.sa_sigaction = seg_handler; sa.sa_flags = SA_SIGINFO | SA_ONSTACK;
    sigemptyset(&sa.sa_mask);
    sigaction(SIGSEGV, &sa, &g_oldsegv);
    sigaction(SIGBUS,  &sa, &g_oldbus);
    LOG("TFTFHOOK loaded (segv-guarded)");
    pthread_t th; pthread_create(&th, NULL, installer, NULL);
}
