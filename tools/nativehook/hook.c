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
    // ---- quest/mission parse discovery (2026-07-16 session 8): make the first story
    //      mission playable. Bracket the quest-list/details/active parsers so every
    //      fast-dot key read inside them is prefixed "QS " (same trick as ==HEROBASE==).
    { 0x12E3214, "==QLIST==",  99, 0 }, // 59 QuestDB.AddQuestSummarys(ArrayList,pveProg,aveProg) — the quest-list result parser
    { 0x10381F0, "==QSET==",   99, 0 }, // 60 Legacy.QuestSet.AddQuestSummarys(IDictionary) — per-set parser
    { 0x12E4110, "==QDET==",   99, 0 }, // 61 QuestDB.AddQuestDetails(IDictionary) — quest-details result parser
    { 0x12E43C0, "==QACT==",   99, 0 }, // 62 QuestDB.AddActiveQuests(IDictionary) — begin-quest/active result parser
    { 0x12E4EC8, "==QBUILD==", 99, 0 }, // 63 QuestDB.BuildActiveQuest(qid,quest,inst,idx,bgs) — per active quest
    { 0x103A0E4, "==QSDET==",  99, 0 }, // 64 Legacy.QuestSet.AddQuestDetails(builder,summary,data) — map/battle parse
    { 0x122C93C, "==QSUM==",   99, 0 }, // 65 Summary.Deserialize(builder,data) — per-mission summary field keys
    { 0x12EF810, "==QMAP==",   99, 0 }, // 66 QuestMap.Deserialize(builder,data) — map tiles/paths/battles field keys
    // ---- story-visibility instrumentation (session 8): the mission summary parses (==QSUM==
    //      fires) but STORY still says "no missions". These log the category the STORY landing
    //      queries + whether my PvE set is counted/returned, to find the availability gate. ----
    { 0x12E30E8, "QVISCNT",  97, 0 }, // 67 QuestDB.GetQuestSetVisibleCount(category) -> log cat arg + ret + AvailableSets count
    { 0x12E3024, "QSETCNT",  97, 0 }, // 68 QuestDB.GetQuestSetCount(category) -> log cat arg + ret
    { 0x12E2E30, "QSETFROM", 97, 0 }, // 69 QuestDB.GetQuestSetFromCategory(category,index) -> log cat + returned set's category
    // ---- story-unlock fix (session 9): make the first story mission playable. ----
    // The STORY set now parses & is visible (category:"Story"), but the mission tile is
    // LOCKED ("Unlock by completing Act 0"). Chain (all disassembled): ActPanel locks when
    // Act.unlocked==false; Act.UpdateProgression sets act.unlocked = OR(chapter.unlocked);
    // Chapter.UpdateProgression sets chapter.unlocked = OR(quest.get_unlocked()); and
    // QuestSummary.get_unlocked() (@0x103A46C) ultimately returns the private _unlocked@0xC0.
    // _unlocked is NEVER set by the quest-list JSON (Summary.Deserialize reads no such key,
    // confirmed via ==QSUM==) — it is only set from per-account quest-PROGRESSION data that a
    // real server sends and the offline server does not. So every quest defaults locked.
    // Rather than reverse-engineer the entire nested, category-keyed progression response and
    // its binary string-literal keys, force the single gate: get_unlocked -> true. Only real
    // quest summaries reach this (the phantom act[0]/chapter[0] hold zero quests), so this
    // unlocks exactly the authored missions. Same targeted-hook approach as the combat fixes.
    { 0x103A46C, "FORCEUNLK", 2, 0 }, // 70 QuestSummary.get_unlocked -> return true (offline unlock)
    // The ActPanel reads Act.unlocked (byte @0x30) DIRECTLY (ActPanel.RefreshMainView @0xC2B584),
    // so forcing QuestSummary.get_unlocked alone is not enough — Act.unlocked must be true. It is
    // written only by Act.UpdateProgression (@0xC2ADC8, unlocked = OR(chapter.unlocked)), called
    // from QuestSet.AddQuestSummarys at login and QuestSet.UpdateProgression. Offline it computes
    // false (no progression data; chapters' quests aren't unlocked at build time). Force it: run
    // the original, then set this.unlocked(@0x30)=1 so the STORY act panel is playable. completed
    // (@0x31) is left as computed (false for the real act -> panel treats it as the "current" act).
    { 0xC2ADC8, "FORCEACT",  2, 0 }, // 71 Act.UpdateProgression -> force this.unlocked=1
    // Forcing Act.unlocked alone left the STORY ACT panel empty + locked (red padlock), so the
    // panel's own lock is the residual gate. QuestSelectPanelBase.get_isLocked (@0xCB2954) =
    // isLevelLocked@0x48 || isJoinRequirementLocked@0x49 || actData==null || act.unlocked@0x30==0
    // || isActivationLocked@0x4a. The offline account has level/CL 0 and no activation/join data,
    // so isLevelLocked / isActivationLocked can still be set. Override the whole gate: return false
    // (not locked) for every QuestSelectPanel (base of both ActPanel and the mission-tile panel),
    // so the STORY act and its mission node render unlocked/tappable. Same targeted-override pattern
    // as FORCEUNLK — only story-select panels reach this getter.
    { 0xCB2954, "FORCELOCK", 2, 0 }, // 72 QuestSelectPanelBase.get_isLocked -> return false
    // DIAGNOSTIC (session 9): ActPanel.RefreshMainView (@0xC2B4E0). The panel now renders unlocked
    // but empty (no mission node). RefreshMainView gates node rendering on: actData.Chapters[]
    // (@0x28) length>=2 AND Chapters[1].Quests[] (Summary[] @0x28) length>=2 (phantom [0] + real).
    // Log the real linked counts so we can see whether the authored chapters/availableQuests link a
    // Summary into Chapters[1].Quests[1], or the data never reaches the act structure.
    { 0xC2B4E0, "RMV",       2, 0 }, // 73 ActPanel.RefreshMainView -> log act/chapter/quest link counts
    // With the ACT unlocked, tapping it expands to a ChapterPanel that is STILL locked ("Unlock by
    // completing Chapter 0"). ChapterPanel is a plain MonoBehaviour (not a QuestSelectPanelBase, so
    // FORCELOCK doesn't reach it); its OnPanelClicked (@0xD15394) reads Chapter.unlocked (byte @0x30)
    // DIRECTLY at 0xd15470 — if 0 it shows the warning instead of entering the mission. Chapter.unlocked
    // is written only by Chapter.UpdateProgression (@0xD13E78, unlocked = OR(quest.get_unlocked())),
    // which — exactly like Act.UpdateProgression — runs at login BEFORE the quest summaries are linked
    // into Chapters[].Quests[], so it computes unlocked=0 (OR of nothing) / completed=1 (AND of nothing)
    // and nothing recomputes it at story-open. Force it, mirroring FORCEACT: run orig, then set
    // this.unlocked(@0x30)=1 and this.completed(@0x31)=0 so the chapter renders unlocked and its tap
    // enters the mission node instead of the "complete Chapter 0" warning.
    { 0xD13E78, "FORCECHAP", 2, 0 }, // 74 Chapter.UpdateProgression -> force this.unlocked=1
    // DIAGNOSTIC: ChapterPanel.OnPanelClicked (@0xD15394). After FORCECHAP the chapter is unlocked
    // (no warning) but tapping it doesn't transition. OnPanelClicked reads _chapterData@0x88,
    // unlocked@0x30 (skip-lock), then invokes onClick@0x78 (Action<ChapterPanel>) — if onClick is
    // null nothing happens. Log unlocked + whether onClick is wired to see if the tap registers and
    // where the flow stops.
    { 0xD15394, "CHAPCLK",   2, 0 }, // 75 ChapterPanel.OnPanelClicked -> log unlocked + onClick null
    // CHAPCLK proved the STORY ChapterPanel's _chapterData.unlocked is 0 at click time even with
    // FORCECHAP — the panel binds a Chapter instance whose UpdateProgression was never forced (it
    // runs only at login, and only on the login set's chapters, not the story-open panel's). The Act
    // survives this because ActPanel is a QuestSelectPanelBase covered by FORCELOCK; ChapterPanel is
    // a plain MonoBehaviour reading Chapter.unlocked@0x30 directly. Fix at the bind point:
    // ChapterPanel.SetData(this, chapterIndex, chapterData) (@0xD14470) stores _chapterData@0x88 then
    // falls into RefreshDisplay. Force the passed chapterData.unlocked(@0x30)=1 / completed(@0x31)=0
    // BEFORE the original runs, so the panel both renders unlocked and enters the mission on tap.
    { 0xD14470, "FORCECHAPSD", 2, 0 }, // 76 ChapterPanel.SetData -> force chapterData.unlocked=1
    // FIXWRAPMI (session 10): make selecting a bot into the STORY squad not crash. Tapping any
    // bot in the EDIT SQUAD roster hard-SIGSEGVs at libil2cpp 0x152b5ec inside
    // SafeAction.<>c__DisplayClass1_0<object>.<Wrap>b__0(T obj) (entry 0x152B570). That method is
    // generic-shared, so its hidden last arg (x2) is the MethodInfo* it needs to resolve
    // Action<object>::Invoke via RGCTX (ldr [x2,#0x18] @0x152b5ec). The bot-select onClick reaches
    // it through a void-signature delegate Invoke chain (OnDragNotification.Invoke -> EB.Action.Invoke
    // -> here) that does NOT thread the gshared MethodInfo through, so x2 arrives NULL -> null-deref.
    // Every legit invocation of this one <object> instantiation passes the SAME MethodInfo, so cache
    // the first non-null x2 and substitute it whenever x2 is null. this(x0)/obj(x1) are intact; only
    // the RGCTX pointer is missing, so restoring it lets cb.Invoke(obj) (the real add-to-squad
    // callback) run. Same targeted-arg-fix approach as SETACTFIX (slot 58).
    { 0x152B570, "FIXWRAPMI", 2, 0 }, // 77 SafeAction.<Wrap>b__0<object> -> restore null gshared MethodInfo
    // STORY encounter discovery: MapTile.Deserialize calls this factory once for every
    // entry in tile["entities"]. Log the two parsed type strings and the returned runtime
    // class so authored enemy data can be verified without guessing which of boss/tower/bcg
    // selected the BCGEntity constructor.
    { 0xEF28CC, "NEWENTITY", 2, 0 }, // 78 Quests.Builder.NewEntity(baseType,entityType,...)
    // BASEDIAG (session 24): the authored /base/active board BUILDS with zero client errors and
    // pulls the right bundles (primordial_base, base_merged, assets_base) but renders almost
    // entirely BLACK -- panning up shows a lit sky + a curved silhouette, so geometry exists but
    // is unlit and/or the camera is aimed off the map. BaseBoard.OnBaseBoardBuildComplete
    // (@0xA6EB98) is the one point where the whole build is finished and every field is final,
    // so dump it in a single shot rather than guessing: the builder's time-of-day GameObject and
    // resolved _TimeOfDayPath (a null ToD means no lights/skybox -> black terrain), the theme /
    // theme-full names, _ThemeLibrary + _ThemeMaterial (null theme material = terrain with no
    // material), the terrain/gameobject partition and light counts (zero terrain partitions would
    // mean nothing was built at all), and the BaseCameraController's Target/_LookAtTarget/FoV
    // (to see whether the camera is aimed at the authored map centre or at the origin).
    { 0xA6EB98, "BASEDIAG", 2, 0 }, // 79 BaseBoard.OnBaseBoardBuildComplete -> dump build state
    // The base's render settings are applied ONLY here, and OnBaseBoardBuildComplete reaches it
    // through set_IsBase(true) -- which it skips entirely when GetActiveTimeOfDay() returns null
    // (cbz @0xA6EE14). A missing BASEAPPLY line therefore means the base never got its ambient/
    // fog/light settings, which is exactly what an all-black board looks like. Hooked here rather
    // than on set_IsBase itself because that setter is only 20 bytes long -- too small to patch.
    { 0xCF52DC, "BASEAPPLY", 2, 0 }, // 80 BaseRenderSettings.ApplyBaseSettings -> marker
    // BASEAPPLY fires (confirmed live), so the base DOES get its render settings and the
    // "ToD never applied" theory is dead. ApplyBaseSettings turns out to be fog-only, but
    // its base class EBRenderSettingsBase drives a physically-based EXPOSURE (Aperture /
    // ShutterTime / ISO -> GetEV100 -> ApplyExposure -> a global shader property) while the
    // sky rides a separate SkyLuminousScale/SkyClearColor path. Zero/garbage exposure blacks
    // out everything lit and leaves the sky and (unlit) UI text drawing -- which is EXACTLY
    // the symptom. Fog is the other candidate: a black FogColor with a short FogDistEnd
    // would paint identical results, camera being ~398 units from the map centre.
    // ApplyWhenActivated runs once per render-settings activation, so logging the whole
    // field block here yields both the base's numbers AND every other scene's numbers in one
    // run -- a direct diff of a working scene against the black one, no guessing.
    { 0x18C2CA4, "RSACTIVE", 2, 0 }, // 81 EBRenderSettingsBase.ApplyWhenActivated -> dump fields
    // The active EBTimeOfDay ('Day_Base') has a non-null KeyLight, 1 LightObject, a skybox and
    // a 1-entry Lightmaps array, and EBTimeOfDay.Apply is fully null-guarded, so the null
    // MergedLights/LightmapSettings are harmless. But KeyLight is ONLY SetActive(true) from
    // inside Apply (@0x124BFD8), and the merged lightmap is only bound to the global "_lm"
    // shader texture in Apply's tail (@0x124C130) -- and BOTH are gated on enabled==true.
    // If Apply never runs, or runs with enabled=false, the scene has no sun and no lightmap:
    // geometry renders and depth-writes but shades black, exactly the symptom. Log the id and
    // the enabled flag from both the per-ToD Apply and the manager-level Apply that drives it.
    { 0x124BDE8, "TODAPPLY", 2, 0 },  // 82 EBTimeOfDayManager.EBTimeOfDay.Apply(id, enabled)
    { 0x18C5150, "TODMGRAPPLY", 2, 0 }, // 83 EBTimeOfDayManager.Apply(broadcast, applyActive)
    // FIXTERRAIN (session 25). GameboardBuilder.FixTerrain is where the terrain actually gets
    // its material: at @0xB65058-0xB65094 it reads _ThemeMaterial, does
    // GetComponentInChildren<Renderer> on it, takes that renderer's sharedMaterial, and
    // assigns it to the freshly merged "<name>_terrain" GameObject's renderer. Every earlier
    // "theme material" reading was taken off the ThemeMaterial MonoBehaviour itself, which is
    // not a Material -- this is the real one. FixTerrain is shared by the STORY and BASE
    // boards, so one hook logs both and the working board doubles as the control: if story's
    // terrain material resolves a real shader and base's does not, the base theme asset is
    // genuinely broken; if both look identical, the black is not a material problem at all.
    { 0xB64608, "FIXTERRAIN", 2, 0 }, // 84 GameboardBuilder.FixTerrain(GameObject)
    // The base/story terrain materials differ in exactly one meaningful way: the base one is
    // bound to the shader variant 'Hidden/PBR_EB_PLANAR_REFLECTION_ON__...' while the story
    // one uses 'Hidden/PBRTerrain_BASE_EB_EMISSIVE_NO_PULSE'. That variant name is baked into
    // the material, so the base terrain ALWAYS samples the planar-reflection texture --
    // EBPlanarReflectionManager.ReflectionTextureName, set as a global by Render(). If the
    // manager is quality Off (eQUALITY.Off = -1, which is what PerformanceManager picks on a
    // weak GPU) it never renders and never sets that global, leaving the terrain sampling an
    // unset texture. Log the quality that gets chosen and whether Render ever runs.
    { 0x1ABF03C, "PRINIT", 2, 0 },   // 85 EBPlanarReflectionManager.Init
    { 0x1ABFCF0, "PRRENDER", 2, 0 }, // 86 EBPlanarReflectionManager.Render (once-flagged)
    // Setup creates the reflection camera and render targets; LoadPlanarReflectionConfig is
    // the EBSetup handler that fills in the Config (Quality@0x10) beforehand. Result: all four
    // fire, six times each, at Quality=High(1) -- so RemapPlanarReflectionQuality (@0xDA74DC,
    // `tier == 1 ? Off(-1) : High(1)`) is NOT classifying this emulator as the low tier, and
    // "the reflection texture is never produced" is not the explanation for the black terrain.
    { 0x1ABECE4, "PRSETUP", 2, 0 },  // 87 EBPlanarReflectionManager.Setup
    { 0xDA8270,  "PRCONFIG", 2, 0 }, // 88 PerformanceManager.LoadPlanarReflectionConfig(ref Config)
    // BASEBLD (session 28): the base's buildings are authored end-to-end and PARSE clean
    // (catalogue x6 + owned x5 + placements x5 all create Building instances, NEWENTITY
    // confirms), but no building model shows on the board. These four bracket the display
    // side of the chain node-by-node:
    //   AttachNodeController fires once per node object the builder creates -- if BASENODE
    //   never appears, the base board never spawns nodes at all (the story board needed
    //   QuestProgression players before its nodes appeared; the base may have its own gate).
    //   The custom hook also dumps the tile's buildingSocket@0x130 / mission@0x140 /
    //   mission.placement@0x70 / placement.entities@0x20 pointers: bs=0 means the socket
    //   type lookup failed, ent=0 means placements never reached the tile's mission.
    { 0xA6FF98, "BASENODE",   2, 0 }, // 89 BaseBoard.AttachNodeController(nodeObj,mapTile) -> custom dump
    { 0xCF2D30, "NODEREFRESH",2, 0 }, // 90 BaseNodeController.Refresh (marker; runs get_building)
    //   SetBuilding logs the resolved model name (a2): present+correct means the whole data
    //   chain works and only the 3D load/lighting is left.
    { 0xB67944, "SETBLDG",    3, 0 }, // 91 GameboardBuilder.SetBuilding(node=a1,name=a2,cb) -> jp3 logs a2
    { 0xB67AB4, "LOADBLDG",   7, 0 }, // 92 GameboardBuilder.LoadBuildingObject(name=a1,cb) -> jp7 logs a1
    //   ONBLDSET closes the load loop: go=0 means the asset request never produced a
    //   GameObject (LOADBLDG fired but nothing came back -- an asset-resolution failure),
    //   while go!=0 means the model IS instantiated and only rendering keeps it invisible.
    { 0xCF4854, "ONBLDSET",   2, 0 }, // 93 BaseNodeController.OnBuildingSet(buildingId=a1,go=a2) -> custom
    // CAMFRAME (slot 94): BaseCameraController.get__CurrentPosOffset -- override the baked
    // pos-offset so the small authored board frames close. See the CAMFRAME note above.
    { 0x121C634, "CAMFRAME",  2, 0 }, // 94 BaseCameraController.get__CurrentPosOffset(this=a0)
    // BLDGSWAP: SpawnBuilding is the visible-object creation site. The normal path resolves
    // "buildings/prefabs/" + name as a standalone asset and falls back to DefaultRelic, but
    // the real z_bldg_* prefabs are inactive children registered in _BuildingLibrary@0x158.
    { 0xB67CC0, "BLDGSWAP",  2, 0 }, // 95 GameboardBuilder.SpawnBuilding(name=a1,node=a2) -> custom
    // BLDGLEAVE: BaseBoard.LeaveBoard is the base-specific teardown path. The game's normal
    // board-root deactivation does not reach the AssetManager-resident fallback anchors that
    // BLDGACT made visible, so hide the hook-touched objects here before STORY takes its camera.
    { 0xA6F844, "BLDGLEAVE", 2, 0 }, // 96 BaseBoard.LeaveBoard -> hide tracked base objects
    // Base-builder entry diagnostics: marker-only hooks; no method behaviour is changed.
    { 0xA71810, "CARDTAP",    2, 0 }, // 97 BaseBoard.OnCardTapped(this,card)
    { 0xA712FC, "TILEBLDG",   2, 0 }, // 98 BaseBoard.HandleTileBuildingInteraction(this,tile)
    { 0xA70A80, "EDITPOP",    3, 0 }, // 99 BaseBoard.OpenBaseEditBuildingPopup(this,tile,cb)
    { 0x121D450,"POPENTER",  2, 0 }, // 100 BaseEditBuildingPopup.WindowEnter(this,isFirstEntry)
    { 0x1220D40,"NODEPOP",   2, 0 }, // 101 BaseEditNodePopup.WindowEnter(this,isFirstEntry)
    // Workaround: the fixed login tutorial response omits only FTEBaseCrystal completion.
    { 0x14F8578,"FTEBASEFIX",2, 0 }, // 102 TutorialManagerHelper.IsBranchComplete(tutorial,branch)
    // Base-builder card lifecycle diagnostics.  These are deliberately observational:
    // RelicCard.Init is the only writer of its base-node gate (@0x88), while Show/
    // Hide/provider initialization establish whether the card ever becomes clickable.
    { 0xE27978, "RCINIT",    3, 0 }, // 103 RelicCard.Init(this,tile,provider)
    { 0xE27888, "RCSHOW",    2, 0 }, // 104 RelicCard.Show(this,...)
    { 0xE2791C, "RCHIDE",    2, 0 }, // 105 RelicCard.Hide(this,...)
    { 0xE27B78, "RCACTIVE", 2, 0 }, // 106 RelicCard.UpdateActiveGameObject
    { 0xE27A80, "RCCOLLIDER",2, 0 }, // 107 RelicCard.UpdateCollider
    { 0xE27D94, "RCCLICK",   2, 0 }, // 108 RelicCard.OnClick
    { 0xCF3584, "CARDLOAD",  2, 0 }, // 109 BaseNodeController.OnRelicCardLoaded
    { 0xA7223C, "SHOWCARDS", 2, 0 }, // 110 BaseBoard.ShowAllNodeCards
    { 0xA708B0, "HIDECARDS", 2, 0 }, // 111 BaseBoard.HideAllNodeCards
    { 0xE27680, "RCAWAKE",   2, 0 }, // 112 RelicCard.Awake
    { 0x1BF84A4,"UICLICK",   2, 0 }, // 113 UIEventListener.OnClick
    // The card collider and listener are both live, but UICamera's world raycast
    // never returns them on this offline base scene.  ProcessTouch is the narrow
    // real-input seam used for the fallback below (one normal card OnClick per tap).
    { 0x1F82794,"BASETAPFIX",3, 0 }, // 114 UICamera.ProcessTouch
    // Popup-initialization bisect.  These remain marker-only while diagnosing the
    // managed exception after POPENTER; the order matches the coroutine chain.
    { 0xC407D4, "POPINITSM",  1, 0 }, // 115 <DoWindowInitialization>d__11.MoveNext
    { 0x121D670,"POPINIT",    3, 0 }, // 116 BaseEditBuildingPopupPresentation.Init
    { 0x121D7EC,"POPSETPARAM",3, 0 }, // 117 BaseEditBuildingPopupPresentation.SetEnterParams
    { 0x121E540,"POPBUILD",   1, 0 }, // 118 BaseEditBuildingPopupPresentation.BuildPresentation
    { 0x121EBF8,"POPLIST",    2, 0 }, // 119 BaseEditBuildingPopupPresentation.GetBuildingList
    { 0x1220128,"POPFILTER",  3, 0 }, // 120 BaseEditBuildingPopupPresentation.FilterBuildingData
    { 0x121D89C,"POPUPDATE",  2, 0 }, // 121 BaseEditBuildingPopupPresentation.UpdatePresentation
};
#define NH (int)(sizeof(H)/sizeof(H[0]))

// Set only while inside BCGHeroBase..ctor (slot 45 brackets it). When set, every
// key read is prefixed "HB " so the ctor's exact field keys can be grepped out of
// the flood of general parse keys. Drives authoring login `heroes` BCGHeroBase JSON.
static volatile int g_inhb = 0;
// Set while inside any quest parser (slots 59-64 bracket them, jp=99). Keys read inside
// are prefixed "QS " so the quest-list/details field keys can be grepped out.
static volatile int g_inqs = 0;
// Runtime input fallback, not a data substitute: populated only after RelicCard.Init
// has received a BaseNodeController and a tile with an actual building.
static void* g_base_tap_card = NULL;
static void* g_base_tap_go = NULL;
static volatile int g_base_tap_dispatching = 0;
// LoadBuildingObject receives the authored model id before its failed standalone-asset
// request falls back to DefaultRelic.  SpawnBuilding then receives DefaultRelic, so retain
// the authored ids in call order and use them to pair those fallback anchors with the
// PrefabLibrary children that actually contain the real meshes.
#define BLDG_FIFO_CAP 16
static char g_bldg_fifo[BLDG_FIFO_CAP][96];
static int g_bldg_fifo_head = 0, g_bldg_fifo_count = 0;
#define BLDG_TRACK_CAP 24
static void* g_bldg_tracked[BLDG_TRACK_CAP];
static int g_bldg_tracked_count = 0;
static int read_str(void* s, char* buf, int cap);
static int obj_ok(void* p);
static void bldg_track(void* go){
    if (!obj_ok(go)) return;
    for (int i=0;i<g_bldg_tracked_count;i++) if (g_bldg_tracked[i] == go) return;
    if (g_bldg_tracked_count < BLDG_TRACK_CAP) g_bldg_tracked[g_bldg_tracked_count++] = go;
}
static void bldg_fifo_push(const char* name){
    int slot;
    if (!name || strncmp(name, "z_bldg_", 7) != 0) return;
    if (g_bldg_fifo_count == BLDG_FIFO_CAP) {
        g_bldg_fifo_head = (g_bldg_fifo_head + 1) % BLDG_FIFO_CAP;
        g_bldg_fifo_count--;
    }
    slot = (g_bldg_fifo_head + g_bldg_fifo_count) % BLDG_FIFO_CAP;
    snprintf(g_bldg_fifo[slot], sizeof g_bldg_fifo[slot], "%s", name);
    g_bldg_fifo_count++;
}
static int bldg_fifo_pop(char* out, int cap){
    if (!out || cap < 1 || g_bldg_fifo_count < 1) return 0;
    snprintf(out, (size_t)cap, "%s", g_bldg_fifo[g_bldg_fifo_head]);
    g_bldg_fifo_head = (g_bldg_fifo_head + 1) % BLDG_FIFO_CAP;
    g_bldg_fifo_count--;
    return 1;
}
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
    if (g_inqs) fputs("QS ", g_f);
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
MKHOOK(91)
// LOADBLDG (slot 92): log the requested model as before, then queue authored building ids
// so the later DefaultRelic SpawnBuilding fallbacks can be paired FIFO with their real prefab.
void* hook_92(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    PROTECT({
        char name[96];
        if (read_str(a1, name, sizeof name)) {
            flog("LOADBLDG %s", name);
            bldg_fifo_push(name);
        } else {
            flog("LOADBLDG <null>");
        }
    });
    return H[92].orig(a0,a1,a2,a3,a4,a5,a6,a7);
}
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
        flog("%sFDS2 name='%s' alt='%s'", g_inqs?"QS ":"", k1, k2); } );
    return H[52].orig(a0,a1,a2,a3,a4,a5,a6,a7);
}
// slots 59-64 (jp=99): quest-parser brackets. Increment/decrement g_inqs (they nest:
// QuestDB.AddQuestSummarys calls QuestSet.AddQuestSummarys) and emit begin/end markers so
// the "QS <tag> <key>" reads in between reveal the exact quest JSON field keys.
#define MKQS(i) \
  void* hook_##i(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){ \
    PROTECT( flog("%s begin", H[i].tag); ); \
    g_inqs++; \
    void* r = H[i].orig(a0,a1,a2,a3,a4,a5,a6,a7); \
    g_inqs--; \
    PROTECT( flog("%s end", H[i].tag); ); \
    return r; }
MKQS(59) MKQS(60) MKQS(61) MKQS(62) MKQS(63) MKQS(64) MKQS(65) MKQS(66)
// slots 67-69 (session 8): story-visibility instrumentation. Log the category argument and
// results of the set-count/lookup queries the STORY landing runs, to locate the availability
// gate that keeps the (correctly-parsed) mission from appearing. read_str/flog defined above.
void* hook_67(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    void* r = H[67].orig(a0,a1,a2,a3,a4,a5,a6,a7);
    PROTECT( char c[80]; if(!read_str(a1,c,sizeof c)) strcpy(c,"<null>");
        int avail=-1; uintptr_t db=(uintptr_t)a0;
        if(db>=0x100000 && !(db&7)){ uintptr_t lst=*(uintptr_t*)(db+0x10);
            if(lst>=0x100000 && !(lst&7)) avail=*(int*)(lst+0x18); }
        flog("QVISCNT cat='%s' ret=%ld availSets=%d", c, (long)r, avail); );
    return r;
}
void* hook_68(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    void* r = H[68].orig(a0,a1,a2,a3,a4,a5,a6,a7);
    PROTECT( char c[80]; if(!read_str(a1,c,sizeof c)) strcpy(c,"<null>");
        flog("QSETCNT cat='%s' ret=%ld", c, (long)r); );
    return r;
}
void* hook_69(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    void* r = H[69].orig(a0,a1,a2,a3,a4,a5,a6,a7);
    PROTECT( char c[80]; if(!read_str(a1,c,sizeof c)) strcpy(c,"<null>");
        char sc[80]; sc[0]=0; uintptr_t s=(uintptr_t)r;
        if(s>=0x100000 && !(s&7)){ void* scat=*(void**)(s+0x28); if(!read_str(scat,sc,sizeof sc)) strcpy(sc,"?"); }
        else strcpy(sc,"<null-set>");
        flog("QSETFROM cat='%s' -> set.cat='%s'", c, sc); );
    return r;
}
// slot 70 FORCEUNLK: QuestSummary.get_unlocked() @0x103A46C -> force true. Fully replaces the
// original (does NOT call orig): the real fn returns _unlocked@0xC0 which offline is always 0.
// Returning true makes Chapter/Act.UpdateProgression mark the chapter/act unlocked, so the STORY
// mission tile stops showing "Unlock by completing Act 0" and becomes playable. Logs once so the
// unlock can be confirmed in the log, then throttles.
void* hook_70(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    static int n = 0;
    if (n < 3) { PROTECT( flog("FORCEUNLK -> true"); ); n++; }
    return (void*)1;
}
// slot 71 FORCEACT: Act.UpdateProgression @0xC2ADC8. Run original, then force this.unlocked=1
// (byte @0x30) so the STORY ActPanel (which reads the field directly) renders unlocked/playable.
void* hook_71(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    void* r = H[71].orig(a0,a1,a2,a3,a4,a5,a6,a7);
    PROTECT( uintptr_t act=(uintptr_t)a0;
        if(act>=0x100000 && !(act&7)){
            static int n=0;
            unsigned char bu=*(unsigned char*)(act+0x30);
            unsigned char bc=*(unsigned char*)(act+0x31);
            // The act's chapters have no quests linked when UpdateProgression runs at login, so it
            // computes unlocked=0 (OR of nothing) and completed=1 (AND of nothing). Force unlocked=1
            // AND completed=0 so get_isLocked() is false and get_isCurrent() (unlocked && !completed)
            // is true -> the STORY act renders as the current, playable act (not "completed"/empty).
            *(unsigned char*)(act+0x30) = 1;   // unlocked
            *(unsigned char*)(act+0x31) = 0;   // completed
            if(n<4){ flog("FORCEACT unlocked %d->1 completed %d->0", bu, bc); n++; }
        } );
    return r;
}
// slot 72 FORCELOCK: QuestSelectPanelBase.get_isLocked @0xCB2954 -> force false (not locked).
// Fully replaces the original (does NOT call orig): the real getter ORs several lock bits
// (isLevelLocked/isJoinReq/actData==null/act.unlocked==0/isActivationLocked) that are all set or
// null offline (level/CL 0, no activation data). Returning false makes the STORY act panel and its
// mission node render unlocked/tappable instead of showing the red padlock. Logs a few times.
void* hook_72(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    static int n = 0;
    if (n < 4) { PROTECT( flog("FORCELOCK -> false"); ); n++; }
    return (void*)0;
}
// slot 73 RMV: ActPanel.RefreshMainView(this=a0) diagnostic. Read the act link structure the
// render path gates on: _actIndex@0x228, _actData@0x220 (Act); Act.Chapters[] (@0x28, arr len@0x18,
// elems@0x20); Chapters[1] (@0x28); Chapters[1].Quests[] (Summary[] @0x28, len@0x18); and the real
// quest Quests[1] (@0x28) unlocked byte@0xC0. Tells us exactly which array is short/unlinked.
void* hook_73(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    PROTECT( uintptr_t p=(uintptr_t)a0;
        int actIdx=-1; uintptr_t act=0; int chLen=-1; uintptr_t ch1=0; int qLen=-1; int q1unl=-1;
        if(p>=0x100000 && !(p&7)){
            actIdx=*(int*)(p+0x228);
            act=*(uintptr_t*)(p+0x220);
            if(act>=0x100000 && !(act&7)){
                uintptr_t chs=*(uintptr_t*)(act+0x28);          // Chapter[] Chapters
                if(chs>=0x100000 && !(chs&7)){
                    chLen=*(int*)(chs+0x18);
                    if(chLen>=2){ ch1=*(uintptr_t*)(chs+0x28);   // Chapters[1]
                        if(ch1>=0x100000 && !(ch1&7)){
                            uintptr_t qs=*(uintptr_t*)(ch1+0x28); // Summary[] Quests
                            if(qs>=0x100000 && !(qs&7)){
                                qLen=*(int*)(qs+0x18);
                                if(qLen>=2){ uintptr_t q1=*(uintptr_t*)(qs+0x28); // Quests[1]
                                    if(q1>=0x100000 && !(q1&7)) q1unl=*(unsigned char*)(q1+0xC0); }
                            } } } } } }
        flog("RMV actIdx=%d act=%p chLen=%d ch1=%p qLen=%d q1unl=%d", actIdx,(void*)act,chLen,(void*)ch1,qLen,q1unl); );
    return H[73].orig(a0,a1,a2,a3,a4,a5,a6,a7);
}
// slot 74 FORCECHAP: Chapter.UpdateProgression @0xD13E78. Mirror of FORCEACT (slot 71). Run the
// original, then force this.unlocked(@0x30)=1 and this.completed(@0x31)=0 so ChapterPanel.OnPanelClicked
// (which reads Chapter.unlocked @0x30 directly) enters the mission instead of showing the "Unlock by
// completing Chapter 0" warning. The chapter's Quests aren't linked when this runs at login, so the
// real computation yields unlocked=0/completed=1; only the authored chapters reach here.
void* hook_74(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    void* r = H[74].orig(a0,a1,a2,a3,a4,a5,a6,a7);
    PROTECT( uintptr_t ch=(uintptr_t)a0;
        if(ch>=0x100000 && !(ch&7)){
            static int n=0;
            unsigned char bu=*(unsigned char*)(ch+0x30);
            unsigned char bc=*(unsigned char*)(ch+0x31);
            *(unsigned char*)(ch+0x30) = 1;   // unlocked
            *(unsigned char*)(ch+0x31) = 0;   // completed
            if(n<4){ flog("FORCECHAP unlocked %d->1 completed %d->0", bu, bc); n++; }
        } );
    return r;
}
// slot 75 CHAPCLK: ChapterPanel.OnPanelClicked(this=a0, go=a1) diagnostic. Log _chapterData@0x88,
// its unlocked byte@0x30, and whether onClick@0x78 is non-null (the wired transition callback).
void* hook_75(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    PROTECT( uintptr_t p=(uintptr_t)a0; int unl=-1; int hasClick=-1; uintptr_t cd=0;
        if(p>=0x100000 && !(p&7)){
            cd=*(uintptr_t*)(p+0x88);
            if(cd>=0x100000 && !(cd&7)) unl=*(unsigned char*)(cd+0x30);
            hasClick = (*(uintptr_t*)(p+0x78)!=0)?1:0;
        }
        flog("CHAPCLK chapterData=%p unlocked=%d onClickWired=%d", (void*)cd, unl, hasClick); );
    return H[75].orig(a0,a1,a2,a3,a4,a5,a6,a7);
}
// slot 76 FORCECHAPSD: ChapterPanel.SetData(this=a0, chapterIndex=a1, chapterData=a2) @0xD14470.
// Force the bound chapter (a2) unlocked(@0x30)=1 and completed(@0x31)=0 BEFORE the original runs
// (which stores it and refreshes the display) so the STORY chapter panel renders unlocked and its
// OnPanelClicked — which reads Chapter.unlocked@0x30 directly — enters the mission instead of the
// "complete Chapter 0" warning. This targets the exact Chapter instance the panel uses (unlike
// FORCECHAP, which only caught the login UpdateProgression instances).
void* hook_76(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    PROTECT( uintptr_t cd=(uintptr_t)a2;
        if(cd>=0x100000 && !(cd&7)){
            static int n=0; unsigned char bu=*(unsigned char*)(cd+0x30);
            *(unsigned char*)(cd+0x30)=1;   // unlocked
            *(unsigned char*)(cd+0x31)=0;   // completed
            if(n<4){ flog("FORCECHAPSD chapter=%p unlocked %d->1", (void*)cd, bu); n++; }
        } );
    return H[76].orig(a0,a1,a2,a3,a4,a5,a6,a7);
}
// slot 77 FIXWRAPMI: SafeAction.<>c__DisplayClass1_0<object>.<Wrap>b__0(this=a0, obj=a1, MethodInfo*=a2)
// @0x152B570. This gshared method needs a2 (its MethodInfo) to resolve Action<object>::Invoke via
// RGCTX. Tapping a bot in the EDIT SQUAD roster reaches it through a mis-signatured delegate-Invoke
// chain (UIScrollView.OnDragNotification.Invoke -> EB.Action.Invoke -> here) that passes NEITHER a
// valid obj arg NOR the hidden gshared MethodInfo (a2 arrives NULL). Restoring a2 alone just pushes
// the crash one frame deeper: cb.Invoke(obj) then runs an isinst type-check on the bogus obj
// (obj.klass.typeHierarchy) and null-derefs inside EditTeamScreenPresentation.OnGridItemInitialized.
// A gshared invocation with a null MethodInfo can never be legitimate, so SKIP it entirely (return
// without calling the body). The real bot-select add path (the grid item's own click handler) is a
// separately-wired, correctly-signatured call that passes a non-null MethodInfo, so it is unaffected
// -- only this spurious drag-notification callback is suppressed.
void* hook_77(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    if ((uintptr_t)a2 < 0x1000) {   // null / bogus MethodInfo -> broken gshared call, skip
        static int n=0; if(n<8){ n++; flog("FIXWRAPMI skip null-MethodInfo call (this=%p obj=%p)", a0, a1); }
        return NULL;
    }
    return H[77].orig(a0,a1,a2,a3,a4,a5,a6,a7);
}
// slot 78 NEWENTITY: Quests.Builder.NewEntity(this=a0, baseType=a1, entityType=a2,
// position in v0, extraData=a3). The generic fn8 prototype cannot name the float registers,
// but the two type strings completely identify which constructor branch is selected. The
// returned object's class name confirms whether the result is Quests.BCGEntity or the base
// EB.Missions.Entity fallback.
void* hook_78(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    char base[80], type[80];
    PROTECT(
        if(!read_str(a1,base,sizeof base)) strcpy(base,"<null>");
        if(!read_str(a2,type,sizeof type)) strcpy(type,"<null>");
        flog("NEWENTITY request base='%s' type='%s'", base, type);
    );
    void* r = H[78].orig(a0,a1,a2,a3,a4,a5,a6,a7);
    PROTECT(
        char cls[80]; cls[0]=0; uintptr_t p=(uintptr_t)r;
        if(p>=0x100000 && !(p&7)){
            uintptr_t k=*(uintptr_t*)p;
            if(k>=0x100000 && !(k&7)){
                char* n=*(char**)(k+0x10); int i=0;
                if((uintptr_t)n>=0x100000) for(;i<(int)sizeof(cls)-1;i++){
                    char c=n[i]; if(c<=0||c>=127) break; cls[i]=c;
                }
                cls[i]=0;
            }
        }
        flog("NEWENTITY result=%p class='%s'", r, cls[0]?cls:"<unknown>");
    );
    return r;
}
// ---- base-board render diagnostics (slot 79) ----
#define FORCELIGHT 1
#define MATSWAP 0
// KEYBOOST: 1 = force KEYBOOST_VAL as the _UnitIntensity of the BASE key light only.
// See the block at the end of log_tod_lighting for what it proves.
// s28 re-test: re-enabled now that BUILDINGS exist on the board. The s26 negative was
// measured against the terrain only, whose material produces no lit output regardless
// (s27) -- nothing on the board back then COULD respond to light. The building meshes
// use their own materials from buildings.assetbundle, so if they appear under boost the
// base's light/exposure combo is the residual culprit; if they stay black, the building
// materials fail the same way the terrain's does.
#define KEYBOOST 1
#define KEYBOOST_VAL 12000.0f
// Base-board visibility workarounds: BLDGACT force-activates a building GameObject the
// game itself disables (not a root-cause fix).  SHFIX injects flat-white spherical-harmonic
// ambient because baked lighting supplies none (a cosmetic workaround).  The base terrain's
// material is still a broken baked shader variant and remains black; that is known unfixed.
// SHFIX: 1 = overwrite the BASE reflection probe's spherical harmonics with a flat
// white ambient of SHFIX_VAL and re-Apply. See log_tod_lighting for the derivation.
#define SHFIX 1
// The base's exposure multiplier is ~0.084 (s26), so SHFIX_VAL 3.0 lands at ~0.25 (mid grey)
// -- enough to reveal buildings only where they silhouette against the sky, invisible against
// the black terrain. ~12 lands near white post-exposure; 15 keeps them clearly bright at any
// framing. Only the buildings' PBR material samples this ambient; the terrain variant ignores
// it (s27), so cranking it does not touch the terrain.
#define SHFIX_VAL 0.5f
// METALFIX: 1 = bind an explicit black texture to the terrain material's
// _metallic_tex, which is unassigned despite _METALLIC_TOGGLE being compiled in.
#define METALFIX 0
// LMFIX: 1 = after EBTimeOfDay.Apply runs on a MERGED time-of-day, re-bind the global
// "_lm" shader texture to Texture2D.whiteTexture.
//
// This is the one asymmetry between the two boards that no experiment has touched yet, and
// it is the only one that survives every negative result so far. Apply's tail (@0x124C110,
// disassembled) is a two-way branch on IsMerged:
//   IsMerged=1 (BASE):  if (Lightmaps != null && Lightmaps.Length == 1)
//                           Shader.SetGlobalTexture("_lm", Lightmaps[0]);
//   IsMerged=0 (STORY): t = GetCurrentLightmapType();
//                       if (Lightmaps == null || t >= Lightmaps.Length) return;   <-- taken
//                           Shader.SetGlobalTexture("_lm", Lightmaps[t]);
// The base has isMerged=1 with exactly one loaded lightmap, so it DOES publish a global _lm.
// The story bails and publishes nothing, so its shader samples the untouched default. That is
// the only lighting input the base has and the story does not.
//
// Why that predicts pure black: the terrain is not authored geometry, it is a mesh stitched
// together at runtime by GameboardBuilder from the map partitions ("terrain-stitch", per
// BASEDIAG). A runtime-generated mesh has no baked lightmap UVs, so wherever the merged
// lightmap is sampled it is sampled at meaningless coordinates -- and a lightmap multiplies
// the lit result. That single multiply-by-zero explains every dead end at once: KEYBOOST
// (x6000 direct), SHFIX (flat SH ambient) and METALFIX (diffuse restored) all feed terms
// that are downstream of it, MATSWAP escaped it by swapping to a shader that never samples
// _lm, and the story board renders precisely because it never binds one.
//
// whiteTexture is the neutral element for that multiply, so if the terrain lights up the
// lightmap binding is confirmed as the cause. Gated on IsMerged so only the base is touched.
// Applied AFTER the original: Apply is the only writer of this global in the binary, so the
// last Apply for a given ToD wins and nothing overwrites it afterwards.
//
// RESULT: inert. LMFIX fired 3x (confirmed in the log, whiteTexture bound), terrain still pure
// black. The merged lightmap is not the multiplier, and _lm is now a dead end too. Retired.
#define LMFIX 0
// THEMESWAP: 1 = on the BASE board only, repoint builder._ThemeMaterial@0x188 at the theme
// library's "qb_theme_material" entry (the one the STORY board uses) instead of the
// "qb_theme_material_base" entry it normally gets, before FixTerrain consumes it.
//
// Motivation, from disassembling GameboardBuilder.BuildInternal @0xF8B570. The theme material
// is not resolved from the theme at all -- the key is hardcoded off the board's base-ness at
// @0xF8CDBC:
//     key = (isUsersBase@0x198 || flag@0x1A8) ? "qb_theme_material_base"   // @0xF8CDD0
//                                             : "qb_theme_material";       // @0xF90840
//     _ThemeLibrary@0x148 -> PrefabLibrary.get_Contents() -> Dictionary<string,GameObject>
//     if (!ContainsKey(key)) skip;  go = dict[key];
//     go.GetComponentInChildren<Renderer>().sharedMaterial   (a local, @0xF8CF04)
//     go.GetComponent<ThemeMaterial>() -> _ThemeMaterial@0x188   (@0xF8CF1C-0xF8CF20)
// So "the base picks the wrong entry out of the theme library" is dead as a theory: the base
// deliberately asks for the _base entry and gets it. The entry it gets is the one carrying
// qb_primordial_terrainblend_base and the Hidden/PBR_...EB_COMPOSITE_RMEAO_ variant that has
// now defeated four separate forcing tests.
//
// What is still worth knowing is whether the plain "qb_theme_material" entry -- the one whose
// material (Hidden/PBRTerrain_BASE_EB_EMISSIVE_NO_PULSE, keywordCount=0) demonstrably renders
// on the story board -- is reachable from the BASE board's library. It may not be: the base
// loads its library from themeFull='primordial_base' while the story loads 'primordial', so
// these are two different PrefabLibrary assets. ContainsKey answers that directly, and the
// answer is worth logging even if the swap cannot happen.
//
// The lookup is reproduced exactly as the call site does it, including taking each gshared
// MethodInfo* from the specific GOT slot that call site uses (two derefs):
//   PrefabLibrary.get_Contents 0xE3B4D4 (null mi)
//   Dictionary.ContainsKey     0x1FFD7C8, mi GOT 0x2C38CC0
//   Dictionary.get_Item        0x1FFD490, mi GOT 0x2C24188
//   GameObject.GetComponent<T> 0x11E5734, mi GOT 0x2BCF8B8
//
// RESULT: the swap is impossible, and that is itself the finding.
//   THEMESWAP lib=0x7600ffa50d80 contents=0x7600ffb06300 hasBaseKey=1 hasStoryKey=0
// The base board's library (themeFull='primordial_base') contains ONLY the _base entry. The
// story's working terrain material is not reachable from the base board at all, so there is
// no in-game asset to substitute and no lookup left to correct. Retired.
#define THEMESWAP 0
// KWFIX: 1 = DisableKeyword("EB_PLANAR_REFLECTION_ON") on the live base terrain material.
//
// The last untested difference in the OUTPUT path. Base and story terrain materials differ in
// their shader family, and of the base's eight keywords EB_PLANAR_REFLECTION_ON is the only
// one that is both absent from the story material and about producing colour rather than
// consuming a texture. Earlier sessions exonerated planar reflection by showing the manager
// runs (PRCONFIG/PRSETUP/PRINIT/PRRENDER all fire 6x at Quality=High and _Color3@0x60 is
// non-null afterwards) -- but that only proves the reflection is RENDERED, not that the
// terrain samples it. Neither _planar_reflection_tex nor _reflection_tex exists on the
// material (has=0 for both), so whatever it samples has to arrive as a global, and nothing
// has ever confirmed that global is bound under the name this variant expects. A reflection
// term that resolves to an unbound sampler is a plausible way to zero the shader's output
// while leaving every lighting input we have forced completely irrelevant -- which is exactly
// the pattern of KEYBOOST, SHFIX, METALFIX and LMFIX all landing on nothing.
//
// EB_PLANAR_REFLECTION_ON is a real runtime keyword (shaderKeywords[0]), so it can simply be
// switched off: Material.DisableKeyword @0x1B56C4C. Caveat: the shader is a pre-generated
// Hidden/PBR_ uber variant whose name bakes the whole keyword set in, so dropping a keyword
// may select a variant that was never generated. That failure mode is informative too -- it
// shows up as magenta, which earlier sessions established is what a missing material looks
// like in this build, and magenta would prove the keyword set is live rather than frozen.
//
// RESULT: inert. The keyword set IS live and mutable (keywordCount went 8 -> 7 on both
// partitions, and no magenta), so the variant is not frozen -- but the terrain is unchanged.
// Measured over the board region, pan_lm vs pan_kw: mean 0.423 vs 0.434 / 255, and over the
// terrain silhouette specifically 3 vs 4 pixels above threshold with an identical (57,72,77)
// maximum in BOTH. That is noise. Planar reflection stays exonerated. Retired.
#define KWFIX 0
// TEXFIX: 1 = assign every UNASSIGNED texture slot on the base terrain material an explicit
// neutral texture, each chosen to be the identity for what that map scales.
//
// METALFIX tested this idea but only one quarter of it. The probe output lists FOUR slots that
// the variant compiles in and the material never fills -- _metallic_tex, _roughness_tex,
// _ao_tex and _emissive_tex (all has=1, tex=0x0) -- and METALFIX bound only _metallic_tex.
// Fixing metallic alone cannot show a result if any of the others is independently zeroing the
// output, so a negative METALFIX never actually cleared the theory.
//
// _ao_tex is the one that matters. An unassigned texture samples the shader's declared default,
// and a "black" default for an ambient-occlusion map means AO = 0, i.e. fully occluded. AO
// multiplies the lit result, so it zeroes direct and indirect light alike -- which is precisely
// why KEYBOOST (x6000 direct), SHFIX (flat SH ambient) and LMFIX (white lightmap) could each be
// applied successfully and still change nothing: every one of them feeds a term downstream of
// this multiply. The _AO_NONE keyword argues AO should be disabled, but EB_COMPOSITE_RMEAO is
// compiled into the same variant and the composite path packs AO alongside roughness/metallic,
// so the two can disagree and the composite sampler can win.
//
// Neutral value per slot -- these are NOT interchangeable, and getting metallic backwards is
// what makes a surface black:
//   _ao_tex        <- white   AO = 1, no occlusion
//   _roughness_tex <- white   roughness = 1, fully diffuse (safe; a smooth mirror would need
//                             the specular cubemap the base does not ship)
//   _metallic_tex  <- BLACK   metallic = 0, full diffuse term (white here = pure metal = black)
//   _emissive_tex  <- white   doubles as a positive control: if the shader can emit at all the
//                             terrain glows, proving the variant produces output and isolating
//                             the fault to the lighting path. _EMISSIVE_NONE is compiled in, so
//                             no glow is the expected reading, not a failure.
//
// RESULT: inert. All eight assignments succeeded (logged, both partitions) and the board region
// measured 0.423 mean / 0.56% non-black -- bit-identical to the baseline. AO is not the
// multiplier, and METALFIX's negative result is now properly cleared rather than confounded.
#define TEXFIX 0
// File scope: a braced initialiser's commas would be read as extra arguments to PROTECT(),
// which takes exactly one. Same rule that put V3/COL4 out here.
static const char* const TF_NAME[4] =
    { "_ao_tex", "_roughness_tex", "_metallic_tex", "_emissive_tex" };
// 1 = bind whiteTexture, 0 = bind blackTexture. Only _metallic_tex wants black.
static const int TF_WHITE[4] = { 1, 1, 0, 1 };
// NORMFIX: 1 = DisableKeyword("_NORMAL_TEX") on the live base terrain material, so the shader
// shades with the interpolated VERTEX normal instead of the tangent-space normal map.
//
// This is the one hypothesis that explains all six inert forcing tests without blaming the
// shader. The terrain is not authored geometry -- GameboardBuilder stitches it at runtime from
// the map partitions (both partitions log go='terrain-stitch'). Normal mapping needs per-vertex
// TANGENTS to build the TBN basis, and a procedurally merged mesh very plausibly has none. With
// no tangents the decoded normal degenerates, N.L collapses to zero for every light, and the
// surface renders black no matter what is fed to it -- which is exactly the pattern: KEYBOOST,
// SHFIX, LMFIX, METALFIX, KWFIX and TEXFIX each successfully changed an input that is
// multiplied by a normal-dependent term downstream. It also explains why MATSWAP was the only
// thing that ever produced pixels: EB/Particle/Uber/Base is unlit and never touches the normal.
//
// Dropping the keyword makes the shader skip the normal map entirely and use the vertex normal,
// which a stitched mesh does carry. If the terrain lights up, the fault is the mesh's tangents,
// not the material -- a completely different (and potentially fixable) place to work.
// Precedent that this is safe: KWFIX established that keywords on this material are live and
// mutable (8 -> 7) and that dropping one does not fall through to a missing variant/magenta.
//
// RESULT: inert, run together with TEXFIX. Keyword dropped on both partitions (8 -> 7),
// measured 0.422 mean / 0.56% non-black against a 0.423 / 0.56% baseline. So the surface is
// black even when shaded by the vertex normal with neutral AO/roughness/metallic -- missing
// tangents are not the explanation either, and the mesh is exonerated along with the mesh-side
// fix that would have followed from it.
#define NORMFIX 0
// CAMFRAME (session 28): the base camera's pos-offset is baked on the scene's
// BaseCameraController for large retail bases (~(-60,160,-370) at the default zoom), which
// parks our small authored board (buildings within +/-20 of origin) at the far horizon,
// small and half-occluded. No server key touches it -- overrideZoom is read only by the dead
// Legacy.QuestMap path, never by EB.Missions. get__CurrentPosOffset (@0x121C634, target mode
// 0) returns lerp(near@0xb8, far@0xa0, clamp01(zoom@0x218)); forcing near==far==(CAMX,CAMY,
// CAMZ) yields a fixed close, steeper offset regardless of the player's pinch-zoom. Cosmetic
// framing only, base board only (BaseCameraController is the base's own camera component).
// CAMFRAME retired (0): every attempt to reframe the base camera made the buildings WORSE, and
// the failure was consistent and instructive. The baked far offset (-60,160,-370, fov 40) is the
// ONLY sightline that clears the black terrain hump and shows the buildings silhouetted against
// the SKY at the horizon. Moving the camera closer (posOffset override) dropped the buildings
// into the black-terrain foreground where they are occluded; a telephoto FOV (0x1a0/0x1a4 -> 15)
// magnified the horizon but recentred the frame on the terrain, again occluding them (verified:
// building name-label UI renders at frame centre but the 3D meshes are behind the terrain, center
// region max pixel (58,74,78) = bare sky tint). The terrain-stitch mesh is a solid occluder from
// any lower/closer/narrower angle. So the camera is left at its baked default; the buildings show
// against the sky exactly as the far view frames them, and SHFIX brightness is what makes them
// read. The fov-field poke lives in hook_94 behind this switch for future experiments.
#define CAMFRAME 0
#define CAMFOV 15.0f
// BLDGSWAP: LoadBuildingObject first asks for an authored z_bldg_* asset, fails, and its
// fallback creates a DefaultRelic anchor. hook_92 remembers those authored ids FIFO; here the
// DefaultRelic calls consume them and put the matching PrefabLibrary child under the returned
// anchor, hiding the rock renderer. A later refresh calls SpawnBuilding with the real id but
// still gets no standalone asset: in that NULL-return phase this hook creates, parents and
// returns the library clone itself so OnBuildingSet receives a real GameObject.
#define BLDGSWAP 1
// BLDGSCALE: presentation-only magnification of the swapped-in base-building clones.
// The authored/base-game scale leaves the buildings ~415 world units from the baked
// base camera, i.e. a few pixels tall. This is a COSMETIC framing workaround, not a
// fidelity fix: it makes the base readable at the default framing without moving the
// camera (camera reframing is a closed dead end -- it occludes the buildings behind
// the terrain stitch hump).
#define BLDGSCALE     1
#define BLDGSCALE_VAL 1.5f
// BLDGDIAG records the hierarchy and renderer bounds at Refresh to distinguish an
// inactive/empty/off-frame building from one that is merely too small or dark.
// BLDGACT forces the building plus any inactive ancestors active; BLDGPROBE moves
// the first building roughly 100 units in front of the base camera as a draw probe.
#define BLDGDIAG  1
#define BLDGACT   1
#define BLDGPROBE 0
// BLDGLEAVE undoes the cosmetic force-activation/rescaling workaround when BaseBoard leaves.
// Those objects live under AssetManager rather than the normal board root, so without this
// matching teardown they survive into STORY even though the game correctly hides its own board.
#define BLDGLEAVE 1
// SHADERSWAP is retired: it called Material.set_shader on GameboardBuilder._ThemeMaterial,
// which is a ThemeMaterial MonoBehaviour and not a Material at all (see the correction note
// in hook_79). It could never have worked, and the reading that motivated it was garbage.
// File scope: PROTECT(...) takes one macro argument, so the commas in the member list would
// otherwise be read as extra arguments. A 3-float struct is an AAPCS64 HFA, so Unity's
// Transform.get_position returns it in s0/s1/s2 and a plain C struct return matches.
typedef struct { float x, y, z; } V3;
typedef struct { float cx, cy, cz, ex, ey, ez; } Bounds;
// Same reason, and the same HFA rule: a 4-float struct comes back in s0-s3, matching
// UnityEngine.Color as returned by Material.get_color.
typedef struct { float r, g, b, a; } COL4;
// Texture-property names for the EB PBR shader family. The first pass guessed Unity's
// standard CamelCase names (_MainTex, _BumpMap, ...) and every one of them missed on BOTH the
// black base material and the working story material, which looked like "the probe carries no
// signal". It does -- the API works (Unity itself logs "doesn't have a texture property
// '_MainTex'" for the base material) and EB simply names its properties in lower_snake_case.
// The real names came out of the bundles: `strings primordial_base.assetbundle | grep -oE
// '_[a-z][a-z0-9_]*(tex|map|reflect)[a-z0-9_]*'` yields _base_tex / _normal_tex / _ao_tex /
// _emissive_tex / _base2_tex, and the shader-variant keyword list is spelled the same way.
// File scope because a braced initialiser inside PROTECT(...) parses as extra macro arguments.
static const char* const TEXPROPS[] = {
    "_base_tex", "_base2_tex", "_normal_tex", "_ao_tex", "_emissive_tex",
    "_roughness_tex", "_rmeao_tex", "_composite_rmeao_tex", "_metallic_tex",
    "_lightmap", "_lm", "_planar_reflection_tex", "_reflection_tex"
};
// The base terrain material turned out to be healthy -- _base_tex resolves to a loaded
// 'qb_primordial_ground_moss' with a live native handle -- so a missing asset is not the
// reason it shades black. What is left inside the material is its scalars: the shader keeps
// _METALLIC_TOGGLE and _ROUGHNESS_TEX enabled while _roughness_tex/_metallic_tex are both
// null, and a metallic surface with zero roughness is a mirror that reflects the base board's
// otherwise-empty scene, i.e. black, no matter how good the albedo is. These names come from
// the same bundle-strings sweep as TEXPROPS.
static const char* const FLOATPROPS[] = {
    "_metallic", "_roughness", "_roughness_range", "_reflectance", "_reflectance_range",
    "_ao", "_alpha", "_emissive_intensity", "_intensity"
};
static const char* const COLORPROPS[] = {
    "_color", "_base_color", "_tint", "_base_tint", "_emissive_color", "_col"
};
// Safe field readers: every base-board field we want lives at a fixed offset off a managed
// object, so read them directly (under PROTECT) rather than calling back into il2cpp.
static int obj_ok(void* p){ return (uintptr_t)p >= 0x100000 && !((uintptr_t)p & 7); }
static void* fld_p(void* o, int off){ return obj_ok(o) ? *(void**)((uintptr_t)o + off) : NULL; }
static float fld_f(void* o, int off){ return obj_ok(o) ? *(float*)((uintptr_t)o + off) : -999.0f; }
// System.Collections.Generic.List<T>._size lives at 0x18; -1 marks "list itself was null".
static int list_count(void* l){ return obj_ok(l) ? *(int32_t*)((uintptr_t)l + 0x18) : -1; }
// Vector3 fields are 3 packed floats stored inline in the object.
static void fld_v3(void* o, int off, float* v){
    v[0]=v[1]=v[2]=0.0f;
    if (obj_ok(o)) memcpy(v, (void*)((uintptr_t)o + off), 12);
}
// Il2CppObject -> klass -> name. Lets a raw pointer be sanity-checked before its fields are
// believed (a wrong static-field offset yields a plausible-looking but meaningless pointer).
static void obj_class(void* o, char* buf, int cap){
    buf[0]=0;
    if(!obj_ok(o)) { strcpy(buf,"<null>"); return; }
    uintptr_t k=*(uintptr_t*)o;
    if(!obj_ok((void*)k)) { strcpy(buf,"<badklass>"); return; }
    char* n=*(char**)(k+0x10); int i=0;
    if((uintptr_t)n < 0x100000) { strcpy(buf,"<noname>"); return; }
    for(;i<cap-1;i++){ char c=n[i]; if(c<=0||c>=127) break; buf[i]=c; }
    buf[i]=0;
}
// Dump the active EBTimeOfDay's lighting state. This lives in a helper because it has to run
// on BOTH boards: everything the base terrain needs in order not to be black has now been
// verified good on the base side in isolation (key light 2.5 luminous, lightmap loaded,
// material textured, shader compiling, planar reflection producing a target), so the only way
// to make progress is to read the same numbers on the STORY board -- the one that renders --
// and diff them. BASEDIAG is base-only, but FixTerrain runs on both, so call it from there.
// EBTimeOfDayManager._this is reached the same way BASEDIAG does it: the GOT slot holds the
// address of the TypeInfo global, whose value is the Il2CppClass*, static_fields at 0xB8.
// EBTimeOfDay (dump.cs:374242): IsMerged@0x10 Name@0x18 RenderSettings@0x20
// LightmapSettings@0x28 SkyBox@0x30 ReflectionProbe@0x38 KeyLight@0x48 LightObjects@0x50
// MergedLights@0x58 CubeMapRule@0x68 CubeMapIndex@0x6C Lightmaps@0x70.
// EBReflectionProbe (dump.cs:370440): Size@0x18 Textures@0x28 Color@0x30 IEM@0x40 PMREM@0x48
// SH@0x50 IEM8Bit@0x98 PMREM8Bit@0xA0.
static void log_tod_lighting(const char* tag){
    void* todgot   = *(void**)(g_base + 0x2BDB680);
    void* todinst  = fld_p(fld_p(fld_p(todgot, 0x0), 0xb8), 0x0);
    void* todarr   = fld_p(todinst, 0x18);
    void* tod0     = obj_ok(todarr) ? *(void**)((uintptr_t)todarr + 0x20) : NULL;
    void* (*obj_get_name0)(void*,void*) = (void*(*)(void*,void*))(g_base + 0x16A16A0);
    float (*tod_keylight_lum)(void*,void*) = (float(*)(void*,void*))(g_base + 0x124C1D4);
    char todnm[80]; if(!read_str(fld_p(tod0,0x18), todnm, sizeof todnm)) strcpy(todnm,"<null>");
    void* refl  = fld_p(tod0, 0x38);
    void* lmaps = fld_p(tod0, 0x70);
    float rc[3]; fld_v3(refl, 0x30, rc);
    flog("%s TOD name='%s' isMerged=%d keyLightLuminous=%.4f cubeMapRule=%d cubeMapIndex=%d "
         "rs=%p lmSettings=%p skybox=%p mergedLights=%p",
         tag, todnm, obj_ok(tod0) ? *(uint8_t*)((uintptr_t)tod0+0x10) : -1,
         obj_ok(tod0) ? tod_keylight_lum(tod0, NULL) : -999.0f,
         obj_ok(tod0) ? *(int32_t*)((uintptr_t)tod0+0x68) : -999,
         obj_ok(tod0) ? *(int32_t*)((uintptr_t)tod0+0x6C) : -999,
         fld_p(tod0,0x20), fld_p(tod0,0x28), fld_p(tod0,0x30), fld_p(tod0,0x58));
    flog("%s TOD refl=%p size=%d textures=%p texLen=%d color=(%.3f,%.3f,%.3f) "
         "IEM=%p PMREM=%p SH=%p IEM8=%p PMREM8=%p",
         tag, refl,
         obj_ok(refl) ? *(int32_t*)((uintptr_t)refl+0x18) : -999,
         fld_p(refl,0x28),
         obj_ok(fld_p(refl,0x28)) ? (int)*(int32_t*)((uintptr_t)fld_p(refl,0x28)+0x18) : -1,
         rc[0], rc[1], rc[2],
         fld_p(refl,0x40), fld_p(refl,0x48), fld_p(refl,0x50),
         fld_p(refl,0x98), fld_p(refl,0xA0));
    for (int i = 0; obj_ok(lmaps) && i < *(int32_t*)((uintptr_t)lmaps+0x18) && i < 4; i++) {
        void* lmt = *(void**)((uintptr_t)lmaps + 0x20 + 8*i);
        char lmn2[80];
        if(!read_str(obj_ok(lmt)?obj_get_name0(lmt,NULL):NULL, lmn2, sizeof lmn2))
            strcpy(lmn2,"<null>");
        flog("%s TOD lightmap[%d]=%p native=%p '%s'", tag, i, lmt, fld_p(lmt,0x10), lmn2);
    }
    // EB lights are physical: EBLight stores a _UnitIntensity in some eUNIT and the shaders
    // divide the result by a photographic exposure built from the active render settings'
    // Aperture/ShutterTime/ISO. The base key light measures 2.5 against the story's 12000 --
    // 4800x dimmer -- so the question is whether the base's own render settings compensate.
    // If they carry the same exposure as the story's, the base terrain is simply ~4800x
    // underexposed, which is black no matter how correct the material is.
    // EBRenderSettingsBase (dump.cs:374681): Aperture@0x20 ShutterTime@0x24 ISO@0x28
    // DirectionalLightValueShift@0x5C SkyLuminousScale@0xE0 EmissiveOffset@0xF8
    // UseNativeExposure@0xFC.
    void* rs = fld_p(tod0, 0x20);
    char rscls[80]; obj_class(rs, rscls, sizeof rscls);
    float (*rs_get_ev100)(void*,void*) = (float(*)(void*,void*))(g_base + 0x18C27B8);
    flog("%s TOD rs=%p class='%s' aperture=%.4f shutter=%.6f iso=%.1f dirLightShift=%.4f "
         "skyLumScale=%.4f emissiveOffset=%.4f useNativeExposure=%d",
         tag, rs, rscls, fld_f(rs,0x20), fld_f(rs,0x24), fld_f(rs,0x28),
         fld_f(rs,0x5C), fld_f(rs,0xE0), fld_f(rs,0xF8),
         obj_ok(rs) ? *(uint8_t*)((uintptr_t)rs+0xFC) : -1);
    flog("%s TOD ev100=%.4f", tag, obj_ok(rs) ? rs_get_ev100(rs, NULL) : -999.0f);
    // And how the key light itself is authored. GetComponent<EBLight> reuses the exact GOT
    // slot GetKeyLightLuminousIntensity loads at 0x124C258, with the same two dereferences a
    // _TypeInfo needs -- a single deref takes the process down.
    // EBLight (dump.cs:370649): Type@0x18 InputType@0x38 _UnitIntensity@0x80
    // LuminousEfficacy@0x84 _Color@0xD4 IsBaked@0x138.
    void* kl = fld_p(tod0, 0x48);
    void* (*go_getcomp)(void*,void*) = (void*(*)(void*,void*))(g_base + 0x11E5734);
    void* glmi = fld_p(*(void**)(g_base + 0x2BE1320), 0x0);
    void* ebl  = (obj_ok(kl) && obj_ok(glmi)) ? go_getcomp(kl, glmi) : NULL;
    char eblcls[80]; obj_class(ebl, eblcls, sizeof eblcls);
    float klc[3]; fld_v3(ebl, 0xD4, klc);
    flog("%s TOD keyLightGO=%p ebLight=%p class='%s' type=%d inputUnit=%d unitIntensity=%.4f "
         "efficacy=%.4f color=(%.3f,%.3f,%.3f) isBaked=%d",
         tag, kl, ebl, eblcls,
         obj_ok(ebl) ? *(int32_t*)((uintptr_t)ebl+0x18) : -999,
         obj_ok(ebl) ? *(int32_t*)((uintptr_t)ebl+0x38) : -999,
         fld_f(ebl,0x80), fld_f(ebl,0x84), klc[0], klc[1], klc[2],
         obj_ok(ebl) ? *(uint8_t*)((uintptr_t)ebl+0x138) : -1);
    // KEYBOOST experiment. The measured numbers say the base is NOT underexposed: its
    // EV100 is 3.31 against the story's 15.87, i.e. 12.56 stops = 6047x more exposure,
    // which more than cancels the 4800x weaker key light (2.5 vs 12000 luminous). Net
    // post-exposure direct sunlight on the base is 1.26x the story's, so "the base is
    // too dark" cannot be the whole answer -- unless the directional term never reaches
    // the terrain shader at all.
    // That is exactly what this forces. EBLight drives lighting purely through global
    // shader properties (there is no UnityEngine.Light anywhere on it); InternalUpdate
    // @0x1D385E4 pushes _EBDirectionalLightDirection/-LuminanceIntensity, gated at
    // @0x1D38688 by `if (IsBaked && !force) return`. The base key light has IsBaked=0,
    // so LateUpdate re-pushes it EVERY frame -- writing _UnitIntensity@0x80 is therefore
    // picked up on the next frame with no Apply/SetDirty needed.
    // Reading: 12000 under the base's own 6047x exposure is a ~6000x overexposure. If the
    // directional term reaches the terrain the board must saturate to white. If it stays
    // black, direct light is not reaching this material at all and the missing ambient
    // cubemaps (IEM8Bit/PMREM8Bit null, see EBReflectionProbe.Apply @0x18C170C) are the
    // entire story. Deliberately a binary readout, because each run costs ~6 minutes.
    // RESULT: still pure black at ~6000x overexposure. The directional term does not reach
    // the terrain, so KEYBOOST is retired (left at 0) and ambient is the remaining suspect.
#if KEYBOOST
    if (obj_ok(ebl) && tag[3]=='B') {          // FT-BASE only, never FT-STORY
        float before = fld_f(ebl, 0x80);
        *(float*)((uintptr_t)ebl + 0x80) = KEYBOOST_VAL;
        flog("%s KEYBOOST ebLight=%p unitIntensity %.4f -> %.4f", tag, ebl, before,
             fld_f(ebl, 0x80));
    }
#endif
    // SHFIX. The ambient story is finer-grained than "the base has no indirect light".
    // EBReflectionProbe.Apply @0x18C170C has TWO independent ambient sources:
    //   * cubemaps -- IEM/PMREM, overridden by IEM8Bit@0x98/PMREM8Bit@0xA0 when
    //     GetCurrentLightmapType() is Forward(1) or Baked(2). All four are null on the base
    //     (base_merged.assetbundle ships no Cubemap at all, while primordial_merged ships
    //     PBR-IEM-*-8bit / PBR-PMREM-*-8bit), so @0x18C1904 branches to the null path, which
    //     does not merely skip the bind -- it explicitly binds a ZERO ambient.
    //   * spherical harmonics -- SH@0x50, read at @0x18C1BEC and pushed out as the seven
    //     Property__SHAr..SHC globals. This one IS non-null on the base.
    // So the base does have a live ambient path, and the only question left is whether its
    // coefficients carry any energy. Dump them, then (SHFIX) overwrite with a flat white
    // ambient and re-Apply. Unity packs SH so that a constant term C is just SHAr/g/b.w = C
    // with the linear/quadratic bands zero -- the shader evaluates dot(SHAr, float4(N,1)).
    // SHFIX_VAL is not arbitrary: the base's own RenderSettings.SkyLuminousScale is 3.0, and
    // its exposure multiplier is 0.084, so 3.0 lands at ~0.25 post-exposure -- a mid grey,
    // the same order as the story's sky (0.555). EBSphericalHarmonics (dump.cs:369406):
    // SHAr@0x10 SHAg@0x20 SHAb@0x30 SHBr@0x40 SHBg@0x50 SHBb@0x60 SHC@0x70, all Vector4.
    void* sh = fld_p(refl, 0x50);
    if (obj_ok(sh)) {
        static const int SHOFF[7] = { 0x10,0x20,0x30,0x40,0x50,0x60,0x70 };
        static const char* SHNM[7] = { "SHAr","SHAg","SHAb","SHBr","SHBg","SHBb","SHC" };
        for (int i = 0; i < 7; i++)
            flog("%s TOD sh %s=(%.4f,%.4f,%.4f,%.4f)", tag, SHNM[i],
                 fld_f(sh, SHOFF[i]),     fld_f(sh, SHOFF[i]+4),
                 fld_f(sh, SHOFF[i]+8),   fld_f(sh, SHOFF[i]+12));
    }
#if SHFIX
    if (obj_ok(sh) && obj_ok(refl) && tag[3]=='B') {   // FT-BASE only, never FT-STORY
        for (int i = 0x10; i <= 0x70; i += 0x10)       // zero every band first
            for (int j = 0; j < 16; j += 4) *(float*)((uintptr_t)sh + i + j) = 0.0f;
        *(float*)((uintptr_t)sh + 0x10 + 12) = SHFIX_VAL;   // SHAr.w  (red   DC)
        *(float*)((uintptr_t)sh + 0x20 + 12) = SHFIX_VAL;   // SHAg.w  (green DC)
        *(float*)((uintptr_t)sh + 0x30 + 12) = SHFIX_VAL;   // SHAb.w  (blue  DC)
        // Push it. TODAPPLY fires once more for Day_Base after FixTerrain, so this would
        // eventually land anyway, but calling Apply directly removes the ordering guess.
        void (*probe_apply)(void*,void*) = (void(*)(void*,void*))(g_base + 0x18C170C);
        probe_apply(refl, NULL);
        flog("%s SHFIX sh=%p flat ambient DC=%.4f applied", tag, sh, (double)SHFIX_VAL);
    }
#endif
}
// slot 79 BASEDIAG: BaseBoard.OnBaseBoardBuildComplete(this=a0). Runs after the board is fully
// built, so dump the finished state (see the H-table comment for why each field matters).
void* hook_79(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    void* r = H[79].orig(a0,a1,a2,a3,a4,a5,a6,a7);
    PROTECT(
        void* bld = fld_p(a0, 0x30);          // BaseBoard._builder
        void* cam = fld_p(a0, 0x68);          // BaseBoard._camera
        char todname[80]; char todpath[200]; char theme[80]; char themefull[80];
        if(!read_str(fld_p(bld,0x38), todname,  sizeof todname))  strcpy(todname,  "<null>");
        if(!read_str(fld_p(bld,0x40), todpath,  sizeof todpath))  strcpy(todpath,  "<null>");
        if(!read_str(fld_p(bld,0x178),theme,    sizeof theme))    strcpy(theme,    "<null>");
        if(!read_str(fld_p(bld,0x180),themefull,sizeof themefull))strcpy(themefull,"<null>");
        flog("==BASEDIAG== board=%p builder=%p root=%p aq=%p state=%d",
             a0, bld, fld_p(a0,0x38), fld_p(a0,0x40),
             obj_ok(a0) ? *(int32_t*)((uintptr_t)a0+0x28) : -1);
        flog("BASEDIAG tod=%p todName='%s' todPath='%s'", fld_p(bld,0x30), todname, todpath);
        flog("BASEDIAG theme='%s' themeFull='%s' themeLib=%p commonLib=%p bldgLib=%p themeMat=%p",
             theme, themefull, fld_p(bld,0x148), fld_p(bld,0x150), fld_p(bld,0x158), fld_p(bld,0x188));
        flog("BASEDIAG terrainParts=%d goParts=%d pathParts=%d lights=%d builderNodes=%d "
             "baseNodes=%d map=%p pathMat=%p isUsersBase=%d",
             list_count(fld_p(bld,0x48)), list_count(fld_p(bld,0x50)), list_count(fld_p(bld,0x58)),
             list_count(fld_p(bld,0x128)), list_count(fld_p(bld,0x130)), list_count(fld_p(bld,0x20)),
             fld_p(bld,0x160), fld_p(bld,0x60),
             obj_ok(bld) ? *(uint8_t*)((uintptr_t)bld+0x198) : -1);
        flog("BASEDIAG boardNodes=%d boardBuildings=%d camera=%p",
             list_count(fld_p(a0,0x58)), list_count(fld_p(a0,0x60)), cam);
        float t[3]; float la[3]; float po[3]; float st[3];
        fld_v3(cam, 0x94,  t);    // BaseCameraController.Target
        fld_v3(cam, 0x204, la);   // _LookAtTarget
        fld_v3(cam, 0xA0,  po);   // PosOffset
        fld_v3(cam, 0x1F8, st);   // _StartingTarget
        // EBTimeOfDayManager._this (static @0x0). OnBaseBoardBuildComplete bails to its tail
        // call the moment get_Instance() returns null (cbz x0 @0xA6EE08), which SKIPS
        // BaseRenderSettings.set_IsBase(true), InitFX and the base ambience -- i.e. the
        // instantiated ToD prefab would never get applied to RenderSettings and everything
        // lit would render black. Read the static directly to settle it.
        // Two derefs: (g_base+slotVA) is a GOT entry holding the ADDRESS of the
        // EBTimeOfDayManager_TypeInfo global, whose value is the Il2CppClass*. That is exactly
        // the `ldr x8,[got]; ldr x9,[x8]; ldr x9,[x9,#0xb8]` pattern the game's own cctors use.
        void* todgot   = *(void**)(g_base + 0x2BDB680);      // EBTimeOfDayManager_TypeInfo GOT
        void* todklass = fld_p(todgot, 0x0);                 // Il2CppClass*
        void* todstat  = fld_p(todklass, 0xb8);              // Il2CppClass.static_fields
        void* todinst  = fld_p(todstat, 0x0);                // EBTimeOfDayManager._this
        // GetActiveTimeOfDay (@0x18C33B0) is literally
        //   TimeOfDays==null || ActiveTimeOfDay>=TimeOfDays.Length ? null : TimeOfDays[i]
        // (length @0x18, data @0x20), and OnBaseBoardBuildComplete SKIPS
        // BaseRenderSettings.IsBase=true when it returns null (cbz @0xA6EE14) -- which would
        // leave the base with no applied render settings, i.e. black. Print the class name too,
        // so a bogus pointer can't be mistaken for a real manager.
        void* todarr = fld_p(todinst, 0x18);
        char todcls[80]; obj_class(todinst, todcls, sizeof todcls);
        flog("BASEDIAG todMgrInstance=%p class='%s' activeToD=%d todArray=%p todArrayLen=%d rsMgr=%p",
             todinst, todcls, obj_ok(todinst) ? *(int32_t*)((uintptr_t)todinst+0x20) : -1,
             todarr, obj_ok(todarr) ? (int)*(int32_t*)((uintptr_t)todarr+0x18) : -1,
             fld_p(todinst, 0x28));
        // The active EBTimeOfDay entry owns everything that LIGHTS the scene: KeyLight (the
        // sun), LightObjects[]/MergedLights, the SkyBox, the baked Lightmaps[] and the
        // reflection probe. Fog and exposure are both ruled out and the terrain silhouettes
        // against the sky, so an absent KeyLight / empty MergedLights is the remaining way to
        // get geometry that renders but shades black. Array data starts at 0x20, length 0x18.
        void* tod0 = obj_ok(todarr) ? *(void**)((uintptr_t)todarr + 0x20) : NULL;
        char todnm[80]; if(!read_str(fld_p(tod0,0x18), todnm, sizeof todnm)) strcpy(todnm,"<null>");
        void* lightObjs = fld_p(tod0, 0x50);
        void* lmaps     = fld_p(tod0, 0x70);
        void* gos       = fld_p(tod0, 0x60);
        flog("BASEDIAG tod0=%p name='%s' isMerged=%d id=%d rs=%p lmSettings=%p skybox=%p refl=%p",
             tod0, todnm, obj_ok(tod0) ? *(uint8_t*)((uintptr_t)tod0+0x10) : -1,
             obj_ok(tod0) ? *(int32_t*)((uintptr_t)tod0+0x78) : -1,
             fld_p(tod0,0x20), fld_p(tod0,0x28), fld_p(tod0,0x30), fld_p(tod0,0x38));
        flog("BASEDIAG tod0 keyLight=%p lightObjs=%p lightObjsLen=%d mergedLights=%p "
             "gameObjs=%p gameObjsLen=%d lightmaps=%p lightmapsLen=%d",
             fld_p(tod0,0x48), lightObjs,
             obj_ok(lightObjs) ? (int)*(int32_t*)((uintptr_t)lightObjs+0x18) : -1,
             fld_p(tod0,0x58), gos,
             obj_ok(gos) ? (int)*(int32_t*)((uintptr_t)gos+0x18) : -1,
             lmaps, obj_ok(lmaps) ? (int)*(int32_t*)((uintptr_t)lmaps+0x18) : -1);
        // Same lighting dump FixTerrain makes on both boards, so the base numbers stay in
        // BASEDIAG where the rest of the base-board state is.
        log_tod_lighting("BASEDIAG");
        // Everything upstream of the actual draw is now ruled out (fog, exposure, ToD Apply,
        // KeyLight, lightmap binding, camera framing as *derived*). So inspect the draw itself:
        // the theme/path material and their shaders, and -- crucially -- the terrain
        // partitions' REAL world positions and active state. The camera framing argument was
        // derived from MapCenter/TileToWorldPos arithmetic and never checked against a live
        // transform; if the terrain is not actually sitting on the origin, the board is simply
        // off-screen and the "silhouette" is something else entirely.
        // Unity accessors resolved from script.json (RVAs, same convention as the H table).
        void* (*obj_get_name)(void*,void*)    = (void*(*)(void*,void*))(g_base + 0x16A16A0);
        void* (*mat_get_shader)(void*,void*)  = (void*(*)(void*,void*))(g_base + 0x1B56338);
        int   (*go_active)(void*,void*)       = (int(*)(void*,void*))(g_base + 0x1B50D38);
        // GameObject.get_transform, NOT Component.get_transform -- GameObject does not derive
        // from Component, and calling the Component accessor on one faults (it did: PROTECT
        // swallowed the rest of this block on the previous run).
        void* (*go_transform)(void*,void*)    = (void*(*)(void*,void*))(g_base + 0x1B50BD8);
        V3 (*tr_get_pos)(void*,void*)         = (V3(*)(void*,void*))(g_base + 0x16B7C04);
        void* (*rend_get_shared_mat)(void*,void*) =
            (void*(*)(void*,void*))(g_base + 0x16ADC78);
        // Session 25 CORRECTION. The previous session read GameboardBuilder@0x188 as a
        // UnityEngine.Material and called Material.get_shader on it; dump.cs:552190 says the
        // field is `private ThemeMaterial _ThemeMaterial` and ThemeMaterial is a MonoBehaviour
        // (dump.cs:420240), NOT a Material. get_shader is an icall that blindly dereferences
        // the m_CachedPtr of whatever it is handed, so it returned garbage -- which is where
        // the bogus 'Hidden/InternalErrorShader' reading came from, and why the SHADERSWAP
        // experiment (Material.set_shader on a MonoBehaviour) always faulted. The whole
        // "base theme shader failed to resolve" lead is void. Only _PathMaterial@0x60 is a
        // real Material, and its 'EB/Particle/Uber/Base' reading was always genuine.
        void* pmat = fld_p(bld, 0x60);
        void* tmat = fld_p(bld, 0x188);
        char tmn[80]; char pmn[80]; char psn[80];
        if(!read_str(obj_ok(tmat)?obj_get_name(tmat,NULL):NULL, tmn, sizeof tmn)) strcpy(tmn,"<null>");
        if(!read_str(obj_ok(pmat)?obj_get_name(pmat,NULL):NULL, pmn, sizeof pmn)) strcpy(pmn,"<null>");
        void* psh = obj_ok(pmat) ? mat_get_shader(pmat,NULL) : NULL;
        if(!read_str(obj_ok(psh)?obj_get_name(psh,NULL):NULL, psn, sizeof psn)) strcpy(psn,"<null>");
        // ThemeMaterial's own fields (dump.cs:420240): LowEndMetallic@0x18 (float),
        // LowResMask@0x20 (Texture2D), MaskValues@0x28 (ArrayHack[]). FixTerrain bakes these
        // into the merged terrain mesh's vertex colours/UV4, so an empty mask is a candidate
        // for "geometry draws and depth-writes but shades black".
        void* lrmask = fld_p(tmat, 0x20);
        void* mvals  = fld_p(tmat, 0x28);
        char lrn[80];
        if(!read_str(obj_ok(lrmask)?obj_get_name(lrmask,NULL):NULL, lrn, sizeof lrn)) strcpy(lrn,"<null>");
        flog("BASEDIAG themeMat='%s'(ThemeMaterial) lowEndMetallic=%.3f lowResMask=%p name='%s' "
             "maskValues=%p len=%d | pathMat='%s' shader='%s'",
             tmn, fld_f(tmat,0x18), lrmask, lrn,
             mvals, obj_ok(mvals) ? (int)*(int32_t*)((uintptr_t)mvals+0x18) : -1, pmn, psn);
        // _TerrainPartitions is List<QuestMapTerrain.Partition> (dump.cs:552150), and
        // QuestMapTerrain.Partition (dump.cs:553956) is a plain class with
        // GameObject GameObject@0x10 / Mesh@0x18 / Renderer MeshRenderer@0x20 /
        // MeshFilter@0x28 -- a SINGLE GameObject, not a List<GameObject> as the previous
        // session assumed. The Renderer at 0x20 is the thing that actually draws the terrain,
        // so read its sharedMaterial + shader directly: that settles what shades the board
        // without any guessing about the theme material.
        // List<T>: _items @0x10 (array, data @0x20), _size @0x18.
        void* tparts = fld_p(bld, 0x48);
        void* titems = fld_p(tparts, 0x10);
        int tn = list_count(tparts);
        for (int i = 0; i < tn && i < 4; i++) {
            void* part = obj_ok(titems) ? *(void**)((uintptr_t)titems + 0x20 + 8*i) : NULL;
            void* go   = fld_p(part, 0x10);
            void* rend = fld_p(part, 0x20);
            char gn[80]; char rmn[80]; char rsn[80];
            if(!read_str(obj_ok(go)?obj_get_name(go,NULL):NULL, gn, sizeof gn)) strcpy(gn,"<null>");
            void* rmat = obj_ok(rend) ? rend_get_shared_mat(rend,NULL) : NULL;
            void* rsh  = obj_ok(rmat) ? mat_get_shader(rmat,NULL) : NULL;
            if(!read_str(obj_ok(rmat)?obj_get_name(rmat,NULL):NULL, rmn, sizeof rmn)) strcpy(rmn,"<null>");
            if(!read_str(obj_ok(rsh)?obj_get_name(rsh,NULL):NULL, rsn, sizeof rsn)) strcpy(rsn,"<null>");
            V3 p; p.x=p.y=p.z=-9999.0f;
            void* tr = obj_ok(go) ? go_transform(go,NULL) : NULL;
            if (obj_ok(tr)) p = tr_get_pos(tr, NULL);
            // MATSWAP (below) proved the terrain draws correctly once it is handed a working
            // material, so camera framing, lighting, fog and exposure are ALL fine and the
            // fault is this material alone. Narrow it down: UnityEngine.Object's native
            // handle m_CachedPtr@0x10 is 0 for a "missing"/unloaded asset (that is what makes
            // Unity's fake-null work), so a zero on the shader means the shader never loaded,
            // while a zero on the main texture means the shader is fine and the textures did
            // not come out of the bundle. renderQueue reads back as garbage when a material
            // has no usable shader, giving a third independent signal.
            int   (*mat_render_queue)(void*,void*) = (int(*)(void*,void*))(g_base + 0x1B56B6C);
            void* (*mat_main_tex)(void*,void*)     = (void*(*)(void*,void*))(g_base + 0x1B565F4);
            void* mtex = obj_ok(rmat) ? mat_main_tex(rmat,NULL) : NULL;
            char mtn[80];
            if(!read_str(obj_ok(mtex)?obj_get_name(mtex,NULL):NULL, mtn, sizeof mtn)) strcpy(mtn,"<null>");
            flog("BASEDIAG terrainPart[%d] go='%s' active=%d pos=(%.2f,%.2f,%.2f) "
                 "rend=%p mat=%p '%s' shader=%p '%s'",
                 i, gn, obj_ok(go) ? go_active(go,NULL) : -1, p.x, p.y, p.z,
                 rend, rmat, rmn, rsh, rsn);
            flog("BASEDIAG terrainPart[%d] matNative=%p shaderNative=%p renderQueue=%d "
                 "mainTex=%p '%s' texNative=%p | pathMatNative=%p pathShaderNative=%p pathQueue=%d",
                 i, fld_p(rmat,0x10), fld_p(rsh,0x10),
                 obj_ok(rmat) ? mat_render_queue(rmat,NULL) : -12345,
                 mtex, mtn, fld_p(mtex,0x10),
                 fld_p(pmat,0x10), fld_p(psh,0x10),
                 obj_ok(pmat) ? mat_render_queue(pmat,NULL) : -12345);
            // MATSWAP experiment. The terrain renderers all carry
            // 'FA1D4FC4668CDA2E014788020357F43D9F88E625 (Instance)', whose Material.get_shader
            // reads back as null -- while the SAME get_shader call on _PathMaterial correctly
            // returns 'EB/Particle/Uber/Base'. So either that material really has no shader
            // (it would draw black) or the reading is another artefact. Settle it by drawing
            // the terrain with the known-good path material instead: if the board stops being
            // black, the terrain material is the fault; if it stays black, materials are not
            // the problem at all and the fault is upstream of the draw.
            // Renderer.set_material (@0x16ADC28) rather than set_sharedMaterial, because
            // FixTerrain itself calls set_material at @0xB65094 -- so that icall is certainly
            // registered in this build, which Material.set_shader turned out not to be.
#if MATSWAP
            if (obj_ok(rend) && obj_ok(pmat)) {
                void (*rend_set_mat)(void*,void*,void*) =
                    (void(*)(void*,void*,void*))(g_base + 0x16ADC28);
                rend_set_mat(rend, pmat, NULL);
                flog("MATSWAP terrainPart[%d] material <- '%s'", i, pmn);
            }
#endif
        }
        // FORCELIGHT experiment. OnBaseBoardBuildComplete runs on the main thread with il2cpp
        // in a normal state, so the game's own GameObject::SetActive can be called directly
        // (address taken from the call at 0x124C020 inside EBTimeOfDay.Apply; x2 = MethodInfo,
        // which that call site passes as null). Force the ToD's KeyLight, LightObjects[] and
        // SkyBox active: if the board lights up, Apply either never ran or ran with
        // enabled=false, and the TODAPPLY log in this same run says which.
#if FORCELIGHT
        if (obj_ok(tod0)) {
            void (*go_setactive)(void*,int,void*) =
                (void (*)(void*,int,void*))(g_base + 0x1B50CA8);
            void* kl = fld_p(tod0, 0x48);
            if (obj_ok(kl)) go_setactive(kl, 1, NULL);
            if (obj_ok(lightObjs)) {
                int n = *(int32_t*)((uintptr_t)lightObjs + 0x18);
                for (int i = 0; i < n && i < 16; i++) {
                    void* lo = *(void**)((uintptr_t)lightObjs + 0x20 + 8*i);
                    if (obj_ok(lo)) go_setactive(lo, 1, NULL);
                }
            }
            flog("FORCELIGHT keyLight=%p lightObjs=%p forced active", kl, lightObjs);
        }
#endif
        flog("BASEDIAG cam target=(%.2f,%.2f,%.2f) lookAt=(%.2f,%.2f,%.2f) "
             "posOffset=(%.2f,%.2f,%.2f) startTarget=(%.2f,%.2f,%.2f) fov=%.2f mode=%d",
             t[0],t[1],t[2], la[0],la[1],la[2], po[0],po[1],po[2], st[0],st[1],st[2],
             obj_ok(cam) ? *(float*)((uintptr_t)cam+0x1A0) : -1.0f,
             obj_ok(cam) ? *(int32_t*)((uintptr_t)cam+0x1E4) : -1);
    );
    return r;
}
// slot 80 BASEAPPLY: BaseRenderSettings.ApplyBaseSettings(this=a0). This runs EVERY FRAME
// (via ApplyEveryFrame), so log the presence marker once and then dump the BaseRenderSettings-
// only fields -- the fog block it lerps between and the camera-height range that drives the
// lerp. A black BaseFogColor, or a Min/MaximumCameraHeight range that doesn't bracket the
// base camera's actual height (y=160), would leave the board fogged to black.
static volatile int g_baseapply_dumped = 0;
// Color is 4 packed floats (rgba) stored inline.
static void fld_col(void* o, int off, float* c){
    c[0]=c[1]=c[2]=c[3]=-999.0f;
    if (obj_ok(o)) memcpy(c, (void*)((uintptr_t)o + off), 16);
}
// FOGFIX experiment. ApplyBaseSettings computes
//   blend = Clamp01((_BaseCameraHeight - MinimumCameraHeight)/(Maximum - Minimum))
//         = (250-0)/(250-0) = 1.0
// then LerpFogSettings UNCONDITIONALLY copies BaseFogColorAmbient into FogColorAmbient
// (@0xCF54A8..0xCF54BC -- not lerped, a straight 16-byte q-register move) and lerps the rest
// of the fog block from the Base*/BaseZoom* pairs. Live values make the applied fog
//   ambient=(0,0,0,0)  color=(0.3,0.3,0.3,0)  distStart=5 distEnd=50  height=-1..20
// i.e. PURE BLACK ambient fog saturating 50 units out, with the camera ~400 units from the
// board. The sky (separate SkyLuminousScale path) and the UI text are unfogged, which is
// precisely what the screenshots show. Overwrite the Base* source fields with the working
// non-base numbers this same object reports via RSACTIVE (distEnd 800, height 0..300, a
// bluish ambient) -- if the board lights up, fog is confirmed as the blackener.
// RESULT: fog is NOT the blackener. With the poke live the log confirmed the applied block
// became distEnd=800 / height 0..300 / bluish ambient and the board stayed exactly as black,
// while the terrain still silhouettes cleanly against the sky along the horizon curve -- so
// geometry renders and depth-writes but shades pure black. That is a LIGHTING failure, not
// fog, exposure, culling or framing. Kept at 0 as documentation of a ruled-out cause.
#define FOGFIX 0
// File scope on purpose: PROTECT(...) is a single-argument macro, so a braced initializer's
// top-level commas would be parsed as extra macro arguments and fail to compile.
static const float g_fogfix_amb[4] = {0.067f,0.169f,0.298f,0.361f}; // RSACTIVE FogColorAmbient
static const float g_fogfix_col[4] = {0.015f,0.042f,0.071f,0.502f}; // RSACTIVE FogColor
void* hook_80(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    PROTECT(
#if FOGFIX
        if(obj_ok(a0)){
            memcpy((void*)((uintptr_t)a0+0x1BC), g_fogfix_amb, 16);  // BaseFogColorAmbient
            memcpy((void*)((uintptr_t)a0+0x1CC), g_fogfix_col, 16);  // BaseFogColor
            memcpy((void*)((uintptr_t)a0+0x1EC), g_fogfix_col, 16);  // BaseZoomFogColor
            *(float*)((uintptr_t)a0+0x1DC) =   0.0f;  // BaseFogDistStart
            *(float*)((uintptr_t)a0+0x1E0) = 800.0f;  // BaseFogDistEnd
            *(float*)((uintptr_t)a0+0x1E4) =   0.0f;  // BaseFogHeightStart
            *(float*)((uintptr_t)a0+0x1E8) = 300.0f;  // BaseFogHeightEnd
            *(float*)((uintptr_t)a0+0x1FC) =   0.0f;  // BaseZoomFogDistStart
            *(float*)((uintptr_t)a0+0x200) = 800.0f;  // BaseZoomFogDistEnd
            *(float*)((uintptr_t)a0+0x204) =   0.0f;  // BaseZoomFogHeightStart
            *(float*)((uintptr_t)a0+0x208) = 300.0f;  // BaseZoomFogHeightEnd
        }
#endif
        if(!g_baseapply_dumped){
            g_baseapply_dumped = 1;
            float amb[4]; float fog[4]; float zoom[4];
            fld_col(a0, 0x1BC, amb);   // BaseFogColorAmbient
            fld_col(a0, 0x1CC, fog);   // BaseFogColor
            fld_col(a0, 0x1EC, zoom);  // BaseZoomFogColor
            flog("BASEAPPLY this=%p isBase=%d baseCamHeight=%.2f minCamH=%.2f maxCamH=%.2f",
                 a0, obj_ok(a0) ? *(uint8_t*)((uintptr_t)a0+0x218) : -1,
                 fld_f(a0,0x214), fld_f(a0,0x20C), fld_f(a0,0x210));
            flog("BASEAPPLY baseFogAmb=(%.3f,%.3f,%.3f,%.3f) baseFog=(%.3f,%.3f,%.3f,%.3f) "
                 "distStart=%.2f distEnd=%.2f heightStart=%.2f heightEnd=%.2f",
                 amb[0],amb[1],amb[2],amb[3], fog[0],fog[1],fog[2],fog[3],
                 fld_f(a0,0x1DC), fld_f(a0,0x1E0), fld_f(a0,0x1E4), fld_f(a0,0x1E8));
            flog("BASEAPPLY zoomFog=(%.3f,%.3f,%.3f,%.3f) zoomDistStart=%.2f zoomDistEnd=%.2f "
                 "zoomHeightStart=%.2f zoomHeightEnd=%.2f",
                 zoom[0],zoom[1],zoom[2],zoom[3],
                 fld_f(a0,0x1FC), fld_f(a0,0x200), fld_f(a0,0x204), fld_f(a0,0x208));
        }
    );
    return H[80].orig(a0,a1,a2,a3,a4,a5,a6,a7);
}
// slot 81 RSACTIVE: EBRenderSettingsBase.ApplyWhenActivated(this=a0). Fires once per
// activation for EVERY render-settings object in the game, so one run captures the base's
// numbers next to those of scenes that render correctly. Dumps the exposure triple (the
// prime suspect for "lit geometry is black but the sky isn't"), the live fog block, the sky
// parameters and the directional-light shift, tagged with the concrete subclass name so the
// base's entry can be picked out of the stream.
void* hook_81(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    PROTECT(
        char cls[80]; obj_class(a0, cls, sizeof cls);
        float amb[4]; float fog[4]; float sky[4];
        fld_col(a0, 0x2C, amb);   // FogColorAmbient
        fld_col(a0, 0x3C, fog);   // FogColor
        fld_col(a0, 0xE4, sky);   // SkyClearColor
        // Exposure: GetEV100 uses Aperture/ShutterTime/ISO. Any of them at 0 makes the
        // EV100 log2() degenerate (inf/NaN), which collapses the global exposure to black.
        flog("RSACTIVE this=%p class='%s' setting=%d aperture=%.4f shutter=%.5f iso=%.2f "
             "nativeExp=%d",
             a0, cls, obj_ok(a0) ? *(int32_t*)((uintptr_t)a0+0x18) : -1,
             fld_f(a0,0x20), fld_f(a0,0x24), fld_f(a0,0x28),
             obj_ok(a0) ? *(uint8_t*)((uintptr_t)a0+0xFC) : -1);
        flog("RSACTIVE fogAmb=(%.3f,%.3f,%.3f,%.3f) fog=(%.3f,%.3f,%.3f,%.3f) "
             "distStart=%.2f distEnd=%.2f heightStart=%.2f heightEnd=%.2f dirLightShift=%.3f",
             amb[0],amb[1],amb[2],amb[3], fog[0],fog[1],fog[2],fog[3],
             fld_f(a0,0x4C), fld_f(a0,0x50), fld_f(a0,0x54), fld_f(a0,0x58), fld_f(a0,0x5C));
        flog("RSACTIVE skyScale=%.4f skyClear=(%.3f,%.3f,%.3f,%.3f) aoInfluence=%.3f "
             "emissiveOffset=%.3f dir2Intensity=%.3f",
             fld_f(a0,0xE0), sky[0],sky[1],sky[2],sky[3],
             fld_f(a0,0xF4), fld_f(a0,0xF8), fld_f(a0,0x70));
    );
    return H[81].orig(a0,a1,a2,a3,a4,a5,a6,a7);
}
// slot 82 TODAPPLY: EBTimeOfDayManager.EBTimeOfDay.Apply(this=a0, int id=a1, bool enabled=a2).
// Both int args arrive in the low 32 bits of their registers.
void* hook_82(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    PROTECT(
        char nm[80]; if(!read_str(fld_p(a0,0x18), nm, sizeof nm)) strcpy(nm,"<null>");
        flog("TODAPPLY this=%p name='%s' id=%d enabled=%d isMerged=%d keyLight=%p lightmaps=%p",
             a0, nm, (int)(uintptr_t)a1 & 0xffffffff, (int)(uintptr_t)a2 & 1,
             obj_ok(a0) ? *(uint8_t*)((uintptr_t)a0+0x10) : -1,
             fld_p(a0,0x48), fld_p(a0,0x70));
    );
    void* r = H[82].orig(a0,a1,a2,a3,a4,a5,a6,a7);
    // LMFIX -- see the toggle block for the derivation. Must run after the original, since
    // the original is what binds the real merged lightmap to the global. Uses the same
    // SetGlobalTexture(string, Texture) overload the call site at @0x124C1AC tail-calls
    // (0x16B0E78, not the 0x16B0EF4 sibling), with a null MethodInfo third argument.
#if LMFIX
    PROTECT(
        if (obj_ok(a0) && *(uint8_t*)((uintptr_t)a0+0x10) == 1 && g_strnew) {
            void* (*tex2d_white)(void*) = (void*(*)(void*))(g_base + 0x16B51C0);
            void  (*sh_set_global_tex)(void*,void*,void*) =
                (void(*)(void*,void*,void*))(g_base + 0x16B0E78);
            void* white = tex2d_white(NULL);
            void* pname = g_strnew("_lm");
            flog("LMFIX merged tod=%p white=%p native=%p prop=%p", a0, white,
                 fld_p(white,0x10), pname);
            if (obj_ok(white) && obj_ok(pname)) {
                sh_set_global_tex(pname, white, NULL);
                flog("LMFIX global _lm <- whiteTexture");
            }
        }
    );
#endif
    return r;
}
// slot 83 TODMGRAPPLY: EBTimeOfDayManager.Apply(this=a0, bool broadcast=a1, bool applyActive=a2).
void* hook_83(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    PROTECT(
        void* arr = fld_p(a0, 0x18);
        flog("TODMGRAPPLY this=%p broadcast=%d applyActive=%d activeIdx=%d todLen=%d",
             a0, (int)(uintptr_t)a1 & 1, (int)(uintptr_t)a2 & 1,
             obj_ok(a0) ? *(int32_t*)((uintptr_t)a0+0x20) : -1,
             obj_ok(arr) ? (int)*(int32_t*)((uintptr_t)arr+0x18) : -1);
    );
    return H[83].orig(a0,a1,a2,a3,a4,a5,a6,a7);
}
// slot 84 FIXTERRAIN: GameboardBuilder.FixTerrain(this=a0, GameObject go=a1). Runs once per
// board build, for BOTH the story board (which renders correctly) and the base board (black),
// so a single run that visits both yields a direct diff of the terrain's real material.
// Reproduces the builder's own lookup at @0xB65058: _ThemeMaterial ->
// GetComponentInChildren<Renderer> -> sharedMaterial -> shader. The generic method is the
// gshared Component.GetComponentInChildren<T> (@0x11D45A4); its MethodInfo* is taken from the
// exact GOT slot the call site uses (0x2C313D0, two derefs, same pattern as a _TypeInfo).
void* hook_84(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    // THEMESWAP -- see the toggle block. Must run BEFORE the original, since the original is
    // what reads _ThemeMaterial@0x188 and pushes its material onto the terrain renderers.
    // Base board only (isUsersBase@0x198); the story board is the control and is left alone.
    // Logs the ContainsKey probe unconditionally so a "not reachable" answer is still recorded.
#if THEMESWAP
    PROTECT(
        if (obj_ok(a0) && *(uint8_t*)((uintptr_t)a0+0x198) && g_strnew) {
            void* (*lib_contents)(void*,void*) = (void*(*)(void*,void*))(g_base + 0xE3B4D4);
            int   (*dict_has)(void*,void*,void*) =
                (int(*)(void*,void*,void*))(g_base + 0x1FFD7C8);
            void* (*dict_get)(void*,void*,void*) =
                (void*(*)(void*,void*,void*))(g_base + 0x1FFD490);
            void* (*go_getcomp)(void*,void*) = (void*(*)(void*,void*))(g_base + 0x11E5734);
            void* mi_has = fld_p(*(void**)(g_base + 0x2C38CC0), 0x0);
            void* mi_get = fld_p(*(void**)(g_base + 0x2C24188), 0x0);
            void* mi_gc  = fld_p(*(void**)(g_base + 0x2BCF8B8), 0x0);
            void* lib    = fld_p(a0, 0x148);
            void* cont   = obj_ok(lib) ? lib_contents(lib, NULL) : NULL;
            void* kbase  = g_strnew("qb_theme_material_base");
            void* kstory = g_strnew("qb_theme_material");
            int hb = (obj_ok(cont) && obj_ok(mi_has)) ? dict_has(cont, kbase,  mi_has) : -1;
            int hs = (obj_ok(cont) && obj_ok(mi_has)) ? dict_has(cont, kstory, mi_has) : -1;
            flog("THEMESWAP lib=%p contents=%p hasBaseKey=%d hasStoryKey=%d mi=%p/%p/%p",
                 lib, cont, hb, hs, mi_has, mi_get, mi_gc);
            if (hs == 1 && obj_ok(mi_get) && obj_ok(mi_gc)) {
                void* go = dict_get(cont, kstory, mi_get);
                void* tm = obj_ok(go) ? go_getcomp(go, mi_gc) : NULL;
                flog("THEMESWAP storyPrefab=%p themeMaterial=%p (was %p)",
                     go, tm, fld_p(a0, 0x188));
                if (obj_ok(tm)) {
                    *(void**)((uintptr_t)a0 + 0x188) = tm;
                    flog("THEMESWAP _ThemeMaterial <- qb_theme_material");
                }
            }
        }
    );
#endif
    void* r = H[84].orig(a0,a1,a2,a3,a4,a5,a6,a7);
    PROTECT(
        void* (*obj_get_name)(void*,void*) = (void*(*)(void*,void*))(g_base + 0x16A16A0);
        void* (*mat_get_shader)(void*,void*) = (void*(*)(void*,void*))(g_base + 0x1B56338);
        void* (*rend_get_shared_mat)(void*,void*) =
            (void*(*)(void*,void*))(g_base + 0x16ADC78);
        void* (*comp_gic)(void*,void*) = (void*(*)(void*,void*))(g_base + 0x11D45A4);
        void* tmat = fld_p(a0, 0x188);
        char theme[80]; char argn[80];
        if(!read_str(fld_p(a0,0x178), theme, sizeof theme)) strcpy(theme,"<null>");
        if(!read_str(obj_ok(a1)?obj_get_name(a1,NULL):NULL, argn, sizeof argn)) strcpy(argn,"<null>");
        flog("==FIXTERRAIN== builder=%p isUsersBase=%d theme='%s' go='%s' themeMat=%p "
             "lowEndMetallic=%.3f lowResMask=%p maskValues=%p",
             a0, obj_ok(a0) ? *(uint8_t*)((uintptr_t)a0+0x198) : -1, theme, argn, tmat,
             fld_f(tmat,0x18), fld_p(tmat,0x20), fld_p(tmat,0x28));
        // The whole point of running this on both boards: the base board's lighting all looks
        // healthy in isolation, so only the story board's numbers can say which of them is
        // actually abnormal. The base ToD reports IEM/PMREM/IEM8Bit/PMREM8Bit all null with
        // CubeMapIndex=-1 -- if the story ToD reports the same, indirect lighting is not the
        // difference and the shader family is; if the story ToD has real cubemaps, the base
        // terrain is a PBR surface with no indirect term, which is black.
        log_tod_lighting(obj_ok(a0) && *(uint8_t*)((uintptr_t)a0+0x198) ? "FT-BASE" : "FT-STORY");
        // Plain field reads first, generic call LAST: a fault inside PROTECT swallows the rest
        // of the block, and the previous attempt lost everything below this point (it passed a
        // singly-dereferenced GOT entry as the MethodInfo* and took the process down with it).
        // _TerrainPartitions@0x48 -> QuestMapTerrain.Partition { GameObject@0x10, Mesh@0x18,
        // Renderer MeshRenderer@0x20 } -- the renderer that actually draws the terrain, so its
        // sharedMaterial + shader answers the question with no generic call at all.
        void* tparts = fld_p(a0, 0x48);
        void* titems = fld_p(tparts, 0x10);
        int tn = list_count(tparts);
        for (int i = 0; i < tn && i < 3; i++) {
            void* part = obj_ok(titems) ? *(void**)((uintptr_t)titems + 0x20 + 8*i) : NULL;
            void* rend = fld_p(part, 0x20);
            void* rmat = obj_ok(rend) ? rend_get_shared_mat(rend,NULL) : NULL;
            void* rsh  = obj_ok(rmat) ? mat_get_shader(rmat,NULL) : NULL;
            char rmn[80]; char rsn[80];
            if(!read_str(obj_ok(rmat)?obj_get_name(rmat,NULL):NULL, rmn, sizeof rmn)) strcpy(rmn,"<null>");
            // read_str refuses anything longer than its buffer, and shader names are paths
            // ('EB/Terrain/...'), so give this one room and print the raw String* + length too
            // -- the terrain shader's name came back unreadable at 80 chars while the material
            // name on the same object read fine, and it matters whether that means "no name"
            // or just "name too long for the buffer".
            void* rshname = obj_ok(rsh) ? obj_get_name(rsh,NULL) : NULL;
            char rsnbig[300];
            if(!read_str(rshname, rsnbig, sizeof rsnbig)) strcpy(rsnbig,"<unreadable>");
            flog("FIXTERRAIN part[%d] rend=%p mat='%s' shaderNameStr=%p len=%d shader='%s'",
                 i, rend, rmn, rshname,
                 obj_ok(rshname) ? *(int32_t*)((uintptr_t)rshname+0x10) : -1, rsnbig);
            // Same three signals BASEDIAG collects, so the story board (which renders) can be
            // diffed field-for-field against the base board (which does not): the native
            // handles prove the assets actually loaded, and mainTexture/renderQueue say
            // whether the material is properly set up.
            int   (*mat_render_queue)(void*,void*) = (int(*)(void*,void*))(g_base + 0x1B56B6C);
            void* (*mat_main_tex)(void*,void*)     = (void*(*)(void*,void*))(g_base + 0x1B565F4);
            void* mtex = obj_ok(rmat) ? mat_main_tex(rmat,NULL) : NULL;
            char mtn[80];
            if(!read_str(obj_ok(mtex)?obj_get_name(mtex,NULL):NULL, mtn, sizeof mtn)) strcpy(mtn,"<null>");
            flog("FIXTERRAIN part[%d] matNative=%p shaderNative=%p renderQueue=%d "
                 "mainTex=%p '%s' texNative=%p",
                 i, fld_p(rmat,0x10), fld_p(rsh,0x10),
                 obj_ok(rmat) ? mat_render_queue(rmat,NULL) : -12345,
                 mtex, mtn, fld_p(mtex,0x10));
            // METALFIX. The base terrain draws with
            //   Hidden/PBR_EB_PLANAR_REFLECTION_ON__AO_NONE__BASE_TEX__EMISSIVE_NONE_
            //   _METALLIC_TOGGLE__MODE_BASIC__NORMAL_TEX__ROUGHNESS_TEX__EB_COMPOSITE_RMEAO_
            // i.e. the _METALLIC_TOGGLE and _ROUGHNESS_TEX variants are compiled IN, so the
            // shader samples _metallic_tex/_roughness_tex unconditionally -- but the probes
            // say both are unassigned (has=1, tex=0x0), as are _ao_tex and _emissive_tex.
            // The story's terrain material, by contrast, carries a real
            // _roughness_tex=qb_primordial_ground_r_nograss.
            // An unassigned texture property falls back to the shader's declared default, and
            // a "white" default means metallic = 1.0. A fully metallic surface has NO diffuse
            // term, which is exactly why the two lighting experiments changed nothing: KEYBOOST
            // (directional x6000) and SHFIX (flat SH ambient) both feed diffuse, and diffuse is
            // multiplied by (1 - metallic) = 0. All a metal can show is the PMREM specular
            // cubemap -- null on the base. That predicts precisely what we see: geometry that
            // depth-writes and renders pure black regardless of how much light it is given.
            // Binding an explicit black texture forces metallic = 0 and hands the surface its
            // diffuse term back. Texture2D.blackTexture @0x16B51F4 is static, so it takes only
            // the MethodInfo* argument.
#if METALFIX
            if (obj_ok(rmat) && g_strnew) {
                void* (*tex2d_black)(void*)      = (void*(*)(void*))(g_base + 0x16B51F4);
                void  (*mat_set_tex)(void*,void*,void*,void*) =
                    (void(*)(void*,void*,void*,void*))(g_base + 0x1B5682C);
                void* black = tex2d_black(NULL);
                void* pmet  = g_strnew("_metallic_tex");
                if (obj_ok(black) && obj_ok(pmet)) {
                    mat_set_tex(rmat, pmet, black, NULL);
                    flog("FIXTERRAIN part[%d] METALFIX _metallic_tex <- blackTexture=%p "
                         "native=%p", i, black, fld_p(black,0x10));
                } else {
                    flog("FIXTERRAIN part[%d] METALFIX FAILED black=%p prop=%p",
                         i, black, pmet);
                }
            }
#endif
            // KWFIX -- see the toggle block. Operates on rmat, the per-renderer material
            // INSTANCE that FixTerrain just assigned, so the change affects only the terrain.
#if KWFIX
            if (obj_ok(rmat) && g_strnew) {
                void  (*mat_disable_kw)(void*,void*,void*) =
                    (void(*)(void*,void*,void*))(g_base + 0x1B56C4C);
                void* (*mat_get_kws)(void*,void*) =
                    (void*(*)(void*,void*))(g_base + 0x1B56E1C);
                void* kw = g_strnew("EB_PLANAR_REFLECTION_ON");
                if (obj_ok(kw)) {
                    mat_disable_kw(rmat, kw, NULL);
                    void* kws = mat_get_kws(rmat, NULL);
                    flog("FIXTERRAIN part[%d] KWFIX disabled EB_PLANAR_REFLECTION_ON "
                         "keywordCount=%d", i,
                         obj_ok(kws) ? (int)*(int32_t*)((uintptr_t)kws+0x18) : -1);
                } else {
                    flog("FIXTERRAIN part[%d] KWFIX FAILED kw=%p", i, kw);
                }
            }
#endif
            // TEXFIX -- see the toggle block. Table-driven so the per-slot neutral value stays
            // next to its property name; white for the maps that scale light, black for
            // metallic (which scales the diffuse term by 1-metallic and is therefore inverted).
#if TEXFIX
            if (obj_ok(rmat) && g_strnew) {
                void* (*tex2d_white)(void*) = (void*(*)(void*))(g_base + 0x16B51C0);
                void* (*tex2d_black)(void*) = (void*(*)(void*))(g_base + 0x16B51F4);
                void  (*mat_set_tex)(void*,void*,void*,void*) =
                    (void(*)(void*,void*,void*,void*))(g_base + 0x1B5682C);
                void* white = tex2d_white(NULL);
                void* black = tex2d_black(NULL);
                for (int t = 0; t < 4; t++) {
                    void* tex = TF_WHITE[t] ? white : black;
                    void* pn  = g_strnew(TF_NAME[t]);
                    if (obj_ok(tex) && obj_ok(pn)) {
                        mat_set_tex(rmat, pn, tex, NULL);
                        flog("FIXTERRAIN part[%d] TEXFIX %-15s <- %s native=%p",
                             i, TF_NAME[t], TF_WHITE[t] ? "white" : "black", fld_p(tex,0x10));
                    } else {
                        flog("FIXTERRAIN part[%d] TEXFIX %-15s FAILED tex=%p prop=%p",
                             i, TF_NAME[t], tex, pn);
                    }
                }
            }
#endif
            // NORMFIX -- see the toggle block. Runs alongside TEXFIX so the surface has neutral
            // AO/roughness/metallic at the same time: if the vertex normal is the missing piece
            // there is then nothing else left multiplying the result to zero.
#if NORMFIX
            if (obj_ok(rmat) && g_strnew) {
                void  (*mat_disable_kw2)(void*,void*,void*) =
                    (void(*)(void*,void*,void*))(g_base + 0x1B56C4C);
                void* (*mat_get_kws2)(void*,void*) =
                    (void*(*)(void*,void*))(g_base + 0x1B56E1C);
                void* nkw = g_strnew("_NORMAL_TEX");
                if (obj_ok(nkw)) {
                    mat_disable_kw2(rmat, nkw, NULL);
                    void* kws = mat_get_kws2(rmat, NULL);
                    flog("FIXTERRAIN part[%d] NORMFIX disabled _NORMAL_TEX keywordCount=%d",
                         i, obj_ok(kws) ? (int)*(int32_t*)((uintptr_t)kws+0x18) : -1);
                } else {
                    flog("FIXTERRAIN part[%d] NORMFIX FAILED kw=%p", i, nkw);
                }
            }
#endif
        }
        // Now the builder's own lookup: two derefs off the GOT, exactly as @0xB65060-0xB65070
        // (x9 = *(g_base+0x2C313D0); x1 = *x9).
        void* migot = *(void**)(g_base + 0x2C313D0);
        void* mi    = fld_p(migot, 0x0);            // MethodInfo* for GetComponentInChildren<Renderer>
        flog("FIXTERRAIN gicMethodInfo=%p", mi);
        void* trend = (obj_ok(tmat) && obj_ok(mi)) ? comp_gic(tmat, mi) : NULL;
        void* tm    = obj_ok(trend) ? rend_get_shared_mat(trend, NULL) : NULL;
        void* tsh   = obj_ok(tm) ? mat_get_shader(tm, NULL) : NULL;
        char tmn2[80]; char tsn2[80];
        if(!read_str(obj_ok(tm)?obj_get_name(tm,NULL):NULL, tmn2, sizeof tmn2)) strcpy(tmn2,"<null>");
        if(!read_str(obj_ok(tsh)?obj_get_name(tsh,NULL):NULL, tsn2, sizeof tsn2)) strcpy(tsn2,"<null>");
        flog("FIXTERRAIN themeRend=%p appliedMat='%s' shader='%s'", trend, tmn2, tsn2);
        // The control run settled it: the STORY theme hands the terrain
        // 'Hidden/PBRTerrain_BASE_EB_EMISSIVE_NO_PULSE' (a dedicated terrain shader that reads
        // the vertex colours / UV3 / UV4 FixTerrain bakes) and renders, while the BASE theme
        // hands it 'Hidden/PBR_EB_..._BASE_TEX__NORMAL_TEX__ROUGHNESS_TEX...' -- a generic
        // object shader that wants texture maps the terrain material has none of, so it shades
        // black. GetComponentInChildren returns the FIRST match in a depth-first walk and
        // skips inactive objects, so the likeliest explanation is that the base theme prefab
        // has extra children (its bundle carries Animator/AnimationClip/SkinnedMeshRenderer
        // classes the story one does not) and the terrain renderer is no longer first.
        // Enumerate every renderer under the theme material to find out whether a PBRTerrain
        // material is present but simply not the one being picked.
        void* (*comp_get_go)(void*,void*) = (void*(*)(void*,void*))(g_base + 0x1B4BD28);
        void* (*go_gcic)(void*,void*)     = (void*(*)(void*,void*))(g_base + 0x11E5B70);
        int   (*go_active_h)(void*,void*) = (int(*)(void*,void*))(g_base + 0x1B50D38);
        void* tgo = obj_ok(tmat) ? comp_get_go(tmat, NULL) : NULL;
        void* gcicmi = fld_p(*(void**)(g_base + 0x2C33E58), 0x0);  // <Renderer> MethodInfo
        void* rends = (obj_ok(tgo) && obj_ok(gcicmi)) ? go_gcic(tgo, gcicmi) : NULL;
        int rn = obj_ok(rends) ? (int)*(int32_t*)((uintptr_t)rends + 0x18) : -1;
        flog("FIXTERRAIN themeGO=%p rendererCount=%d", tgo, rn);
        for (int k = 0; k < rn && k < 12; k++) {
            void* rr = *(void**)((uintptr_t)rends + 0x20 + 8*k);
            if (!obj_ok(rr)) { flog("FIXTERRAIN  rend[%d]=<null>", k); continue; }
            void* rgo = comp_get_go(rr, NULL);
            void* rm  = rend_get_shared_mat(rr, NULL);
            void* rs  = obj_ok(rm) ? mat_get_shader(rm, NULL) : NULL;
            char gname[80]; char mname[80]; char sname[300];
            if(!read_str(obj_ok(rgo)?obj_get_name(rgo,NULL):NULL, gname, sizeof gname)) strcpy(gname,"<null>");
            if(!read_str(obj_ok(rm)?obj_get_name(rm,NULL):NULL, mname, sizeof mname)) strcpy(mname,"<null>");
            if(!read_str(obj_ok(rs)?obj_get_name(rs,NULL):NULL, sname, sizeof sname)) strcpy(sname,"<null>");
            flog("FIXTERRAIN  rend[%d] go='%s' active=%d mat='%s' shader='%s'",
                 k, gname, obj_ok(rgo) ? go_active_h(rgo,NULL) : -1, mname, sname);
            // Which texture slots does this material actually have, and are any of them
            // filled? HasProperty says the slot exists on the shader; a non-null GetTexture
            // says an asset is bound to it.
            int   (*mat_has_prop)(void*,void*,void*) =
                (int(*)(void*,void*,void*))(g_base + 0x1B56B10);
            void* (*mat_get_tex)(void*,void*,void*) =
                (void*(*)(void*,void*,void*))(g_base + 0x1B56704);
            flog("FIXTERRAIN   probing textures mat=%p strnew=%p", rm, (void*)g_strnew);
            if (obj_ok(rm) && g_strnew) {
                for (unsigned t = 0; t < sizeof(TEXPROPS)/sizeof(TEXPROPS[0]); t++) {
                    void* pn = g_strnew(TEXPROPS[t]);
                    if (!obj_ok(pn)) { flog("FIXTERRAIN   prop %s <strnew failed>", TEXPROPS[t]); continue; }
                    int has = mat_has_prop(rm, pn, NULL) & 1;
                    void* tx = has ? mat_get_tex(rm, pn, NULL) : NULL;
                    char txn[80];
                    if(!read_str(obj_ok(tx)?obj_get_name(tx,NULL):NULL, txn, sizeof txn)) strcpy(txn,"<null>");
                    flog("FIXTERRAIN   prop %-22s has=%d tex=%p native=%p '%s'",
                         TEXPROPS[t], has, tx, fld_p(tx,0x10), txn);
                }
            }
            // The shader variant is baked into the shader NAME here (Hidden/PBR_EB_..._ON__...),
            // which is EB's stripped-variant naming, so the material's runtime keyword list is
            // a separate thing worth seeing: it says which branches the material itself asks
            // for. get_color reads the shader's _Color/_color tint -- a black tint on a
            // BASE_TEX shader would explain a black surface all by itself, with no missing
            // asset involved.
            void* (*mat_keywords)(void*,void*) = (void*(*)(void*,void*))(g_base + 0x1B56E1C);
            COL4 (*mat_get_color)(void*,void*) = (COL4(*)(void*,void*))(g_base + 0x1B563C8);
            if (obj_ok(rm)) {
                COL4 c = mat_get_color(rm, NULL);
                void* kw = mat_keywords(rm, NULL);
                int kn = obj_ok(kw) ? (int)*(int32_t*)((uintptr_t)kw + 0x18) : -1;
                flog("FIXTERRAIN   color=(%.3f,%.3f,%.3f,%.3f) keywordCount=%d",
                     c.r, c.g, c.b, c.a, kn);
                float (*mat_get_float)(void*,void*,void*) =
                    (float(*)(void*,void*,void*))(g_base + 0x1B57610);
                COL4 (*mat_get_colorn)(void*,void*,void*) =
                    (COL4(*)(void*,void*,void*))(g_base + 0x1B564A8);
                for (unsigned t = 0; t < sizeof(FLOATPROPS)/sizeof(FLOATPROPS[0]); t++) {
                    void* pn = g_strnew(FLOATPROPS[t]);
                    if (!obj_ok(pn)) continue;
                    int has = mat_has_prop(rm, pn, NULL) & 1;
                    flog("FIXTERRAIN   f %-20s has=%d val=%.4f", FLOATPROPS[t], has,
                         has ? mat_get_float(rm, pn, NULL) : -999.0f);
                }
                for (unsigned t = 0; t < sizeof(COLORPROPS)/sizeof(COLORPROPS[0]); t++) {
                    void* pn = g_strnew(COLORPROPS[t]);
                    if (!obj_ok(pn)) continue;
                    int has = mat_has_prop(rm, pn, NULL) & 1;
                    COL4 cc; cc.r=cc.g=cc.b=cc.a=-999.0f;
                    if (has) cc = mat_get_colorn(rm, pn, NULL);
                    flog("FIXTERRAIN   c %-20s has=%d val=(%.3f,%.3f,%.3f,%.3f)",
                         COLORPROPS[t], has, cc.r, cc.g, cc.b, cc.a);
                }
                for (int q = 0; q < kn && q < 16; q++) {
                    char kb[160];
                    if(!read_str(*(void**)((uintptr_t)kw + 0x20 + 8*q), kb, sizeof kb))
                        strcpy(kb,"<unreadable>");
                    flog("FIXTERRAIN   keyword[%d] '%s'", q, kb);
                }
            }
        }
    );
    return r;
}
// slot 85 PRINIT: EBPlanarReflectionManager.Init(this=a0). Dump the quality actually chosen
// and the render targets it allocated. Fields: _Config@0x18 (Quality@0x10, ClearMode@0x18,
// UseReplacementShaders@0x30), _ReflectionCamera@0x20, _Color0@0x48, _Depth@0x70,
// _ReflectionTextureProperty@0x40, _setup@0x81, CurrentQuality@0x84.
void* hook_85(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    void* r = H[85].orig(a0,a1,a2,a3,a4,a5,a6,a7);
    PROTECT(
        void* cfg = fld_p(a0, 0x18);
        flog("PRINIT this=%p currentQuality=%d cfgQuality=%d clearMode=%d replShaders=%d "
             "reflCam=%p color0=%p depth=%p texProp=%d setup=%d paused=%d",
             a0,
             obj_ok(a0)  ? *(int32_t*)((uintptr_t)a0+0x84)  : -999,
             obj_ok(cfg) ? *(int32_t*)((uintptr_t)cfg+0x10) : -999,
             obj_ok(cfg) ? *(int32_t*)((uintptr_t)cfg+0x18) : -999,
             obj_ok(cfg) ? *(uint8_t*)((uintptr_t)cfg+0x30) : -1,
             fld_p(a0,0x20), fld_p(a0,0x48), fld_p(a0,0x70),
             obj_ok(a0) ? *(int32_t*)((uintptr_t)a0+0x40) : -999,
             obj_ok(a0) ? *(uint8_t*)((uintptr_t)a0+0x81) : -1,
             obj_ok(a0) ? *(uint8_t*)((uintptr_t)a0+0x80) : -1);
    );
    return r;
}
// slot 86 PRRENDER: EBPlanarReflectionManager.Render. Runs per frame when reflections are on,
// so log only the first call -- its presence or absence is the whole signal. It DOES fire, at
// quality High: the previous session's "planar reflections never initialise" reading came
// from a build where slots 85/86 were listed in the H table but never added to the
// function-pointer array, so the hooks were simply not installed. What is odd is that _Color0
// reads null on entry while _Depth is non-null, so log the whole render-target set again
// after the original runs -- if Render allocates _Color0 lazily this is a non-issue, and if
// it stays null the terrain is sampling a reflection texture that was never produced.
static volatile int g_prrender_logged = 0;
void* hook_86(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    int first = !g_prrender_logged;
    if (first) {
        g_prrender_logged = 1;
        PROTECT(
            flog("PRRENDER pre  this=%p currentQuality=%d color0=%p paused=%d",
                 a0,
                 obj_ok(a0) ? *(int32_t*)((uintptr_t)a0+0x84) : -999,
                 fld_p(a0,0x48),
                 obj_ok(a0) ? *(uint8_t*)((uintptr_t)a0+0x80) : -1);
        );
    }
    void* r = H[86].orig(a0,a1,a2,a3,a4,a5,a6,a7);
    if (first) {
        PROTECT(
            // _Color0@0x48 _Color1@0x50 _Color2@0x58 _Color3@0x60 _Color3Blit@0x68 _Depth@0x70
            flog("PRRENDER post color0=%p color1=%p color2=%p color3=%p blit=%p depth=%p "
                 "replShader=%p texProp=%d",
                 fld_p(a0,0x48), fld_p(a0,0x50), fld_p(a0,0x58), fld_p(a0,0x60),
                 fld_p(a0,0x68), fld_p(a0,0x70), fld_p(a0,0x78),
                 obj_ok(a0) ? *(int32_t*)((uintptr_t)a0+0x40) : -999);
        );
    }
    return r;
}
// slot 87 PRSETUP: EBPlanarReflectionManager.Setup(this=a0).
void* hook_87(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    void* r = H[87].orig(a0,a1,a2,a3,a4,a5,a6,a7);
    PROTECT(
        void* cfg = fld_p(a0, 0x18);
        flog("PRSETUP this=%p cfg=%p cfgQuality=%d currentQuality=%d setup=%d reflCam=%p",
             a0, cfg,
             obj_ok(cfg) ? *(int32_t*)((uintptr_t)cfg+0x10) : -999,
             obj_ok(a0)  ? *(int32_t*)((uintptr_t)a0+0x84)  : -999,
             obj_ok(a0)  ? *(uint8_t*)((uintptr_t)a0+0x81)  : -1,
             fld_p(a0,0x20));
    );
    return r;
}
// slot 88 PRCONFIG: PerformanceManager.LoadPlanarReflectionConfig(this=a0, ref Config=a1).
// a1 is a byref, so the Config object is one dereference away (the method itself does
// `ldr x21,[x19]` at 0xDA82F8 before writing Quality at +0x10). The return value in w0 is the
// bool that tells EBSetup whether a config was produced at all.
void* hook_88(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    void* r = H[88].orig(a0,a1,a2,a3,a4,a5,a6,a7);
    PROTECT(
        void* cfg = obj_ok(a1) ? *(void**)a1 : NULL;
        flog("PRCONFIG ret=%d cfg=%p quality=%d clearMode=%d replShaders=%d clipOffset=%.3f",
             (int)(uintptr_t)r & 1, cfg,
             obj_ok(cfg) ? *(int32_t*)((uintptr_t)cfg+0x10) : -999,
             obj_ok(cfg) ? *(int32_t*)((uintptr_t)cfg+0x18) : -999,
             obj_ok(cfg) ? *(uint8_t*)((uintptr_t)cfg+0x30) : -1,
             fld_f(cfg, 0x2C));
    );
    return r;
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
// BASENODE (slot 89): per-node dump of the building-lookup chain, taken at
// AttachNodeController time (this=a0, nodeObj=a1, mapTile=a2). Reads exactly the
// fields Quests.MapTile.get_building (@0x1094230) reads: buildingSocket@0x130,
// mission@0x140, mission.placement@0x70, placement.entities@0x20 -- plus the
// socket id string so the placements key can be compared by eye.
void* hook_89(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    PROTECT({
        uintptr_t t=(uintptr_t)a2;
        if(t>=0x100000 && !(t&7)){
            uintptr_t bs=*(uintptr_t*)(t+0x130); uintptr_t mi=*(uintptr_t*)(t+0x140);
            uintptr_t pl=(mi>=0x100000 && !(mi&7))?*(uintptr_t*)(mi+0x70):0;
            uintptr_t en=(pl>=0x100000 && !(pl&7))?*(uintptr_t*)(pl+0x20):0;
            char sid[48]; sid[0]=0;
            if(bs>=0x100000 && !(bs&7)){ uintptr_t s=*(uintptr_t*)(bs+0x20);
                if(s>=0x100000 && !(s&7)){ int32_t l=*(int32_t*)(s+0x10); uint16_t* c=(uint16_t*)(s+0x14);
                    if(l>0&&l<46){ int k=0; for(;k<l;k++) sid[k]=(c[k]<128)?(char)c[k]:'?'; sid[k]=0; } } }
            flog("BASENODE tile=%lx bs=%lx sid=%s mission=%lx placement=%lx entities=%lx",
                 t, bs, sid, mi, pl, en);
        } else flog("BASENODE tile=%lx", t);
    });
    return H[89].orig(a0,a1,a2,a3,a4,a5,a6,a7);
}
// Shared GameObject dump for the base-building diagnostics: name, active flags, world
// position, localScale and up to 6 child renderers with their shader names.
static void dump_go(const char* tag, void* go){
    if (!obj_ok(go)) { flog("%s go=<null>", tag); return; }
    int  (*go_active)(void*,void*)   = (int(*)(void*,void*))(g_base + 0x1B50CF8);
    int  (*go_active_h)(void*,void*) = (int(*)(void*,void*))(g_base + 0x1B50D38);
    void* (*go_transform)(void*,void*) = (void*(*)(void*,void*))(g_base + 0x1B50BD8);
    V3   (*tr_get_pos)(void*,void*)  = (V3(*)(void*,void*))(g_base + 0x16B7C04);
    V3   (*tr_get_scale)(void*,void*)= (V3(*)(void*,void*))(g_base + 0x16B841C);
    void* (*obj_get_name)(void*,void*) = (void*(*)(void*,void*))(g_base + 0x16A16A0);
    void* (*comp_get_go)(void*,void*) = (void*(*)(void*,void*))(g_base + 0x1B4BD28);
    void* (*go_gcic)(void*,void*)     = (void*(*)(void*,void*))(g_base + 0x11E5B70);
    void* (*rend_shared_mat)(void*,void*) = (void*(*)(void*,void*))(g_base + 0x16ADC78);
    void* (*mat_get_shader)(void*,void*)  = (void*(*)(void*,void*))(g_base + 0x1B56338);
    char bnm[80];
    if(!read_str(obj_get_name(go,NULL), bnm, sizeof bnm)) strcpy(bnm,"<noname>");
    void* tr = go_transform(go, NULL);
    V3 p; p.x=p.y=p.z=0; V3 sc; sc.x=sc.y=sc.z=0;
    if (obj_ok(tr)) { p = tr_get_pos(tr,NULL); sc = tr_get_scale(tr,NULL); }
    flog("%s go='%s' active=%d/%d pos=(%.1f,%.1f,%.1f) scale=(%.2f,%.2f,%.2f)",
         tag, bnm, go_active(go,NULL), go_active_h(go,NULL), p.x, p.y, p.z, sc.x, sc.y, sc.z);
    void* gcicmi = fld_p(*(void**)(g_base + 0x2C33E58), 0x0);
    void* rends = obj_ok(gcicmi) ? go_gcic(go, gcicmi) : NULL;
    int rn = obj_ok(rends) ? (int)*(int32_t*)((uintptr_t)rends + 0x18) : -1;
    flog("%s  rendererCount=%d", tag, rn);
    for (int k = 0; k < rn && k < 6; k++) {
        void* rr = *(void**)((uintptr_t)rends + 0x20 + 8*k);
        if (!obj_ok(rr)) continue;
        void* rgo = comp_get_go(rr, NULL);
        void* rm  = rend_shared_mat(rr, NULL);
        void* rs  = obj_ok(rm) ? mat_get_shader(rm, NULL) : NULL;
        char gname[80]; char sname[200];
        if(!read_str(obj_ok(rgo)?obj_get_name(rgo,NULL):NULL, gname, sizeof gname)) strcpy(gname,"<null>");
        if(!read_str(obj_ok(rs)?obj_get_name(rs,NULL):NULL, sname, sizeof sname)) strcpy(sname,"<null>");
        flog("%s  rend[%d] go='%s' goActive=%d shader='%s'",
             tag, k, gname, obj_ok(rgo)?go_active_h(rgo,NULL):-1, sname);
    }
}
// NODEREFRESH (slot 90): BaseNodeController.Refresh(this=a0). After the original runs,
// dump the node's _building GameObject (@0x28 on NodeController): active flags, world
// position, localScale (AnimateSetBuilding tweens scale in -- a stuck 0 scale would be
// invisible regardless of lighting), and every child renderer's material+shader. This is
// the ground truth for "the model is instanced -- what does its draw state look like".
void* hook_90(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    void* r = H[90].orig(a0,a1,a2,a3,a4,a5,a6,a7);
    static int dumps = 0;
    static int diag_nodes = 0;
    static int probe_nodes = 0;
    PROTECT({
        void* go = fld_p(a0, 0x28);
        if (obj_ok(go) && dumps < 24) {
            dumps++;
            char bid[64]; bid[0]=0;
            read_str(fld_p(a0,0x30), bid, sizeof bid);
            char tag[96];
            snprintf(tag, sizeof tag, "BLDGO node=%p id=%s", a0, bid);
            dump_go(tag, go);
        }
#if BLDGDIAG || BLDGACT || BLDGPROBE
        if (obj_ok(go) && diag_nodes < 8) {
            diag_nodes++;
            int  (*go_active)(void*,void*) = (int(*)(void*,void*))(g_base + 0x1B50CF8);
            int  (*go_active_h)(void*,void*) = (int(*)(void*,void*))(g_base + 0x1B50D38);
            void (*go_set_active)(void*,int,void*) = (void(*)(void*,int,void*))(g_base + 0x1B50CA8);
            void* (*go_transform)(void*,void*) = (void*(*)(void*,void*))(g_base + 0x1B50BD8);
            void* (*tr_get_parent)(void*,void*) = (void*(*)(void*,void*))(g_base + 0x16AA7D8);
            void  (*tr_set_position)(void*,V3,void*) = (void(*)(void*,V3,void*))(g_base + 0x16B7CB4);
            void  (*tr_set_local_scale)(void*,V3,void*) = (void(*)(void*,V3,void*))(g_base + 0x16B84CC);
            void* (*comp_get_go)(void*,void*) = (void*(*)(void*,void*))(g_base + 0x1B4BD28);
            void* (*go_gcic)(void*,void*) = (void*(*)(void*,void*))(g_base + 0x11E5B70);
            void  (*rend_get_bounds)(void*,Bounds*,void*) = (void(*)(void*,Bounds*,void*))(g_base + 0x16AD7EC);
            void* (*obj_get_name)(void*,void*) = (void*(*)(void*,void*))(g_base + 0x16A16A0);
            void* chain[13];
            int chain_count = 0;
            void* current = go;
            while (obj_ok(current) && chain_count < 13) {
                chain[chain_count++] = current;
                char name[96]; name[0] = 0;
                if (!read_str(obj_get_name(current,NULL), name, sizeof name)) strcpy(name,"<noname>");
#if BLDGDIAG
                flog("BLDGANC level=%d go='%s' active=%d/%d", chain_count - 1, name,
                     go_active(current,NULL), go_active_h(current,NULL));
#endif
                void* tr = go_transform(current,NULL);
                if (!obj_ok(tr)) break;
                void* parent_tr = tr_get_parent(tr,NULL);
                if (!obj_ok(parent_tr)) break;
                current = comp_get_go(parent_tr,NULL);
            }
#if BLDGDIAG
            void* gcicmi = fld_p(*(void**)(g_base + 0x2C33E58), 0x0);
            void* rends = obj_ok(gcicmi) ? go_gcic(go,gcicmi) : NULL;
            int render_count = obj_ok(rends) ? (int)*(int32_t*)((uintptr_t)rends + 0x18) : -1;
            for (int k = 0; k < render_count && k < 6; k++) {
                void* renderer = *(void**)((uintptr_t)rends + 0x20 + 8*k);
                if (!obj_ok(renderer)) continue;
                void* rgo = comp_get_go(renderer,NULL);
                Bounds bounds;
                bounds.cx=bounds.cy=bounds.cz=bounds.ex=bounds.ey=bounds.ez=0.0f;
                rend_get_bounds(renderer,&bounds,NULL);
                char rname[96]; rname[0] = 0;
                if (!read_str(obj_ok(rgo)?obj_get_name(rgo,NULL):NULL,rname,sizeof rname)) strcpy(rname,"<null>");
                flog("BLDGBND rend=%d go='%s' active=%d center=(%.2f,%.2f,%.2f) extents=(%.2f,%.2f,%.2f)",
                     k, rname, obj_ok(rgo)?go_active_h(rgo,NULL):-1, bounds.cx,bounds.cy,bounds.cz,
                     bounds.ex,bounds.ey,bounds.ez);
            }
#endif
#if BLDGACT
            int before = go_active_h(go,NULL);
            int forced = 0;
            if (before == 0) {
                for (int k = chain_count - 1; k >= 0; k--) {
                    if (obj_ok(chain[k]) && go_active(chain[k],NULL) == 0) {
                        go_set_active(chain[k],1,NULL);
                        forced++;
                    }
                }
            }
            flog("BLDGACT go=%p before=%d after=%d forced=%d", go, before,
                 go_active_h(go,NULL), forced);
#endif
#if BLDGPROBE
            if (probe_nodes++ == 0) {
                void* tr = go_transform(go,NULL);
                if (obj_ok(tr)) {
                    V3 position; position.x=-38.0f; position.y=124.0f; position.z=-278.0f;
                    V3 scale; scale.x=scale.y=scale.z=20.0f;
                    tr_set_position(tr,position,NULL);
                    tr_set_local_scale(tr,scale,NULL);
                    flog("BLDGPROBE go=%p pos=(-38.0,124.0,-278.0) scale=20.0",go);
                }
            }
#endif
        }
#endif
    });
    return r;
}
// ONBLDSET (slot 93): BaseNodeController.OnBuildingSet(this=a0, buildingId=a1, go=a2).
// Logs the model-name string and whether a real GameObject arrived.
void* hook_93(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    PROTECT({
        char nm[64]; nm[0]=0;
        uintptr_t s=(uintptr_t)a1;
        if(s>=0x100000 && !(s&7)){ int32_t l=*(int32_t*)(s+0x10); uint16_t* c=(uint16_t*)(s+0x14);
            if(l>0&&l<62){ int k=0; for(;k<l;k++) nm[k]=(c[k]<128)?(char)c[k]:'?'; nm[k]=0; } }
        flog("ONBLDSET name=%s go=%lx", nm, (uintptr_t)a2);
        if ((uintptr_t)a2) dump_go("ONBLDSET", a2);
    });
    void* r = H[93].orig(a0,a1,a2,a3,a4,a5,a6,a7);
    PROTECT({ if ((uintptr_t)a2) dump_go("ONBLDSET-post", a2); });
    return r;
}
// BLDGSWAP (slot 95): both the DefaultRelic fallback path and the later real-name refresh
// miss the synthetic standalone path. Use the PrefabLibrary's exact child instead.
void* hook_95(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    void* r = H[95].orig(a0,a1,a2,a3,a4,a5,a6,a7);
    void* replacement = NULL;
#if BLDGSWAP
    PROTECT({
        char requested[96]; char resolved[96];
        int from_fifo=0;
        int have_key=0;
        int had_anchor=obj_ok(r);
        requested[0]=resolved[0]=0;
        if (!read_str(a1, requested, sizeof requested)) strcpy(requested, "<null>");
        void* (*lib_contents)(void*,void*) = (void*(*)(void*,void*))(g_base + 0xE3B4D4);
        int (*dict_has)(void*,void*,void*) = (int(*)(void*,void*,void*))(g_base + 0x1FFD7C8);
        void* (*dict_get)(void*,void*,void*) = (void*(*)(void*,void*,void*))(g_base + 0x1FFD490);
        void* (*obj_instantiate)(void*,void*) = (void*(*)(void*,void*))(g_base + 0x16A1AF8);
        void  (*obj_set_name)(void*,void*,void*) = (void(*)(void*,void*,void*))(g_base + 0x16A1760);
        void* (*go_transform)(void*,void*) = (void*(*)(void*,void*))(g_base + 0x1B50BD8);
        void  (*tr_set_parent)(void*,void*,int,void*) = (void(*)(void*,void*,int,void*))(g_base + 0x16B877C);
        void  (*tr_set_local_position)(void*,V3,void*) = (void(*)(void*,V3,void*))(g_base + 0x16B7E0C);
        void  (*tr_set_local_scale)(void*,V3,void*) = (void(*)(void*,V3,void*))(g_base + 0x16B84CC);
        V3    (*tr_get_position)(void*,void*) = (V3(*)(void*,void*))(g_base + 0x16B7C04);
        void  (*tr_set_position)(void*,V3,void*) = (void(*)(void*,V3,void*))(g_base + 0x16B7CB4);
        void  (*go_set_active)(void*,int,void*) = (void(*)(void*,int,void*))(g_base + 0x1B50CA8);
        void* (*comp_get_go)(void*,void*) = (void*(*)(void*,void*))(g_base + 0x1B4BD28);
        void* (*go_gcic)(void*,void*) = (void*(*)(void*,void*))(g_base + 0x11E5B70);
        void* lib = fld_p(a0, 0x158);
        void* cont = obj_ok(lib) ? lib_contents(lib, NULL) : NULL;
        void* mi_has = fld_p(*(void**)(g_base + 0x2C38CC0), 0x0);
        void* mi_get = fld_p(*(void**)(g_base + 0x2C24188), 0x0);
        void* selected_key=NULL;
        void* prefab=NULL;
        if (obj_ok(cont) && obj_ok(mi_has) && g_strnew) {
            selected_key=g_strnew(requested);
            if (obj_ok(selected_key) && dict_has(cont,selected_key,mi_has)==1) {
                snprintf(resolved,sizeof resolved,"%s",requested);
                have_key=1;
            } else if (strcmp(requested,"DefaultRelic")==0 && bldg_fifo_pop(resolved,sizeof resolved)) {
                from_fifo=1;
                selected_key=g_strnew(resolved);
                have_key=obj_ok(selected_key) && dict_has(cont,selected_key,mi_has)==1;
            }
        }
        flog("BLDGSWAP requested='%s' key='%s' fifo=%d anchor=%p", requested,
             resolved[0]?resolved:"<none>", from_fifo, r);
        if (have_key && obj_ok(mi_get)) prefab=dict_get(cont,selected_key,mi_get);
        if (!have_key || !obj_ok(prefab)) {
            flog("BLDGSWAP outcome=decline key='%s' haveKey=%d prefab=%p", resolved, have_key, prefab);
        } else if (had_anchor) {
            // Capture these before the clone exists so only DefaultRelic renderer objects hide.
            void* gcicmi=fld_p(*(void**)(g_base+0x2C33E58),0x0);
            void* oldrends=obj_ok(gcicmi)?go_gcic(r,gcicmi):NULL;
            int oldcount=obj_ok(oldrends)?*(int32_t*)((uintptr_t)oldrends+0x18):-1;
            void* clone=obj_instantiate(prefab,NULL);
            void* ctr=obj_ok(clone)?go_transform(clone,NULL):NULL;
            void* rtr=go_transform(r,NULL);
            if (obj_ok(clone) && obj_ok(ctr) && obj_ok(rtr)) {
                bldg_track(r);
                bldg_track(clone);
                V3 zero; zero.x=zero.y=zero.z=0.0f;
                tr_set_parent(ctr,rtr,0,NULL);
                tr_set_local_position(ctr,zero,NULL);
#if BLDGSCALE
                V3 scale; scale.x=scale.y=scale.z=BLDGSCALE_VAL;
                tr_set_local_scale(ctr,scale,NULL);
                flog("BLDGSCALE applied key='%s' mult=%.2f",resolved,BLDGSCALE_VAL);
#endif
                go_set_active(clone,1,NULL);
                char clonename[128]; snprintf(clonename,sizeof clonename,"BLDGSWAP-clone:%s",resolved);
                void* managed_name=g_strnew?g_strnew(clonename):NULL;
                if (obj_ok(managed_name)) obj_set_name(clone,managed_name,NULL);
                if (obj_ok(oldrends) && oldcount>=0 && oldcount<=256) for(int i=0;i<oldcount;i++) {
                    void* rr=*(void**)((uintptr_t)oldrends+0x20+8*i);
                    void* rgo=obj_ok(rr)?comp_get_go(rr,NULL):NULL;
                    if(obj_ok(rgo)) go_set_active(rgo,0,NULL);
                }
                flog("BLDGSWAP outcome=anchor-swap key='%s' clone=%p hiddenRenderers=%d",resolved,clone,oldcount);
                dump_go("BLDGSWAP-clone",clone);
            } else flog("BLDGSWAP outcome=anchor-clone-failed key='%s' clone=%p ctr=%p anchorTr=%p",resolved,clone,ctr,rtr);
        } else {
            void* clone=obj_instantiate(prefab,NULL);
            void* ctr=obj_ok(clone)?go_transform(clone,NULL):NULL;
            if (obj_ok(clone)) {
                bldg_track(clone);
                if (obj_ok(a1)) obj_set_name(clone,a1,NULL);
                void* parent_go=fld_p(a0,0xB0);
                void* parent_tr=obj_ok(parent_go)?go_transform(parent_go,NULL):NULL;
                if (obj_ok(ctr) && obj_ok(parent_tr)) tr_set_parent(ctr,parent_tr,1,NULL);
                if (obj_ok(ctr) && obj_ok(a2)) { V3 pos=tr_get_position(a2,NULL); tr_set_position(ctr,pos,NULL); }
#if BLDGSCALE
                if (obj_ok(ctr)) { V3 scale; scale.x=scale.y=scale.z=BLDGSCALE_VAL; tr_set_local_scale(ctr,scale,NULL); flog("BLDGSCALE applied key='%s' mult=%.2f",resolved,BLDGSCALE_VAL); }
#endif
                go_set_active(clone,1,NULL);
                replacement=clone;
                flog("BLDGSWAP outcome=return-clone key='%s' clone=%p parent=%p node=%p",resolved,clone,parent_tr,a2);
                dump_go("BLDGSWAP-return",clone);
            } else flog("BLDGSWAP outcome=return-clone-failed key='%s' clone=%p",resolved,clone);
        }
    });
#endif
    return replacement ? replacement : r;
}
// BLDGLEAVE (slot 96): BaseBoard.LeaveBoard is the confirmed base-board teardown path
// (it calls SafeSetActive(false) on the regular board root).  BLDGACT is deliberately a
// cosmetic workaround for the game's disabled-GameObject behaviour, not a root-cause fix;
// the fallback anchors/clones it touches live under AssetManager and therefore need the same
// explicit deactivation before a STORY board reuses the scene/camera.
void* hook_96(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    // RCINIT re-arms these on the next BaseBoard entry.  Clear them before the
    // outgoing board can be torn down so ProcessTouch cannot dispatch a stale card.
    g_base_tap_card=NULL;
    g_base_tap_go=NULL;
#if BLDGLEAVE
    PROTECT({
        void (*go_set_active)(void*,int,void*) =
            (void(*)(void*,int,void*))(g_base + 0x1B50CA8);
        int hidden=0;
        for (int i=0;i<g_bldg_tracked_count;i++) {
            if (obj_ok(g_bldg_tracked[i])) { go_set_active(g_bldg_tracked[i],0,NULL); hidden++; }
        }
        flog("BLDGLEAVE base=%p hidden=%d tracked=%d",a0,hidden,g_bldg_tracked_count);
        g_bldg_tracked_count=0;
        g_bldg_fifo_count=0;
        g_bldg_fifo_head=0;
    });
#endif
    return H[96].orig(a0,a1,a2,a3,a4,a5,a6,a7);
}
// Base-builder entry markers (slots 97-101): intentionally log only, then execute originals.
#define MK_BASEMARK(n) \
void* hook_##n(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){ \
    LOG("%s this=%p arg1=%p arg2=%p", H[n].tag, a0, a1, a2); \
    return H[n].orig(a0,a1,a2,a3,a4,a5,a6,a7); \
}
MK_BASEMARK(97) MK_BASEMARK(98) MK_BASEMARK(99) MK_BASEMARK(100) MK_BASEMARK(101)
#undef MK_BASEMARK
// Popup initialization bisect markers (slots 115-120): no behavioural changes.
#define MK_POPMARK(n) \
void* hook_##n(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){ \
    LOG("%s this=%p arg1=%p arg2=%p", H[n].tag, a0, a1, a2); \
    return H[n].orig(a0,a1,a2,a3,a4,a5,a6,a7); \
}
MK_POPMARK(115) MK_POPMARK(116) MK_POPMARK(117) MK_POPMARK(119) MK_POPMARK(120)
#undef MK_POPMARK
// The retail BaseEditBuildingPopup prefab present in this APK has no serialized
// button SCI references.  BuildPresentation first dereferences _addButtonSCI@0x70
// and throws; UpdatePresentation would repeat that same UI-only setup.  These two
// hooks preserve the popup lifecycle by taking the methods' normal successful tail:
// SafeInvoke(onReady).  They intentionally do not fabricate UI objects or alter
// authored base data; the visible base board remains under the open popup.
static void popup_safe_ready(void* onReady) {
    if (onReady) ((void(*)(void*,void*))(g_base + 0xDC8048))(onReady, NULL);
}
void* hook_118(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    LOG("POPBUILD this=%p onReady=%p (missing prefab SCI workaround)",a0,a1);
    popup_safe_ready(a1);
    return NULL;
}
void* hook_121(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    LOG("POPUPDATE this=%p onReady=%p (missing prefab SCI workaround)",a0,a1);
    popup_safe_ready(a1);
    return NULL;
}
// Card-state logger used only for RE.  @0x88 is true exactly when Init receives a
// BaseNodeController provider; @0x48 selects the active-FX branch.  The remaining
// fields are enough to tell whether the card has a tile and a collider component.
static void log_relic_card(const char* tag, void* card){
    PROTECT({
        if (!obj_ok(card)) { LOG("%s card=%p invalid", tag, card); return; }
        void* cardgo=((void*(*)(void*,void*))(g_base+0x1B4BD28))(card,NULL);
        void* collider=*(void**)((uintptr_t)card+0x50);
        void* collidergo=collider ? ((void*(*)(void*,void*))(g_base+0x1B4BD28))(collider,NULL) : NULL;
        void* listener=collidergo ? ((void*(*)(void*,void*))(g_base+0x1BF8C0C))(collidergo,NULL) : NULL;
        int cardactive=cardgo ? ((int(*)(void*,void*))(g_base+0x1B50D38))(cardgo,NULL) : -1;
        int collideractive=collidergo ? ((int(*)(void*,void*))(g_base+0x1B50D38))(collidergo,NULL) : -1;
        int layer=collidergo ? ((int(*)(void*,void*))(g_base+0x1B50C18))(collidergo,NULL) : -1;
        int colenabled=collider ? ((int(*)(void*,void*))(g_base+0x21B5694))(collider,NULL) : -1;
        int trigger=collider ? ((int(*)(void*,void*))(g_base+0x21B5764))(collider,NULL) : -1;
        float* p = (float*)((uintptr_t)card + 0x58);
        LOG("%s card=%p gate88=%u flag48=%u cardgo=%p active=%d comp50=%p colgo=%p active=%d layer=%d colenabled=%d trigger=%d listener=%p onclick=%p tilec0=%p p58=(%.1f,%.1f) s60=(%.1f,%.1f) p68=(%.1f,%.1f) s70=(%.1f,%.1f) p78=(%.1f,%.1f) s80=(%.1f,%.1f)",
            tag, card, *(uint8_t*)((uintptr_t)card+0x88), *(uint8_t*)((uintptr_t)card+0x48),
            cardgo,cardactive,collider,collidergo,collideractive,layer,colenabled,trigger,listener,listener?*(void**)((uintptr_t)listener+0x28):NULL,*(void**)((uintptr_t)card+0xc0),
            p[0],p[1],p[2],p[3],p[4],p[5],p[6],p[7],p[8],p[9],p[10],p[11]);
    });
}
void* hook_103(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    LOG("RCINIT card=%p tile=%p provider=%p", a0, a1, a2);
    void* r=H[103].orig(a0,a1,a2,a3,a4,a5,a6,a7);
    PROTECT({
        void* building=((void*(*)(void*,void*))(g_base+0x1094230))(a1,NULL);
        if (!g_base_tap_card && building) {
            g_base_tap_card=a0;
            g_base_tap_go=((void*(*)(void*,void*))(g_base+0x1B4BD28))(a0,NULL);
            LOG("BASETAPFIX armed card=%p tile=%p building=%p",g_base_tap_card,a1,building);
        }
    });
    log_relic_card("RCINIT_DONE",a0); return r;
}
#define MK_RELICMARK(n) \
void* hook_##n(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){ \
    log_relic_card(H[n].tag,a0); return H[n].orig(a0,a1,a2,a3,a4,a5,a6,a7); \
}
MK_RELICMARK(104) MK_RELICMARK(105) MK_RELICMARK(106) MK_RELICMARK(107) MK_RELICMARK(108)
#undef MK_RELICMARK
#define MK_CARDMARK(n) \
void* hook_##n(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){ \
    LOG("%s this=%p arg1=%p",H[n].tag,a0,a1); return H[n].orig(a0,a1,a2,a3,a4,a5,a6,a7); \
}
MK_CARDMARK(109) MK_CARDMARK(110) MK_CARDMARK(111)
#undef MK_CARDMARK
void* hook_112(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    LOG("RCAWAKE card=%p",a0); void* r=H[112].orig(a0,a1,a2,a3,a4,a5,a6,a7); log_relic_card("RCAWAKE_DONE",a0); return r;
}
void* hook_113(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    LOG("UICLICK listener=%p onClick=%p",a0,obj_ok(a0)?*(void**)((uintptr_t)a0+0x28):NULL);
    return H[113].orig(a0,a1,a2,a3,a4,a5,a6,a7);
}
void* hook_114(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    void* r=H[114].orig(a0,a1,a2,a3,a4,a5,a6,a7);
    // ProcessTouch receives `pressed` in w1; dispatch only on that edge, not the
    // matching release/update call for the same Android touch.
    if ((uintptr_t)a1 & 1) {
        // UICamera.get_isOverUI is static: x0 is its hidden MethodInfo* (NULL).
        // If NGUI handled the touch (including the top navigation), preserve that
        // UI action and do not turn it into a base-card click.
        int over=1;
        int dispatched=0;
        PROTECT({ over=((int(*)(void*))(g_base+0x1F7B2EC))(NULL); });
        if (!over && g_base_tap_card && !g_base_tap_dispatching) {
            g_base_tap_dispatching=1;
            dispatched=1;
            H[108].orig(g_base_tap_card,g_base_tap_go,NULL,NULL,NULL,NULL,NULL,NULL);
            // BaseBoard.OnCardTapped has a shipped RelicCard/BossCard type mismatch in
            // this client build (it logs CARDTAP then aborts before the tile handler).
            // The normal destination is nevertheless known and receives the real tile.
            PROTECT({
                void* node=*(void**)((uintptr_t)g_base_tap_card+0x38); // QuestCard._provider
                void* board=node ? *(void**)((uintptr_t)node+0x48) : NULL; // NodeController._provider
                void* tile=*(void**)((uintptr_t)g_base_tap_card+0xC0);
                LOG("BASETAPFIX -> HandleTileBuildingInteraction board=%p tile=%p",board,tile);
                if (board && tile) H[98].orig(board,tile,NULL,NULL,NULL,NULL,NULL,NULL);
            });
            g_base_tap_dispatching=0;
        }
        LOG("BASETAPFIX gate overUI=%d card=%p -> %s",over,g_base_tap_card,
            dispatched ? "dispatch" : "skip");
    }
    return r;
}
// FTEBASEFIX (slot 102): permit the base-edit branch only; authored tutorial state is
// otherwise unchanged and the original result is retained for every other branch.
void* hook_102(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
    void* r = H[102].orig(a0,a1,a2,a3,a4,a5,a6,a7);
    char tutorial[32]={0}, branch[48]={0};
    PROTECT({ read_str(a0,tutorial,sizeof tutorial); read_str(a1,branch,sizeof branch); });
    if (!strcmp(tutorial,"FTE") && !strcmp(branch,"FTEBaseCrystal")) {
        LOG("FTEBASEFIX forced complete tutorial=%s branch=%s",tutorial,branch);
        return (void*)1;
    }
    return r;
}
// CAMFRAME (slot 94): a0 is the base's BaseCameraController. get__CurrentPosOffset runs every
// frame from UpdateCamera (right after it applies the FOV), so it is a convenient place to poke
// the controller's FOV range fields (min@0x1a0, max@0x1a4) to a telephoto value -- magnifying
// the distant buildings on the next frame without moving the camera off the sky-framed sightline.
void* hook_94(void* a0,void* a1,void* a2,void* a3,void* a4,void* a5,void* a6,void* a7){
#if CAMFRAME
    PROTECT({
        if (obj_ok(a0)) {
            *(float*)((uintptr_t)a0+0x1a0)=CAMFOV;   // fov range min
            *(float*)((uintptr_t)a0+0x1a4)=CAMFOV;   // fov range max
        }
    });
#endif
    return H[94].orig(a0,a1,a2,a3,a4,a5,a6,a7);
}
static void* handlers[] = { hook_0,hook_1,hook_2,hook_3,hook_4,hook_5,hook_6,hook_7,hook_8,
    hook_9,hook_10,hook_11,hook_12,hook_13,hook_14,hook_15,hook_16,hook_17,hook_18,hook_19,hook_20,hook_21,
    hook_22,hook_23,hook_24,hook_25,hook_26,hook_27,hook_28,hook_29,hook_30,
    hook_31,hook_32,hook_33,hook_34,hook_35,hook_36,hook_37,hook_38,hook_39,hook_40,hook_41,hook_42,hook_43,
    hook_44,hook_45,hook_46,hook_47,hook_48,hook_49,hook_50,hook_51,hook_52,
    hook_53,hook_54,hook_55,hook_56,hook_57,hook_58,
    hook_59,hook_60,hook_61,hook_62,hook_63,hook_64,hook_65,hook_66,
    hook_67,hook_68,hook_69,hook_70,hook_71,hook_72,hook_73,hook_74,hook_75,hook_76,hook_77,hook_78,
    hook_79,hook_80,hook_81,hook_82,hook_83,hook_84,hook_85,hook_86,hook_87,hook_88,
    hook_89,hook_90,hook_91,hook_92,hook_93,hook_94,hook_95,hook_96,
    hook_97,hook_98,hook_99,hook_100,hook_101,hook_102,
    hook_103,hook_104,hook_105,hook_106,hook_107,hook_108,hook_109,hook_110,hook_111,hook_112,hook_113,hook_114,
    hook_115,hook_116,hook_117,hook_118,hook_119,hook_120,hook_121 };

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

// Single 32-bit instruction rewrite at (g_base+rva). Installed before the target is first
// executed (constructor thread runs at app start; the patched funcs run only later), so
// libnb's lazy translation picks up the new word -- same guarantee as the inline hooks.
static void poke32(uintptr_t rva, uint32_t word){
    uint8_t* t = (uint8_t*)(g_base + rva);
    uintptr_t pg = (uintptr_t)t & ~0xFFFUL;
    if (mprotect((void*)pg, 0x2000, PROT_READ|PROT_WRITE|PROT_EXEC) != 0){ LOG("poke mprotect fail 0x%lx", (long)rva); return; }
    uint32_t old = *(uint32_t*)t;
    *(uint32_t*)t = word;
    __builtin___clear_cache((char*)t, (char*)t + 4);
    LOG("poked 0x%lx : %08x -> %08x", (long)rva, old, word);
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
    // FIXSYN (session 10): BCGBlueprintBase.get_SynergyBonuses (@0xC17198) throws
    // NullReferenceException when this._synergyBonuses (List<string> @0xE0) is null -- which it
    // ALWAYS is offline (the blueprint ctor never parses a synergy key). Adding a bot to the STORY
    // squad runs TeamData.RefreshSynergyBonusData -> b__56_0 -> get_SynergyBonuses on each hero's
    // blueprint and the throw surfaces as the "unknown error" dialog. The getter already allocates
    // a fresh empty result List<string> (x19) BEFORE the null-check and returns it at 0xC17340;
    // the null branch instead jumps to the throw at 0xC17370. Redirect that one `cbz x0` from the
    // throw to the normal empty-list return -> get_SynergyBonuses returns an empty list for a null
    // field instead of throwing. (Same spirit as the Tags empty-collection fix; done as a targeted
    // instruction poke rather than fabricating a List<string> whose RGCTX may be uninitialized.)
    poke32(0xC17278, 0xB4000640);   // cbz x0, 0xC17370 (throw) -> cbz x0, 0xC17340 (return empty)
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
