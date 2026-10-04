/* Queued-action adapter built on the shared IL2CPP metadata runtime layer. */
#ifndef TFTF_MANAGED_INPUT_H
#define TFTF_MANAGED_INPUT_H
#include <math.h>
#include <string.h>
#include "managed_runtime.h"

typedef struct {
    ManagedRuntime runtime;
    void *simulation, *player_input, *queue_class;
    void *instance, *time, *timestamp;
} ManagedInput;

static int managed_input_bind(ManagedInput *state, void *(*resolve)(const char *)) {
    return state && managed_runtime_bind(&state->runtime, resolve);
}

/* -1 = unavailable (no write), 0 = original retained, 1 = fallback applied. */
static int managed_input_apply(ManagedInput *s, void *queue, float fallback, float *window) {
    ManagedRuntime *runtime = s ? &s->runtime : NULL;
    ManagedRuntimeAPI *a = runtime ? &runtime->api : NULL;
    if (!runtime || !a || !a->domain_get || !queue || !window ||
        !isfinite(fallback) || fallback <= 0) return -1;
    if (!s->simulation) {
        void *sim = managed_runtime_class(runtime, "", "Simulation");
        void *input = managed_runtime_class(runtime, "", "PlayerInput");
        if (!sim || !input) return -1;
        void *instance = managed_runtime_field(runtime, sim, "_instance", 0x12, 1);
        void *time = managed_runtime_field(runtime, sim, "<Time>k__BackingField", 0x0c, 0);
        if (!instance || !time || a->class_from_type(a->field_get_type(instance)) != sim) return -1;
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
        void *timestamp = managed_runtime_field(runtime, klass,
                                                "<TimeStamp>k__BackingField", 0x0c, 0);
        if (!timestamp) return -1;
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

static void managed_input_after_set_action(void *queue) {
    static __thread ManagedInput state;
    static __thread int diagnostics;
    float window = 0;
    int result = -1;
    if (state.runtime.api.domain_get || managed_input_bind(&state, managed_runtime_symbol))
        result = managed_input_apply(&state, queue, 0.2f, &window);
    if (diagnostics < 4) {
        diagnostics++;
        if (result < 0) flog("SETACTFIX managed fields unavailable; original retained");
        else flog("SETACTFIX managed window=%.3f (%s)", window,
                  result ? "fallback" : "kept");
    }
}
#endif
