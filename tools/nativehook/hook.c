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
#include <stdint.h>
#include <stddef.h>
#include <string.h>
#include <stdio.h>
#include <stdlib.h>
#include <unistd.h>
#include <sys/mman.h>
#include <pthread.h>
#include <link.h>

// SIGSEGV/SIGBUS guard so a bad read inside a hook can't crash the game.
static __thread sigjmp_buf g_jb;
static __thread volatile int g_prot;
static struct sigaction g_oldsegv, g_oldbus;
static void seg_handler(int sig, siginfo_t* si, void* uc){
    if (g_prot) siglongjmp(g_jb, 1);
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
};
#define NH (int)(sizeof(H)/sizeof(H[0]))

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
MKHOOK(9) MKHOOK(10) MKHOOK(11) MKHOOK(12) MKHOOK(13) MKHOOK(14) MKHOOK(15) MKHOOK(16)
MKHOOK(17) MKHOOK(18) MKHOOK(19) MKHOOK(20)
MKHOOK(25) MKHOOK(26) MKHOOK(27) MKHOOK(29) MKHOOK(30)
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
    hook_22,hook_23,hook_24,hook_25,hook_26,hook_27,hook_28,hook_29,hook_30 };

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

static uintptr_t g_base = 0;
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
