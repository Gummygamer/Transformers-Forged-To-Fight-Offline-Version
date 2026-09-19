/* Original interoperability adapter. No game implementation or layout is embedded.
 * Called only from an existing managed SetAction invocation on its runtime thread.
 * Keep metadata per thread; never cache the Simulation object across fights.
 */
#ifndef TFTF_MANAGED_INPUT_H
#define TFTF_MANAGED_INPUT_H
#include <dlfcn.h>
#include <math.h>
#include <string.h>

typedef struct {
    void *(*domain_get)(void);
    const void *(*domain_assembly_open)(void *, const char *);
    const void *(*assembly_get_image)(const void *);
    void *(*class_from_name)(const void *, const char *, const char *);
    void *(*object_get_class)(void *);
    const char *(*class_get_name)(void *);
    void *(*class_get_declaring_type)(void *);
    void *(*class_get_field_from_name)(void *, const char *);
    const void *(*field_get_type)(void *);
    int (*type_get_type)(const void *);
    void *(*class_from_type)(const void *);
    int (*field_get_flags)(void *);
    void (*field_get_value)(void *, void *, void *);
    void (*field_static_get_value)(void *, void *);
    void (*field_set_value)(void *, void *, void *);
} ManagedInputAPI;

typedef struct {
    ManagedInputAPI api;
    void *simulation, *player_input, *queue_class;
    void *instance, *time, *timestamp;
} ManagedInput;

/* A resolver is injectable so host tests exercise the same binding path. */
static int managed_input_bind(ManagedInput *state, void *(*resolve)(const char *)) {
    ManagedInputAPI api = {0};
#define BIND(name) do { \
    *(void **)(&api.name) = resolve("il2cpp_" #name); \
    if (!api.name) return 0; \
} while (0)
    BIND(domain_get); BIND(domain_assembly_open); BIND(assembly_get_image);
    BIND(class_from_name); BIND(object_get_class); BIND(class_get_name);
    BIND(class_get_declaring_type); BIND(class_get_field_from_name);
    BIND(field_get_type); BIND(type_get_type); BIND(class_from_type);
    BIND(field_get_flags); BIND(field_get_value); BIND(field_static_get_value);
    BIND(field_set_value);
#undef BIND
    state->api = api;
    return 1;
}

/* CLI metadata element codes: SINGLE=0x0c, CLASS=0x12; field STATIC=0x10.
 * Validate before reading into a float/pointer-sized destination.
 */
static int managed_input_field(ManagedInputAPI *api, void *field, int type, int is_static) {
    if (!field) return 0;
    const void *field_type = api->field_get_type(field);
    return field_type && api->type_get_type(field_type) == type &&
        !!(api->field_get_flags(field) & 0x10) == is_static;
}

/* -1 = unavailable (no write), 0 = original retained, 1 = fallback applied.
 * This preserves the project's existing 0.01-second threshold and caller's
 * authored fallback; it does not import a retail default from the old client.
 */
static int managed_input_apply(ManagedInput *s, void *queue, float fallback, float *window) {
    ManagedInputAPI *a = &s->api;
    if (!a->domain_get || !queue || !isfinite(fallback) || fallback <= 0) return -1;
    if (!s->simulation) {
        void *domain = a->domain_get();
        if (!domain) return -1;
        const void *assembly = a->domain_assembly_open(domain, "Assembly-CSharp");
        if (!assembly) return -1;
        const void *image = a->assembly_get_image(assembly);
        if (!image) return -1;
        void *sim = a->class_from_name(image, "", "Simulation");
        void *input = a->class_from_name(image, "", "PlayerInput");
        if (!sim || !input) return -1;
        void *instance = a->class_get_field_from_name(sim, "_instance");
        void *time = a->class_get_field_from_name(sim, "<Time>k__BackingField");
        if (!managed_input_field(a, instance, 0x12, 1) ||
            a->class_from_type(a->field_get_type(instance)) != sim ||
            !managed_input_field(a, time, 0x0c, 0)) return -1;
        s->player_input = input;
        s->instance = instance;
        s->time = time;
        s->simulation = sim;
    }
    void *klass = a->object_get_class(queue);
    if (!klass) return -1;
    if (klass != s->queue_class) {
        const char *name = a->class_get_name(klass);
        if (!name || strcmp(name, "QueuedAction") ||
            a->class_get_declaring_type(klass) != s->player_input) return -1;
        void *timestamp = a->class_get_field_from_name(klass, "<TimeStamp>k__BackingField");
        if (!managed_input_field(a, timestamp, 0x0c, 0)) return -1;
        s->timestamp = timestamp;
        s->queue_class = klass;
    }
    void *simulation = NULL;
    a->field_static_get_value(s->instance, &simulation);
    if (!simulation || a->object_get_class(simulation) != s->simulation) return -1;
    float now, timestamp;
    a->field_get_value(simulation, s->time, &now);
    a->field_get_value(queue, s->timestamp, &timestamp);
    if (!isfinite(now) || now < 0 || !isfinite(timestamp)) return -1;
    *window = timestamp - now;
    if (*window > 0.01f) return 0;
    timestamp = now + fallback;
    if (!isfinite(timestamp) || timestamp <= now) return -1;
    a->field_set_value(queue, s->timestamp, &timestamp);
    *window = fallback;
    return 1;
}

static void *managed_input_symbol(const char *name) {
    void *symbol = dlsym(RTLD_DEFAULT, name);
    if (symbol) return symbol;
    void *handle = dlopen("libil2cpp.so", RTLD_NOW | RTLD_NOLOAD);
    if (!handle) return NULL;
    symbol = dlsym(handle, name);
    dlclose(handle);
    return symbol;
}

static void managed_input_after_set_action(void *queue) {
    static __thread ManagedInput state;
    static __thread int diagnostics;
    float window = 0;
    int result = -1;
    if (state.api.domain_get || managed_input_bind(&state, managed_input_symbol))
        result = managed_input_apply(&state, queue, 0.2f, &window);
    if (diagnostics < 4) {
        diagnostics++;
        if (result < 0) flog("SETACTFIX managed fields unavailable; original retained");
        else flog("SETACTFIX managed window=%.3f (%s)", window,
                  result ? "fallback" : "kept");
    }
}
#endif
