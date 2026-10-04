/* Synthetic metadata and objects only; no extracted game fixtures. */
#include <assert.h>
#include <stddef.h>
#include <stdio.h>
#include <stdarg.h>
static void flog(const char *fmt, ...) { (void)fmt; }
#include "managed_input.h"

static int sim_class, input_class, queue_class, wrong_class, assembly, image, domain;
typedef struct { int type, flags; const char *name; } Field;
static Field instance_field = {0x12, 0x10, "_instance"};
static Field time_field = {0x0c, 0, "<Time>k__BackingField"};
static Field stamp_field = {0x0c, 0, "<TimeStamp>k__BackingField"};
/* Deliberately unlike either game ABI: adapter must let the API find values. */
typedef struct { char padding[47]; void *klass; float value; } Object;
static Object sim = {{0}, &sim_class, 10}, queue = {{0}, &queue_class, 10};
static Object *current = &sim;
static int ready = 1, writes;
static const char *missing_field, *missing_symbol;

static void *fake_domain_get(void) { return ready ? &domain : NULL; }
static const void *fake_domain_assembly_open(void *d, const char *name) {
    assert(d == &domain);
    assert(!strcmp(name, "Assembly-CSharp"));
    return &assembly;
}
static const void *fake_assembly_get_image(const void *a) {
    assert(a == &assembly); return &image;
}
static void *fake_class_from_name(const void *i, const char *ns, const char *name) {
    assert(i == &image && !*ns);
    if (!strcmp(name, "Simulation")) return &sim_class;
    if (!strcmp(name, "PlayerInput")) return &input_class;
    return NULL;
}
static void *fake_object_get_class(void *object) { return ((Object *)object)->klass; }
static const char *fake_class_get_name(void *c) {
    return c == &queue_class ? "QueuedAction" : "Other";
}
static void *fake_class_get_declaring_type(void *c) {
    return c == &queue_class ? &input_class : NULL;
}
static void *fake_class_get_field_from_name(void *c, const char *name) {
    if (missing_field && !strcmp(name, missing_field)) return NULL;
    if (c == &sim_class && !strcmp(name, instance_field.name)) return &instance_field;
    if (c == &sim_class && !strcmp(name, time_field.name)) return &time_field;
    if (c == &queue_class && !strcmp(name, stamp_field.name)) return &stamp_field;
    return NULL;
}
static const void *fake_field_get_type(void *f) { return f; }
static int fake_type_get_type(const void *t) { return ((const Field *)t)->type; }
static void *fake_class_from_type(const void *t) {
    return t == &instance_field ? &sim_class : NULL;
}
static int fake_field_get_flags(void *f) { return ((Field *)f)->flags; }
static void fake_field_get_value(void *object, void *f, void *out) {
    assert(f == &time_field || f == &stamp_field);
    memcpy(out, &((Object *)object)->value, sizeof(float));
}
static void fake_field_static_get_value(void *f, void *out) {
    assert(f == &instance_field); memcpy(out, &current, sizeof(current));
}
static void fake_field_set_value(void *object, void *f, void *value) {
    assert(object == &queue && f == &stamp_field);
    memcpy(&queue.value, value, sizeof(float)); writes++;
}
static void *resolve(const char *name) {
    if (missing_symbol && !strcmp(name, missing_symbol)) return NULL;
#define EXPORT(n) if (!strcmp(name, "il2cpp_" #n)) return (void *)fake_##n
    EXPORT(domain_get); EXPORT(domain_assembly_open); EXPORT(assembly_get_image);
    EXPORT(class_from_name); EXPORT(object_get_class); EXPORT(class_get_name);
    EXPORT(class_get_declaring_type); EXPORT(class_get_field_from_name);
    EXPORT(field_get_type); EXPORT(type_get_type); EXPORT(class_from_type);
    EXPORT(field_get_flags); EXPORT(field_get_value); EXPORT(field_static_get_value);
    EXPORT(field_set_value);
#undef EXPORT
    return NULL;
}
static ManagedInput fresh(void) {
    ManagedInput s = {0}; assert(managed_input_bind(&s, resolve)); return s;
}
static void unavailable(ManagedInput *s) {
    float window; int before = writes;
    assert(managed_input_apply(s, &queue, 0.2f, &window) == -1);
    assert(writes == before);
}
int main(void) {
    float window;
    ManagedInput null_state = {0};
    assert(!managed_input_bind(NULL, resolve));
    assert(!managed_input_bind(&null_state, NULL));
    ManagedInput s = fresh();
    queue.value = 10.4f;
    assert(managed_input_apply(&s, &queue, 0.2f, &window) == 0 && writes == 0);
    assert(queue.value == 10.4f);
    queue.value = 10;
    assert(managed_input_apply(&s, &queue, 0.2f, &window) == 1 && writes == 1);
    assert(queue.value == 10.2f && window == 0.2f);
    queue.value = 9;
    assert(managed_input_apply(&s, &queue, 0.2f, &window) == 1);
    queue.value = 10.005f;
    assert(managed_input_apply(&s, &queue, 0.2f, &window) == 1);
    queue.value = 10.02f;
    assert(managed_input_apply(&s, &queue, 0.2f, &window) == 0);
    current = NULL; unavailable(&s);
    Object next = {{0}, &sim_class, 20}; current = &next; queue.value = 20;
    assert(managed_input_apply(&s, &queue, 0.2f, &window) == 1);
    assert(queue.value == 20.2f); /* singleton refreshed, not stale */
    current = &sim;
    queue.klass = &wrong_class; unavailable(&s); queue.klass = &queue_class;
    sim.value = NAN; unavailable(&s); sim.value = -1; unavailable(&s); sim.value = 10;
    queue.value = INFINITY; unavailable(&s); queue.value = 10;
    current = &queue; unavailable(&s); current = &sim;
    s = fresh(); ready = 0; unavailable(&s); ready = 1;
    assert(managed_input_apply(&s, &queue, 0.2f, &window) == 1);
    const char *names[] = {instance_field.name, time_field.name, stamp_field.name};
    for (size_t i = 0; i < 3; i++) {
        s = fresh(); missing_field = names[i]; unavailable(&s); missing_field = NULL;
        assert(managed_input_apply(&s, &queue, 0.2f, &window) == 0);
    }
    s = fresh(); time_field.type = 0x0d; unavailable(&s); time_field.type = 0x0c;
    s = fresh(); stamp_field.flags = 0x10; unavailable(&s); stamp_field.flags = 0;
    s = fresh(); instance_field.flags = 0; unavailable(&s); instance_field.flags = 0x10;
    s = (ManagedInput){0}; missing_symbol = "il2cpp_field_set_value";
    assert(!managed_input_bind(&s, resolve) && !s.runtime.api.domain_get);
    unavailable(&s); missing_symbol = NULL;
    s = fresh();
    assert(managed_input_apply(&s, NULL, 0.2f, &window) == -1);
    assert(managed_input_apply(&s, &queue, NAN, &window) == -1);
    managed_input_after_set_action(NULL); /* no runtime exports on host */
    puts("managed input: all synthetic runtime tests passed");
    return 0;
}
