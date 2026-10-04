/* Small, version-checked access layer for Unity IL2CPP metadata exports.
 *
 * This header contains no game layout constants or recovered implementation. It
 * resolves classes and fields by metadata names so hook code can share one path
 * on arm64 and ARMv7. The generated/local analysis tools provide the names; this
 * layer performs the runtime validation.
 */
#ifndef TFTF_MANAGED_RUNTIME_H
#define TFTF_MANAGED_RUNTIME_H
#include <dlfcn.h>
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
} ManagedRuntimeAPI;

typedef struct {
    ManagedRuntimeAPI api;
    void *domain;
    const void *image;
    const char *assembly_name;
} ManagedRuntime;

/* The resolver is injectable for host tests and can be backed by dlsym in-game. */
static int managed_runtime_bind(ManagedRuntime *runtime, void *(*resolve)(const char *)) {
    if (!runtime || !resolve) return 0;
    ManagedRuntimeAPI api = {0};
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
    runtime->api = api;
    runtime->domain = NULL;
    runtime->image = NULL;
    runtime->assembly_name = NULL;
    return 1;
}

static const void *managed_runtime_image(ManagedRuntime *runtime, const char *assembly_name) {
    if (!runtime || !assembly_name || !runtime->api.domain_get) return NULL;
    if (!runtime->domain) runtime->domain = runtime->api.domain_get();
    if (!runtime->domain) return NULL;
    const void *assembly = runtime->api.domain_assembly_open(runtime->domain, assembly_name);
    if (!assembly) return NULL;
    runtime->image = runtime->api.assembly_get_image(assembly);
    runtime->assembly_name = runtime->image ? assembly_name : NULL;
    return runtime->image;
}

static void *managed_runtime_class(ManagedRuntime *runtime, const char *namespace_name,
                                   const char *class_name) {
    if (!runtime || !namespace_name || !class_name) return NULL;
    if (!runtime->image && !managed_runtime_image(runtime, "Assembly-CSharp")) return NULL;
    return runtime->api.class_from_name(runtime->image, namespace_name, class_name);
}

/* CLI metadata element codes: SINGLE=0x0c, CLASS=0x12; FIELD_ATTRIBUTE_STATIC=0x10. */
static int managed_runtime_field_matches(ManagedRuntime *runtime, void *field,
                                         int type_code, int is_static) {
    if (!runtime || !field) return 0;
    const void *field_type = runtime->api.field_get_type(field);
    return field_type && runtime->api.type_get_type(field_type) == type_code &&
        !!(runtime->api.field_get_flags(field) & 0x10) == is_static;
}

static void *managed_runtime_field(ManagedRuntime *runtime, void *klass,
                                   const char *field_name, int type_code, int is_static) {
    if (!runtime || !klass || !field_name) return NULL;
    void *field = runtime->api.class_get_field_from_name(klass, field_name);
    return managed_runtime_field_matches(runtime, field, type_code, is_static) ? field : NULL;
}

static void *managed_runtime_symbol(const char *name) {
    if (!name) return NULL;
    void *symbol = dlsym(RTLD_DEFAULT, name);
    if (symbol) return symbol;
    void *handle = dlopen("libil2cpp.so", RTLD_NOW | RTLD_NOLOAD);
    if (!handle) return NULL;
    symbol = dlsym(handle, name);
    dlclose(handle);
    return symbol;
}
#endif
