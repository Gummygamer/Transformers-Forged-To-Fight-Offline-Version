/* off_t must be 64-bit on 32-bit ARM: bionic's LP32 struct stat already carries a
   64-bit st_size, so without this the APK offsets below truncate silently.  This
   also redirects pread/mmap to pread64/mmap64.  No effect on 64-bit builds. */
#define _FILE_OFFSET_BITS 64

#define _POSIX_C_SOURCE 200809L
#include "inapk_server.h"

#include <arpa/inet.h>
#include <ctype.h>
#include <errno.h>
#include <fcntl.h>
#include <netinet/in.h>
#include <pthread.h>
#include <stdarg.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>
#include <sys/mman.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/time.h>
#include <sys/types.h>
#include <time.h>
#include <unistd.h>

#define HDR 64u
#define REC 16u
#define MAX_HEAD 32768u
#define MAX_BODY (1024u * 1024u)
#define MAX_CONN 64
#define TEAM_SIZE_MAX 5

typedef struct { uint32_t ko, kl, bo, bl; } Rec;
typedef struct { const unsigned char *p; size_t n; uint32_t count, eo, pc, po, dfo, dfl, port; } Blob;
typedef struct { char qid[64]; int x, y; } Position;
typedef struct { unsigned char *p; size_t n, cap; } Out;
typedef struct { char bid[TEAM_SIZE_MAX][64]; int count; } Team;
typedef struct { const char *token; const unsigned char *p; size_t n; } TemplateArg;

static Blob g_blob;
static tftf_log_fn g_log;
static pthread_mutex_t g_start_lock = PTHREAD_MUTEX_INITIALIZER;
static pthread_mutex_t g_pos_lock = PTHREAD_MUTEX_INITIALIZER;
static pthread_mutex_t g_conn_lock = PTHREAD_MUTEX_INITIALIZER;
static Position g_pos[16];
static char g_saved_team[TEAM_SIZE_MAX][64];
static int g_saved_team_count;
static int g_connections;
static int g_started;

static void logmsg(const char *fmt, ...) {
    va_list ap;
    if (!g_log) return;
    va_start(ap, fmt);
    /* A varargs callback cannot receive a va_list.  Format once, then forward it. */
    char text[512];
    vsnprintf(text, sizeof text, fmt, ap);
    va_end(ap);
    g_log("%s", text);
}
void tftf_server_set_logger(tftf_log_fn fn) { g_log = fn; }

static uint32_t le32(const unsigned char *p) {
    return (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
}
static uint32_t crc32_bytes(const unsigned char *p, size_t n) {
    uint32_t c = 0xffffffffu;
    size_t i; int b;
    for (i = 0; i < n; i++) { c ^= p[i]; for (b = 0; b < 8; b++) c = (c >> 1) ^ (0xedb88320u & (uint32_t)-(int)(c & 1)); }
    return ~c;
}
static int range_ok(size_t off, size_t len, size_t total) { return off <= total && len <= total - off; }
static int value_ok(const Blob *b, uint32_t off, uint32_t len) {
    size_t end, pad, i;
    if ((off & 3u) || off < HDR || !range_ok(off, len, b->n) || (size_t)off + len >= b->n) return 0;
    end = (size_t)off + len;
    if (b->p[end] != 0) return 0;
    pad = (end + 4u) & ~(size_t)3u;
    if (pad > b->n) return 0;
    for (i = end + 1; i < pad; i++) if (b->p[i]) return 0;
    return 1;
}
static Rec rec_at(const Blob *b, uint32_t off, uint32_t i) {
    const unsigned char *p = b->p + (size_t)off + (size_t)i * REC;
    Rec r = { le32(p), le32(p+4), le32(p+8), le32(p+12) }; return r;
}
static int keycmp(const unsigned char *a, size_t an, const char *b, size_t bn) {
    size_t n = an < bn ? an : bn; int c = memcmp(a, b, n);
    if (c) return c;
    return an < bn ? -1 : an > bn;
}
static int blob_validate(const void *data, size_t len, Blob *out) {
    const unsigned char *p = data; Blob b; uint32_t i; Rec prev = {0,0,0,0};
    if (!p || len < HDR || memcmp(p, "TFTFPAY\0", 8) || le32(p+8) != 1 || le32(p+12) != len) return -1;
    if (le32(p+48) || le32(p+52) || le32(p+56) || le32(p+60) || crc32_bytes(p+HDR, len-HDR) != le32(p+44)) return -2;
    memset(&b, 0, sizeof b); b.p=p; b.n=len; b.port=le32(p+16); b.count=le32(p+20); b.eo=le32(p+24); b.pc=le32(p+28); b.po=le32(p+32); b.dfo=le32(p+36); b.dfl=le32(p+40);
    if (!b.port || !range_ok(b.eo, (size_t)b.count*REC, len) || !range_ok(b.po, (size_t)b.pc*REC, len) || (b.eo&3) || (b.po&3) || b.eo < HDR || b.po < HDR || !value_ok(&b,b.dfo,b.dfl)) return -3;
    for (i=0; i<b.count+b.pc; i++) {
        Rec r = i < b.count ? rec_at(&b,b.eo,i) : rec_at(&b,b.po,i-b.count);
        if (!value_ok(&b,r.ko,r.kl) || !value_ok(&b,r.bo,r.bl)) return -4;
        if (i < b.count && i && keycmp(b.p+prev.ko,prev.kl,(const char*)b.p+r.ko,r.kl) >= 0) return -5;
        if (i < b.count) prev=r;
    }
    *out=b; return 0;
}
static const Rec *find_key(const char *key) {
    static __thread Rec got; uint32_t lo=0, hi=g_blob.count; size_t n=strlen(key);
    while (lo < hi) { uint32_t m=lo+(hi-lo)/2; Rec r=rec_at(&g_blob,g_blob.eo,m); int c=keycmp(g_blob.p+r.ko,r.kl,key,n); if (!c) { got=r; return &got; } if(c<0) lo=m+1; else hi=m; }
    return NULL;
}
static const unsigned char *body_for(const Rec *r, size_t *n) { if (!r) return NULL; *n=r->bl; return g_blob.p+r->bo; }
static const unsigned char *lookup(const char *key, size_t *n) { return body_for(find_key(key),n); }
static int out_reserve(Out *o, size_t add) { size_t cap; unsigned char *p; if (add <= o->cap-o->n) return 1; cap=o->cap?o->cap:256; while(cap-o->n<add) { if(cap>MAX_BODY*8) return 0; cap*=2; } p=realloc(o->p,cap); if(!p)return 0; o->p=p;o->cap=cap;return 1; }
static int out_add(Out *o, const void *p, size_t n) { if(!out_reserve(o,n))return 0; memcpy(o->p+o->n,p,n);o->n+=n;return 1; }
static int out_template_args(Out *o, const unsigned char *s, size_t n, const TemplateArg *args, size_t count) {
    size_t i=0;
    while(i<n) { size_t j; int found=0;
        for(j=0;j<count;j++) { size_t l=strlen(args[j].token); if(l&&i+l<=n&&!memcmp(s+i,args[j].token,l)) { if(!out_add(o,args[j].p,args[j].n))return 0;i+=l;found=1;break; } }
        if(!found) { if(!out_add(o,s+i,1))return 0;i++; }
    }
    return 1;
}
static int out_template(Out *o, const unsigned char *s, size_t n, const char *a, const char *av, const char *b, const char *bv) {
    TemplateArg args[2]={{a,(const unsigned char*)av,strlen(av)},{b,(const unsigned char*)bv,strlen(bv)}};
    return out_template_args(o,s,n,args,2);
}
/* The authored dynamic bodies are compact; fakeserver's json.dumps uses its default spaces. */
static const unsigned char *json_default_spaces(const unsigned char *s, size_t n, Out *o, size_t *outn) {
    size_t i; int quoted=0, escaped=0;
    for(i=0;i<n;i++) {
        unsigned char c=s[i];
        if(!out_add(o,&c,1)) return NULL;
        if(quoted) { if(escaped) escaped=0; else if(c=='\\') escaped=1; else if(c=='\"') quoted=0; }
        else if(c=='\"') quoted=1;
        else if(c==':' || c==',') { c=' '; if(!out_add(o,&c,1)) return NULL; }
    }
    *outn=o->n;
    return o->p;
}
static const unsigned char *template_spaced(Out *o, const unsigned char *s, size_t n, const TemplateArg *args, size_t count, size_t *outn) {
    Out compact={0}; const unsigned char *result;
    if(!out_template_args(&compact,s,n,args,count)){free(compact.p);return NULL;}
    result=json_default_spaces(compact.p,compact.n,o,outn);free(compact.p);return result;
}
static int safe_id(const char *s) { size_t i,n=strlen(s); if(!n||n>63)return 0; for(i=0;i<n;i++)if(!isalnum((unsigned char)s[i])&&s[i]!='_'&&s[i]!='.'&&s[i]!=':'&&s[i]!='-')return 0; return 1; }
/* Narrow JSON scanner: quoted scalar values and integer scalars only. */
static int json_string(const char *s, const char *end, const char *want, char *out, size_t cap) {
    const char *p=s; size_t wl=strlen(want);
    while (p < end) { const char *q=strstr(p,"\""); if(!q||q>=end)return 0; q++; if((size_t)(end-q)<wl+1||memcmp(q,want,wl)||q[wl]!='\"'){p=q;continue;} q+=wl+1; while(q<end&&isspace((unsigned char)*q))q++; if(q>=end||*q!=':') {p=q;continue;} q++;while(q<end&&isspace((unsigned char)*q))q++; if(q>=end||*q!='\"')return 0; q++; size_t n=0; while(q<end&&*q!='\"'){if(*q=='\\'||n+1>=cap)return 0;out[n++]=*q++;} if(q>=end)return 0;out[n]=0;return 1; }
    return 0;
}
static int json_int(const char *s, const char *end, const char *want, int def) {
    const char *p=s; size_t wl=strlen(want);
    while(p<end){const char*q=strstr(p,"\"");char *stop; long v;if(!q||q>=end)break;q++;if((size_t)(end-q)<wl+1||memcmp(q,want,wl)||q[wl]!='\"'){p=q;continue;}q+=wl+1;while(q<end&&isspace((unsigned char)*q))q++;if(q>=end||*q!=':'){p=q;continue;}q++;while(q<end&&isspace((unsigned char)*q))q++;errno=0;v=strtol(q,&stop,10);if(stop==q||errno)return def;return (int)v;}return def;
}
static int list_has(const unsigned char *s, size_t n, const char *id) { size_t l=strlen(id), i=0; while(i<n){size_t j=i;while(j<n&&s[j]!='\n')j++;if(j-i==l&&!memcmp(s+i,id,l))return 1;i=j+1;}return 0; }
static int team_from_lines(const unsigned char *s, size_t n, Team *team) {
    size_t i=0;
    team->count=0;
    while(i<n) { size_t j=i,l; while(j<n&&s[j]!='\n')j++;l=j-i;
        if(!l||l>=sizeof team->bid[0]||team->count>=TEAM_SIZE_MAX)return 0;
        memcpy(team->bid[team->count],s+i,l);team->bid[team->count][l]=0;team->count++;i=j+1;
    }
    return team->count>0;
}
static void store_saved_team(const char bids[][64], int count, int invalid) {
    int i;
    pthread_mutex_lock(&g_pos_lock);
    g_saved_team_count=invalid||count<0||count>TEAM_SIZE_MAX?-1:count;
    if(g_saved_team_count>0)for(i=0;i<g_saved_team_count;i++)snprintf(g_saved_team[i],sizeof g_saved_team[i],"%s",bids[i]);
    pthread_mutex_unlock(&g_pos_lock);
}
static int resolve_team(Team *team) {
    const unsigned char *roster,*defaults; size_t rn,dn; int i,valid=1;
    defaults=lookup("@team:default",&dn);roster=lookup("@roster",&rn);
    if(!defaults||!roster||!team_from_lines(defaults,dn,team))return 0;
    pthread_mutex_lock(&g_pos_lock);
    if(g_saved_team_count<=0)valid=0;
    else {
        Team saved; saved.count=g_saved_team_count;
        for(i=0;i<saved.count;i++)snprintf(saved.bid[i],sizeof saved.bid[i],"%s",g_saved_team[i]);
        for(i=0;i<saved.count;i++)if(!safe_id(saved.bid[i])||!list_has(roster,rn,saved.bid[i]))valid=0;
        if(valid)*team=saved;
    }
    pthread_mutex_unlock(&g_pos_lock);
    return 1;
}
static int render_qteam(Out *o, const Team *team) {
    int i; char key[96]; const unsigned char *v; size_t n;
    if(!out_add(o,"{",1))return 0;
    for(i=0;i<team->count;i++) { snprintf(key,sizeof key,"@questmember:%s",team->bid[i]);v=lookup(key,&n);if(!v)return 0;if(i&&!out_add(o,",",1))return 0;if(!out_add(o,v,n))return 0; }
    return out_add(o,"}",1);
}
static int render_ateam(Out *o, const Team *team) {
    int i; char key[96]; const unsigned char *v; size_t n;
    if(!out_add(o,"{",1))return 0;
    for(i=0;i<team->count;i++) { snprintf(key,sizeof key,"@savedteam:hero:%s",team->bid[i]);v=lookup(key,&n);if(!v)return 0;if(i&&!out_add(o,",",1))return 0;if(!out_add(o,"\"",1)||!out_add(o,team->bid[i],strlen(team->bid[i]))||!out_add(o,"\":",2)||!out_add(o,v,n))return 0; }
    return out_add(o,"}",1);
}
static int render_steam(Out *o, const Team *team) {
    int i; char key[96]; const unsigned char *v; size_t n;
    if(!out_add(o,"[",1))return 0;
    for(i=0;i<team->count;i++) { snprintf(key,sizeof key,"@savedteam:hero:%s",team->bid[i]);v=lookup(key,&n);if(!v)return 0;if(i&&!out_add(o,",",1))return 0;if(!out_add(o,v,n))return 0; }
    return out_add(o,"]",1);
}
static const char *json_value(const char *s, const char *end, const char *want) {
    const char *p=s; size_t wl=strlen(want);
    while(p<end) { const char *q=strstr(p,"\""); if(!q||q>=end)return NULL;q++;if((size_t)(end-q)<wl+1||memcmp(q,want,wl)||q[wl]!='\"'){p=q;continue;}q+=wl+1;while(q<end&&isspace((unsigned char)*q))q++;if(q>=end||*q!=':'){p=q;continue;}q++;while(q<end&&isspace((unsigned char)*q))q++;return q; }
    return NULL;
}
static int json_heroes(const char *s, const char *end, char bids[][64], int *count, int *invalid) {
    const char *q=json_value(s,end,"heroes");
    *count=0;*invalid=0;if(!q||q>=end||*q!='[')return 1;q++;
    for(;;) { const char *e; size_t l;while(q<end&&isspace((unsigned char)*q))q++;if(q>=end){*invalid=1;return 0;}if(*q==']')return 1;if(*q!='\"'){*invalid=1;return 0;}q++;e=q;while(e<end&&*e!='\"'){if(*e=='\\'){*invalid=1;return 0;}e++;}if(e>=end){*invalid=1;return 0;}l=(size_t)(e-q);if(*count>=TEAM_SIZE_MAX||l>=sizeof bids[0]){*invalid=1;}else{memcpy(bids[*count],q,l);bids[*count][l]=0;if(!safe_id(bids[*count]))*invalid=1;(*count)++;}q=e+1;while(q<end&&isspace((unsigned char)*q))q++;if(q>=end){*invalid=1;return 0;}if(*q==']')return 1;if(*q!=','){*invalid=1;return 0;}q++; }
}
static void store_quest_team(const char *s, const char *end) {
    char bids[TEAM_SIZE_MAX][64]; int count=0,invalid=0,i,present=0; char value[64];
    for(i=0;i<TEAM_SIZE_MAX;i++) { char key[4];snprintf(key,sizeof key,"tm%d",i);if(json_value(s,end,key)){present=1;if(!json_string(s,end,key,value,sizeof value)){invalid=1;continue;}if(value[0]){if(!safe_id(value))invalid=1;else snprintf(bids[count++],sizeof bids[0],"%s",value);}} }
    if(json_value(s,end,"tm5")){present=1;if(!json_string(s,end,"tm5",value,sizeof value)||value[0])invalid=1;}
    if(present)store_saved_team(bids,count,invalid);
}
static const char *path_last(const char *p) { const char *x=strrchr(p,'/'); return x?x+1:p; }
static int has_suffix(const char *p, const char *s) { size_t a=strlen(p),b=strlen(s);return a>=b&&!memcmp(p+a-b,s,b); }
static const unsigned char *dynamic(const char *method, const char *p, const char *query, const char *body, size_t bn, Out *o, size_t *outn) {
    char key[256], tid[64]="", bid[64]="", mid[64], qid[64]; const unsigned char *v; size_t n; const char *end=body+bn;
    /* This ordering deliberately mirrors fakeserver.H._dynamic. */
    if(has_suffix(p,"/bcg/getUserData")) { Team team; Out saved={0},active={0}; TemplateArg args[2];
        v=lookup("@userdata:template",&n);if(!v||!resolve_team(&team)||!render_steam(&saved,&team)||!render_ateam(&active,&team)){free(saved.p);free(active.p);return NULL;}
        args[0]=(TemplateArg){"%STEAM%",saved.p,saved.n};args[1]=(TemplateArg){"%ATEAM%",active.p,active.n};
        if(!out_template_args(o,v,n,args,2)){free(saved.p);free(active.p);return NULL;}free(saved.p);free(active.p);*outn=o->n;return o->p;
    }
    if(has_suffix(p,"/autorefresh/grouprefresh")) { int mission=0; const char *x=query; while(x&&*x){const char*e=strchr(x,'&');const char*eq=strchr(x,'=');size_t nl;if(!e)e=x+strlen(x);if(eq&&eq<e){nl=(size_t)(eq-x);if(nl>=12&&!memcmp(x,"groups.",7)&&nl>=5&&!memcmp(eq-5,".name",5)&&(size_t)(e-eq-1)==14&&!memcmp(eq+1,"missionsconfig",14))mission=1;}x=*e?e+1:NULL;} return lookup(mission?"@grouprefresh:missionsconfig":"@grouprefresh:",outn); }
    if(has_suffix(p,"/base/active")) { snprintf(key,sizeof key,"%s /base/active",method); v=lookup(key,outn); return v?v:lookup("GET /base/active",outn); }
    if(has_suffix(p,"/tutorial/start-tutorial")||has_suffix(p,"/tutorial/start-branch")||has_suffix(p,"/tutorial/early-start-branch")||has_suffix(p,"/tutorial/complete-tutorial")) {
        if(!json_string(body,end,"tid",tid,sizeof tid))if(!json_string(body,end,"tutorialId",tid,sizeof tid))json_string(body,end,"id",tid,sizeof tid);
        if(!safe_id(tid)) return NULL;
        if(!json_string(body,end,"bid",bid,sizeof bid)) json_string(body,end,"branchId",bid,sizeof bid);
        if(!safe_id(bid)) bid[0]=0;
        v=lookup("@tutorial:blocked",&n);if(v&&list_has(v,n,tid))return lookup("@tutorial:error",outn);
        v=lookup("@tutorial:live",&n); if(v&&list_has(v,n,tid)&&!has_suffix(p,"/tutorial/complete-tutorial")) v=lookup(bid[0]?"@tutorial:started":"@tutorial:started-nobid",&n); else v=lookup(bid[0]?"@tutorial:completed":"@tutorial:completed-nobid",&n);
        if(!v||!out_template(o,v,n,"%TID%",tid,"%BID%",bid)) return NULL;
        *outn=o->n;
        return o->p;
    }
    if(has_suffix(p,"/bcg/getBaseHeroData")) {
        const char *a=strstr(body,"\"heroes\""); const char *arr=a?strchr(a,'['):NULL; const char *q=arr?arr+1:NULL; v=lookup("@herodata:open",&n);if(!v||!out_add(o,v,n))return NULL;
        int first=1; while(q&&q<end){const char *open=strchr(q,'{'),*close;int depth=0;if(!open||open>=end)break;close=open;do{if(*close=='{')depth++;else if(*close=='}')depth--;close++;}while(close<end&&depth);if(depth)break;char hb[64]="", hk[200], sig[32];int rank=json_int(open,close,"rank",1),level=json_int(open,close,"level",1),sl=json_int(open,close,"sig_lvl",0);if(!rank)rank=1;if(!level)level=1;json_string(open,close,"bid",hb,sizeof hb);snprintf(hk,sizeof hk,"@hero:%s:%d:%d",hb,rank,level);v=lookup(hk,&n);if(!v){snprintf(hk,sizeof hk,"@hero:%s:1:1",hb);v=lookup(hk,&n);}if(!v)v=lookup("@hero:*:1:1",&n);if(v){snprintf(sig,sizeof sig,"%d",sl);if(!first&&!out_add(o,",",1))return NULL;if(!out_template(o,v,n,"%SIG%",sig,"", ""))return NULL;first=0;}q=close;}
        v=lookup("@herodata:close",&n);if(!v||!out_add(o,v,n))return NULL; Out compact=*o; o->p=NULL;o->n=o->cap=0; v=json_default_spaces(compact.p,compact.n,o,outn);free(compact.p);return v;
    }
    if(strstr(p,"/quests/quest-detail/")) { snprintf(mid,sizeof mid,"%.63s",path_last(p));snprintf(key,sizeof key,"%s /quests/quest-detail/%s",method,mid);v=lookup(key,&n);if(!v){snprintf(key,sizeof key,"POST /quests/quest-detail/%s",mid);v=lookup(key,&n);}return v?json_default_spaces(v,n,o,outn):NULL; }
    if(strstr(p,"/quests/quest-begin/")) { Team team; Out qteam={0}; TemplateArg args[2];snprintf(qid,sizeof qid,"%.63s",path_last(p)); /* reset even if response is absent */
        int x=0,y=1;store_quest_team(body,end);snprintf(key,sizeof key,"@quest:start:%s",qid);v=lookup(key,&n);if(v)sscanf((const char*)v,"%d %d",&x,&y);pthread_mutex_lock(&g_pos_lock);int slot=-1;for(int i=0;i<16;i++)if(!strcmp(g_pos[i].qid,qid)||!g_pos[i].qid[0]){slot=i;break;}if(slot>=0){snprintf(g_pos[slot].qid,sizeof g_pos[slot].qid,"%s",qid);g_pos[slot].x=x;g_pos[slot].y=y;}pthread_mutex_unlock(&g_pos_lock);snprintf(key,sizeof key,"%s /quests/quest-begin/%s",method,qid);v=lookup(key,&n);if(!v){snprintf(key,sizeof key,"POST /quests/quest-begin/%s",qid);v=lookup(key,&n);}if(!v||!resolve_team(&team)||!render_qteam(&qteam,&team)){free(qteam.p);return NULL;}args[0]=(TemplateArg){"%LEAD%",(const unsigned char*)team.bid[0],strlen(team.bid[0])};args[1]=(TemplateArg){"%QTEAM%",qteam.p,qteam.n};v=template_spaced(o,v,n,args,2,outn);free(qteam.p);return v; }
    if(strstr(p,"/quests/quest-movedir/")) { int dx=1,dy=0,sx=0,sy=1,nx,ny;
        const char *z=strrchr(p,'/'); const char *yseg=z?z+1:""; const char *z2=z?NULL:NULL; if(z){z2=z-1;while(z2>p&&*z2!='/')z2--; if(*z2=='/')z2++;} if(!z||!z2)return NULL; char xs[32], ys[32], seg[96];snprintf(ys,sizeof ys,"%.31s",yseg);snprintf(xs,sizeof xs,"%.*s",(int)(z-z2),z2); const char *z3=z2-2;while(z3>p&&*z3!='/')z3--;if(*z3=='/')z3++;snprintf(seg,sizeof seg,"%.*s",(int)(z2-z3-1),z3);char *dash=strrchr(seg,'-');if(!dash)return NULL;*dash=0;snprintf(qid,sizeof qid,"%.63s",seg);char *ep;long lx=strtol(xs,&ep,10);if(*ep)lx=1;long ly=strtol(ys,&ep,10);if(*ep){lx=1;ly=0;}dx=(int)lx;dy=(int)ly;
        pthread_mutex_lock(&g_pos_lock);int slot=-1;for(int i=0;i<16;i++)if(!strcmp(g_pos[i].qid,qid)){slot=i;break;}if(slot<0)for(int i=0;i<16;i++)if(!g_pos[i].qid[0]){slot=i;snprintf(g_pos[i].qid,sizeof g_pos[i].qid,"%s",qid);break;}if(slot>=0){sx=g_pos[slot].x;sy=g_pos[slot].y;if(!sx&&!sy){sy=1;g_pos[slot].y=1;}}snprintf(key,sizeof key,"@quest:moves:%s",qid);v=lookup(key,&n);int found=0;if(v){char *copy=malloc(n+1);if(copy){memcpy(copy,v,n);copy[n]=0;char *line=copy;while(line&&*line){int ax,ay,ad,ae,bx,by;char *next=strchr(line,'\n');if(next)*next++=0;if(sscanf(line,"%d %d %d %d %d %d",&ax,&ay,&ad,&ae,&bx,&by)==6&&ax==sx&&ay==sy&&ad==dx&&ae==dy){nx=bx;ny=by;found=1;break;}line=next;}free(copy);}}if(found&&slot>=0){g_pos[slot].x=nx;g_pos[slot].y=ny;}pthread_mutex_unlock(&g_pos_lock);snprintf(key,sizeof key,"@movedir:%s:%d:%d:%d:%d",qid,sx,sy,dx,dy);v=lookup(key,&n);if(!v){snprintf(key,sizeof key,"@movedir:%s:%d:%d:0:0",qid,sx,sy);v=lookup(key,&n);}if(v){Team team;Out qteam={0},ateam={0};TemplateArg args[3];if(!resolve_team(&team)||!render_qteam(&qteam,&team)||!render_ateam(&ateam,&team)){free(qteam.p);free(ateam.p);return NULL;}args[0]=(TemplateArg){"%LEAD%",(const unsigned char*)team.bid[0],strlen(team.bid[0])};args[1]=(TemplateArg){"%QTEAM%",qteam.p,qteam.n};args[2]=(TemplateArg){"%ATEAM%",ateam.p,ateam.n};v=template_spaced(o,v,n,args,3,outn);free(qteam.p);free(ateam.p);return v;}return NULL; }
    if(has_suffix(p,"/bcg/setSavedTeam")) { char teamid[64]="0",bids[TEAM_SIZE_MAX][64];int count,invalid;Team team;Out steam={0},ateam={0};TemplateArg args[3];json_string(body,end,"teamID",teamid,sizeof teamid);json_heroes(body,end,bids,&count,&invalid);store_saved_team(bids,count,invalid);v=lookup("@savedteam:template",&n);if(!v||!resolve_team(&team)||!render_steam(&steam,&team)||!render_ateam(&ateam,&team)){free(steam.p);free(ateam.p);return NULL;}args[0]=(TemplateArg){"%TID%",(const unsigned char*)teamid,strlen(teamid)};args[1]=(TemplateArg){"%STEAM%",steam.p,steam.n};args[2]=(TemplateArg){"%ATEAM%",ateam.p,ateam.n};v=template_spaced(o,v,n,args,3,outn);free(steam.p);free(ateam.p);return v; }
    return NULL;
}

static int send_all(int fd, const void *p, size_t n) {
    const unsigned char *s=p;
    while(n) { ssize_t r=send(fd,s,n,MSG_NOSIGNAL); if(r<=0) return 0; s+=r;n-=(size_t)r; }
    return 1;
}
static int ci_equal(const char *a, const char *b) { while(*a&&*b){if(tolower((unsigned char)*a)!=tolower((unsigned char)*b))return 0;a++;b++;}return !*a&&!*b; }
static const char *ci_find_header(const char *h, const char *name) {
    size_t nl=strlen(name); const char *p=h;
    while(*p) { const char *e=strstr(p,"\r\n"); if(!e)break; if((size_t)(e-p)>nl&& !strncasecmp(p,name,nl) && p[nl]==':') {p+=nl+1;while(*p==' '||*p=='\t')p++;return p;} p=e+2; } return NULL;
}
static int has_token(const char *v, const char *token) {
    size_t tl;
    if(!v)return 0;
    tl=strlen(token);
    while(*v&&*v!='\r'&&*v!='\n') {
        while(*v==' '||*v=='\t'||*v==',')v++;
        if(!strncasecmp(v,token,tl)&&(v[tl]==','||v[tl]==' '||v[tl]=='\r'||v[tl]=='\n'||!v[tl]))return 1;
        while(*v&&*v!=',')v++;
    }
    return 0;
}
static void *find_end(unsigned char *p, size_t n) { size_t i; for(i=0;i+4<=n;i++)if(!memcmp(p+i,"\r\n\r\n",4))return p+i;return NULL; }
static int respond(int fd, const unsigned char *data, size_t n, int head) {
    char hdr[160]; int m;
    if(head) { m=snprintf(hdr,sizeof hdr,"HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n"); return send_all(fd,hdr,(size_t)m); }
    m=snprintf(hdr,sizeof hdr,"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: %zu\r\n\r\n",n);
    return m>0 && send_all(fd,hdr,(size_t)m) && send_all(fd,data,n);
}
static ssize_t recv_more(int fd, unsigned char *buf, size_t *used, size_t cap) {
    ssize_t r; if(*used>=cap)return -1; r=recv(fd,buf+*used,cap-*used,0);if(r>0)*used+=(size_t)r;return r;
}
static void *connection(void *arg) {
    int fd=(int)(intptr_t)arg; unsigned char *buf=malloc(MAX_HEAD+MAX_BODY+1); size_t used=0;
    if(!buf) {close(fd);goto done;}
    for (;;) {
        char *head_end; size_t hlen, clen=0, body_have, take; char method[24], target[4096], proto[16], path[4096], *query=""; const char *cv,*ev,*conn; int close_after=0;
        while(!(head_end=(char*)find_end(buf,used))) { if(used>=MAX_HEAD || recv_more(fd,buf,&used,MAX_HEAD+MAX_BODY)<1)goto out; }
        hlen=(size_t)((unsigned char*)head_end-buf)+4;
        if(sscanf((char*)buf,"%23s %4095s %15s",method,target,proto)!=3 || strncmp(proto,"HTTP/",5))goto out;
        cv=ci_find_header((char*)buf,"Content-Length"); if(cv) { char *ep; unsigned long long x=strtoull(cv,&ep,10); if(ep==cv||x>64u*1024u*1024u)goto out;clen=(size_t)x; }
        ev=ci_find_header((char*)buf,"Expect"); if(ev&&has_token(ev,"100-continue")&&!send_all(fd,"HTTP/1.1 100 Continue\r\n\r\n",25))goto out;
        while(used < hlen+clen) { if(used>=MAX_HEAD+MAX_BODY) { /* consume a huge body in chunks, retaining no more than its cap */ break; } if(recv_more(fd,buf,&used,MAX_HEAD+MAX_BODY)<1)goto out; }
        body_have=used>hlen?used-hlen:0; take=body_have<clen?body_have:clen;
        /* Bodies beyond MAX_BODY are deliberately discarded before replying. */
        if(clen>MAX_BODY) { size_t consumed=body_have; unsigned char junk[4096]; while(consumed<clen){size_t want=clen-consumed;if(want>sizeof junk)want=sizeof junk;ssize_t r=recv(fd,junk,want,0);if(r<=0)goto out;consumed+=(size_t)r;} take=MAX_BODY; }
        if (hlen + take < MAX_HEAD + MAX_BODY) buf[hlen + take] = 0;
        snprintf(path,sizeof path,"%s",target); char *q=strchr(path,'?');if(q){*q++=0;query=q;}
        Out o={0}; size_t n=0; const unsigned char *answer;
        if(ci_equal(method,"HEAD")) answer=(const unsigned char*)"";
        else { answer=dynamic(method,path,query,(const char*)buf+hlen,take,&o,&n); if(!answer){char key[4200];snprintf(key,sizeof key,"%s %s",method,path);answer=lookup(key,&n);} if(!answer){uint32_t i;for(i=0;i<g_blob.pc;i++){Rec r=rec_at(&g_blob,g_blob.po,i);if(strlen(path)>=r.kl&&!memcmp(path,g_blob.p+r.ko,r.kl)){answer=body_for(&r,&n);break;}}}if(!answer){answer=g_blob.p+g_blob.dfo;n=g_blob.dfl;} }
        if(!respond(fd,answer,n,ci_equal(method,"HEAD"))){free(o.p);goto out;} free(o.p);
        conn=ci_find_header((char*)buf,"Connection");if(has_token(conn,"close"))close_after=1; if(!strcmp(proto,"HTTP/1.0")&&!has_token(conn,"keep-alive"))close_after=1;
        if(clen>MAX_BODY) { used=0; } else { size_t consumed=hlen+clen; if(used>consumed)memmove(buf,buf+consumed,used-consumed); used=used>consumed?used-consumed:0; }
        if(close_after)break;
    }
out: free(buf);close(fd);
done: pthread_mutex_lock(&g_conn_lock);g_connections--;pthread_mutex_unlock(&g_conn_lock);return NULL;
}
static void *accept_loop(void *unused) {
    int fd=(int)(intptr_t)unused;
    for(;;) { int c=accept(fd,NULL,NULL); if(c<0){if(errno==EINTR)continue;break;} struct timeval tv={60,0};setsockopt(c,SOL_SOCKET,SO_RCVTIMEO,&tv,sizeof tv);pthread_mutex_lock(&g_conn_lock);if(g_connections>=MAX_CONN){pthread_mutex_unlock(&g_conn_lock);close(c);continue;}g_connections++;pthread_mutex_unlock(&g_conn_lock);pthread_t th;pthread_attr_t at;pthread_attr_init(&at);pthread_attr_setstacksize(&at,128*1024);if(pthread_create(&th,&at,connection,(void*)(intptr_t)c)==0)pthread_detach(th);else{pthread_mutex_lock(&g_conn_lock);g_connections--;pthread_mutex_unlock(&g_conn_lock);close(c);}pthread_attr_destroy(&at); }
    close(fd);return NULL;
}
int tftf_server_start_blob(const void *blob, size_t len) {
    Blob b; int fd,opt=1;struct sockaddr_in sa;pthread_t th;int rc=blob_validate(blob,len,&b);
    if(rc){logmsg("payload validation failed (%d)",rc);return -1;} pthread_mutex_lock(&g_start_lock);if(g_started){pthread_mutex_unlock(&g_start_lock);return -2;}g_blob=b;
    fd=socket(AF_INET,SOCK_STREAM,0);if(fd<0)goto fail;setsockopt(fd,SOL_SOCKET,SO_REUSEADDR,&opt,sizeof opt);memset(&sa,0,sizeof sa);sa.sin_family=AF_INET;sa.sin_addr.s_addr=htonl(INADDR_LOOPBACK);sa.sin_port=htons((uint16_t)b.port);
    for(int i=0;i<5;i++){if(!bind(fd,(struct sockaddr*)&sa,sizeof sa))break;if(i==4)goto failclose;struct timespec ts={0,200000000};nanosleep(&ts,NULL);}if(listen(fd,64))goto failclose;if(pthread_create(&th,NULL,accept_loop,(void*)(intptr_t)fd))goto failclose;pthread_detach(th);g_started=1;pthread_mutex_unlock(&g_start_lock);logmsg("in-apk server listening on 127.0.0.1:%u",b.port);return 0;
failclose: close(fd);
fail: pthread_mutex_unlock(&g_start_lock);logmsg("in-apk server bind failed on %u: %s",b.port,strerror(errno));return -3;
}

static int find_entry(int fd, off_t fsize, off_t *off, size_t *len) {
    unsigned char e[22], h[30], c[46]; off_t start=fsize>(off_t)(65536+22)?fsize-(65536+22):0, pos;
    size_t scan=(size_t)(fsize-start);unsigned char *buf=malloc(scan);if(!buf)return -1;if(pread(fd,buf,scan,start)!=(ssize_t)scan){free(buf);return -1;}pos=-1;for(size_t i=scan-22;;i--){if(!memcmp(buf+i,"PK\005\006",4)){pos=start+(off_t)i;break;}if(!i)break;}free(buf);if(pos<0||pread(fd,e,22,pos)!=22)return -1;uint32_t cdsize=le32(e+12),cdoff=le32(e+16);if(cdoff==0xffffffffu)return -1;if((uint64_t)cdoff+cdsize>(uint64_t)fsize)return -1;off_t at=cdoff,end=cdoff+cdsize;
    while(at+46<=end&&pread(fd,c,46,at)==46&& !memcmp(c,"PK\001\002",4)){uint16_t nl=(uint16_t)(c[28]|c[29]<<8),xl=(uint16_t)(c[30]|c[31]<<8),cl=(uint16_t)(c[32]|c[33]<<8);uint32_t local=le32(c+42);char name[128];if(nl>=sizeof name)return -1;if(pread(fd,name,nl,at+46)!=nl)return -1;name[nl]=0;if(!strcmp(name,"assets/tftf_offline_payload.bin")){if((uint16_t)(c[10]|c[11]<<8)!=0)return -1;if(pread(fd,h,30,local)!=30||memcmp(h,"PK\003\004",4))return -1;uint16_t lnl=(uint16_t)(h[26]|h[27]<<8),lxl=(uint16_t)(h[28]|h[29]<<8);*off=(off_t)local+30+lnl+lxl;*len=le32(c+24);return 0;}at+=46+nl+xl+cl;}return -1;
}
static int map_payload_fd(int fd, off_t off, size_t len) { long pg=sysconf(_SC_PAGESIZE);off_t aligned=off&~((off_t)pg-1);size_t delta=(size_t)(off-aligned),whole=delta+len;void*m=mmap(NULL,whole,PROT_READ,MAP_PRIVATE,fd,aligned);if(m==MAP_FAILED)return -1;int rc=tftf_server_start_blob((unsigned char*)m+delta,len);if(rc)munmap(m,whole);return rc; }
static int fallback_magic(int fd, off_t size) { unsigned char *buf=malloc(1024*1024+16);if(!buf)return -1;for(off_t at=0;at<size;at+=1024*1024){size_t want=(size-at>1024*1024+16)?1024*1024+16:(size_t)(size-at);if(pread(fd,buf,want,at)!=(ssize_t)want)break;for(size_t i=0;i+16<=want;i++)if(!memcmp(buf+i,"TFTFPAY\0",8)){size_t n=le32(buf+i+12);if(n&&at+(off_t)i+(off_t)n<=size&&map_payload_fd(fd,at+(off_t)i,n)==0){free(buf);logmsg("APK payload found by magic fallback");return 0;}}}free(buf);return -1; }

#define MAX_APK_CANDIDATES 32

static void read_package_name(const char *cmdline_path, char package[4096]) {
    FILE *f;
    size_t n;
    char *suffix;
    package[0] = 0;
    if (!cmdline_path || !(f = fopen(cmdline_path, "r"))) return;
    n = fread(package, 1, 4095, f);
    fclose(f);
    if (!n) return;
    package[n] = 0;
    /* /proc/self/cmdline is NUL-separated; its first token is already a C string. */
    suffix = strchr(package, ':');
    if (suffix) *suffix = 0;
}

static int apk_matches_package(const char *path, const char *package) {
    const char *p;
    size_t n;
    if (!package || !package[0]) return 0;
    n = strlen(package);
    for (p = strstr(path, package); p; p = strstr(p + 1, package)) {
        if (p > path && p[-1] == '/' &&
            (p[n] == '-' || p[n] == '/' || !strcmp(p + n, ".apk"))) return 1;
    }
    return 0;
}

static int apk_rank(const char *path, const char *package) {
    if (apk_matches_package(path, package)) return 2;
    return has_suffix(path, "/base.apk") ? 1 : 0;
}

int tftf_apk_candidates(const char *maps_path, const char *cmdline_path,
                        char out[][4096], int max) {
    FILE *f;
    char line[4096], package[4096];
    int count = 0, limit, i;
    if (!maps_path || !out || max <= 0) return 0;
    limit = max < MAX_APK_CANDIDATES ? max : MAX_APK_CANDIDATES;
    read_package_name(cmdline_path, package);
    f = fopen(maps_path, "r");
    if (!f) return 0;
    while (fgets(line, sizeof line, f)) {
        char *path = strchr(line, '/');
        size_t n;
        int rank, insert;
        if (!path) continue;
        n = strcspn(path, "\n");
        if (n >= sizeof out[0]) continue;
        path[n] = 0;
        if (!has_suffix(path, ".apk")) continue;
        for (i = 0; i < count; i++) if (!strcmp(out[i], path)) break;
        if (i != count) continue;
        rank = apk_rank(path, package);
        insert = count;
        for (i = 0; i < count; i++) {
            if (rank > apk_rank(out[i], package)) { insert = i; break; }
        }
        if (count < limit || insert < limit) {
            int end = count < limit ? count : limit - 1;
            for (i = end; i > insert; i--) memcpy(out[i], out[i - 1], sizeof out[0]);
            memcpy(out[insert], path, n + 1);
            if (count < limit) count++;
        }
    }
    fclose(f);
    return count;
}

int tftf_server_start_from_apk(void) {
    char paths[MAX_APK_CANDIDATES][4096], package[4096];
    int count, i;
    count = tftf_apk_candidates("/proc/self/maps", "/proc/self/cmdline", paths,
                                MAX_APK_CANDIDATES);
    read_package_name("/proc/self/cmdline", package);
    for (i = 0; i < count; i++) {
        int fd, rc;
        struct stat st;
        off_t off;
        size_t len;
        fd = open(paths[i], O_RDONLY);
        if (fd < 0) {
            logmsg("in-apk candidate rejected %s: open: %s", paths[i], strerror(errno));
            continue;
        }
        if (fstat(fd, &st)) {
            logmsg("in-apk candidate rejected %s: fstat: %s", paths[i], strerror(errno));
            close(fd);
            continue;
        }
        if (!find_entry(fd, st.st_size, &off, &len)) {
            rc = map_payload_fd(fd, off, len);
            close(fd);
            if (!rc) {
                logmsg("APK payload %s offset %lld size %zu", paths[i], (long long)off, len);
                return 0;
            }
            logmsg("in-apk candidate rejected %s: payload could not start (%d)", paths[i], rc);
            continue;
        }
        if (apk_matches_package(paths[i], package)) {
            rc = fallback_magic(fd, st.st_size);
            close(fd);
            if (!rc) return 0;
            logmsg("in-apk candidate rejected %s: payload entry and magic absent", paths[i]);
        } else {
            close(fd);
            logmsg("in-apk candidate rejected %s: payload entry absent (not game APK)", paths[i]);
        }
    }
    logmsg("in-apk payload not found in any of %d mapped APKs", count);
    return -1;
}
