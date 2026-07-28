#ifndef TFTF_INAPK_SERVER_H
#define TFTF_INAPK_SERVER_H

#include <stddef.h>

typedef void (*tftf_log_fn)(const char *fmt, ...);

void tftf_server_set_logger(tftf_log_fn fn);
int tftf_server_start_blob(const void *blob, size_t len);
/* Fills out with up to max mapped APK paths, ordered most likely first. */
int tftf_apk_candidates(const char *maps_path, const char *cmdline_path,
                        char out[][4096], int max);
int tftf_server_start_from_apk(void);

#endif
