// dothook.c, LD_PRELOAD inline-hook of EB.Fast.Dot.* / EB.Dot.* to dump every JSON
// key the game reads at runtime. Plain byte-patch inline hook (no Gum) installed BEFORE
// login, so libnb (LDPlayer ARM->x86 JIT) translates the patched code lazily on first call.
// Keys (deduped) -> logcat tag DOTHOOK. Build: aarch64 clang -shared -fPIC -O2 -llog.
#define _GNU_SOURCE
#include <android/log.h>
#include <pthread.h>
#include <unistd.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <stdlib.h>
#include <sys/mman.h>

#define LOG(...) __android_log_print(ANDROID_LOG_INFO,"DOTHOOK",__VA_ARGS__)

// RVA == file offset for libil2cpp.so; in-memory addr = load_base + RVA.
#define RVA_FString 0x1451344   // EB.Fast.Dot.String  (key = &struct, str at struct[0..2])
#define RVA_FInt    0x1464720   // EB.Fast.Dot.Integer
#define RVA_FObj    0x146428C   // EB.Fast.Dot.Object
#define RVA_FObj2   0x1464318   // EB.Fast.Dot.Object (overload 2)
#define RVA_DString 0x144F534   // EB.Dot.String       (key = Il2CppString* at arg0)
#define RVA_DObj    0x1460038   // EB.Dot.Object
#define RVA_HeroCtor 0xA62348   // BCGUserHeroBase.ctor(IDictionary) marker
#define RVA_HandleUpd 0x1695CC0 // HandleUserDataUpdates marker
#define RVA_CharCtor 0xB034E4   // TFBCGCharacterData.ctor marker
#define RVA_BpCtor   0xB030A8   // TFBCGBlueprintBase.ctor marker

typedef void* (*fn8)(void*,void*,void*,void*,void*,void*,void*,void*);
static fn8 o_FString,o_FInt,o_FObj,o_FObj2,o_DString,o_DObj,o_HeroCtor,o_HandleUpd,o_CharCtor,o_BpCtor;

static uint32_t g_seen[32768];
static int seen_add(uint32_t h){
  if(!h) h=1; uint32_t i=h&32767;
  for(int n=0;n<48;n++){ if(g_seen[i]==h) return 1; if(!g_seen[i]){ g_seen[i]=h; return 0; } i=(i+1)&32767; }
  return 1;
}
static uint32_t fnv(const char*s){ uint32_t h=2166136261u; while(*s) h=(h^(uint8_t)*s++)*16777619u; return h; }

static FILE* g_f=0;
static void filelog(const char* tag,const char* key){
  if(!g_f) g_f=fopen("/data/data/com.kabam.bigrobot/files/dotkeys.txt","a");
  if(g_f){ fprintf(g_f,"%s %s\n",tag,key); fflush(g_f); }
}

static inline int okptr(void* p){ uintptr_t a=(uintptr_t)p; return a>=0x10000 && a<0x800000000000ULL && (a&3)==0; }
// Il2CppString -> ascii buf; -1 if not a plausible ascii key
static int readstr(void* sp,char* buf,int cap){
  if(!okptr(sp)) return -1;
  int32_t len=*(int32_t*)((char*)sp+0x10);
  if(len<1||len>64) return -1;
  uint16_t* ch=(uint16_t*)((char*)sp+0x14);
  int i; for(i=0;i<len&&i<cap-1;i++){ uint16_t c=ch[i]; if(c<32||c>126) return -1; buf[i]=(char)c; }
  buf[i]=0; return i;
}
// Fast.Dot key = JSONPath struct {_Elements[] @0, _SinglePath(string) @0x8, _Count @0x10}.
static void logfast(const char* tag,void* ks){
  if(!okptr(ks)) return; char buf[210];
  void** s=(void**)ks;
  if(readstr(s[1],buf,sizeof buf)>0){ if(!seen_add(fnv(buf))){ LOG("%s %s",tag,buf); filelog(tag,buf);} return; }
  // _SinglePath null -> multi-element path: _Elements (Il2CppArray) first element @ array+0x20
  if(okptr(s[0]) && readstr(((void**)((char*)s[0]+0x20))[0],buf,sizeof buf)>0){
    if(!seen_add(fnv(buf))){ LOG("%s.E %s",tag,buf); filelog(tag,buf);} }
}
void* h_HeroCtor(void*a,void*b,void*c,void*d,void*e,void*f,void*g,void*h){
  static int once=0; if(!once){once=1; LOG("MARK HeroCtor CALLED"); filelog("MARK","HeroCtor");} return o_HeroCtor(a,b,c,d,e,f,g,h); }
void* h_HandleUpd(void*a,void*b,void*c,void*d,void*e,void*f,void*g,void*h){
  static int once=0; if(!once){once=1; LOG("MARK HandleUpd CALLED"); filelog("MARK","HandleUpd");} return o_HandleUpd(a,b,c,d,e,f,g,h); }
void* h_CharCtor(void*a,void*b,void*c,void*d,void*e,void*f,void*g,void*h){
  static int once=0; if(!once){once=1; LOG("MARK CharCtor CALLED"); filelog("MARK","CharCtor");} return o_CharCtor(a,b,c,d,e,f,g,h); }
void* h_BpCtor(void*a,void*b,void*c,void*d,void*e,void*f,void*g,void*h){
  static int once=0; if(!once){once=1; LOG("MARK BpCtor CALLED"); filelog("MARK","BpCtor");} return o_BpCtor(a,b,c,d,e,f,g,h); }
static void logdot(const char* tag,void* str){
  char buf[210]; if(readstr(str,buf,sizeof buf)>0){ if(!seen_add(fnv(buf))){ LOG("%s %s",tag,buf); filelog(tag,buf); } }
}

void* h_FString(void*a,void*b,void*c,void*d,void*e,void*f,void*g,void*h){ logfast("FStr",a); return o_FString(a,b,c,d,e,f,g,h); }
void* h_FInt   (void*a,void*b,void*c,void*d,void*e,void*f,void*g,void*h){ logfast("FInt",a); return o_FInt(a,b,c,d,e,f,g,h); }
void* h_FObj   (void*a,void*b,void*c,void*d,void*e,void*f,void*g,void*h){ logfast("FObj",a); return o_FObj(a,b,c,d,e,f,g,h); }
void* h_FObj2  (void*a,void*b,void*c,void*d,void*e,void*f,void*g,void*h){ logfast("FObj",a); return o_FObj2(a,b,c,d,e,f,g,h); }
void* h_DString(void*a,void*b,void*c,void*d,void*e,void*f,void*g,void*h){ logdot("DStr",a); return o_DString(a,b,c,d,e,f,g,h); }
void* h_DObj   (void*a,void*b,void*c,void*d,void*e,void*f,void*g,void*h){ logdot("DObj",a); return o_DObj(a,b,c,d,e,f,g,h); }

// inline hook: first 4 instrs of every target are pure frame-setup (pos-independent),
// so copy 16 bytes to a trampoline + jump back to target+16; patch target with abs jump.
static int hook(uintptr_t t,void* repl,void** orig){
  uint8_t* tr=(uint8_t*)mmap(0,64,PROT_READ|PROT_WRITE|PROT_EXEC,MAP_PRIVATE|MAP_ANONYMOUS,-1,0);
  if(tr==MAP_FAILED) return 0;
  memcpy(tr,(void*)t,16);
  uint32_t* j=(uint32_t*)(tr+16); j[0]=0x58000051; j[1]=0xD61F0220; *(uint64_t*)(j+2)=t+16; // ldr x17,#8;br x17;.quad t+16
  __builtin___clear_cache((char*)tr,(char*)tr+40);
  uintptr_t pg=t&~0xFFFUL;
  if(mprotect((void*)pg,0x2000,PROT_READ|PROT_WRITE|PROT_EXEC)!=0){ LOG("mprotect fail %p",(void*)t); return 0; }
  uint32_t* d=(uint32_t*)t; d[0]=0x58000051; d[1]=0xD61F0220; *(uint64_t*)(d+2)=(uint64_t)repl;
  __builtin___clear_cache((char*)t,(char*)t+16);
  mprotect((void*)pg,0x2000,PROT_READ|PROT_EXEC);
  *orig=tr; return 1;
}
static uintptr_t base_of(const char* nm){
  FILE* fp=fopen("/proc/self/maps","r"); if(!fp) return 0;
  char ln[700]; uintptr_t b=0;
  while(fgets(ln,sizeof ln,fp)) if(strstr(ln,nm)){ uintptr_t lo=strtoul(ln,0,16); if(!b||lo<b) b=lo; }
  fclose(fp); return b;
}
static void* worker(void* _){
  uintptr_t base=0;
  for(int i=0;i<4000;i++){ base=base_of("libil2cpp.so"); if(base) break; usleep(50000); }
  if(!base){ LOG("libil2cpp NOT FOUND"); return 0; }
  usleep(800000);
  LOG("libil2cpp base=%p", (void*)base);
  LOG("FString[0]=%08x (want d10103ff)", *(uint32_t*)(base+RVA_FString));
  hook(base+RVA_FString,(void*)h_FString,(void**)&o_FString);
  hook(base+RVA_FInt,   (void*)h_FInt,   (void**)&o_FInt);
  hook(base+RVA_FObj,   (void*)h_FObj,   (void**)&o_FObj);
  hook(base+RVA_FObj2,  (void*)h_FObj2,  (void**)&o_FObj2);
  hook(base+RVA_DString,(void*)h_DString,(void**)&o_DString);
  hook(base+RVA_DObj,   (void*)h_DObj,   (void**)&o_DObj);
  hook(base+RVA_HeroCtor,(void*)h_HeroCtor,(void**)&o_HeroCtor);
  hook(base+RVA_HandleUpd,(void*)h_HandleUpd,(void**)&o_HandleUpd);
  hook(base+RVA_CharCtor,(void*)h_CharCtor,(void**)&o_CharCtor);
  hook(base+RVA_BpCtor,(void*)h_BpCtor,(void**)&o_BpCtor);
  LOG("HOOKS INSTALLED");
  return 0;
}
__attribute__((constructor)) static void init(){ LOG("dothook ctor pid=%d",getpid()); pthread_t th; pthread_create(&th,0,worker,0); }
