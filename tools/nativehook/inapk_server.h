#ifndef TFTF_INAPK_SERVER_H
#define TFTF_INAPK_SERVER_H

#include <stddef.h>

typedef void (*tftf_log_fn)(const char *fmt, ...);

void tftf_server_set_logger(tftf_log_fn fn);
int tftf_server_start_blob(const void *blob, size_t len);
int tftf_server_start_from_apk(void);

#endif
