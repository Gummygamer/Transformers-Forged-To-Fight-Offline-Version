#define _POSIX_C_SOURCE 200809L
#include "inapk_server.h"

#include <fcntl.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <unistd.h>

static void verbose(const char *fmt, ...) {
    va_list ap;
    va_start(ap, fmt);
    vfprintf(stderr, fmt, ap);
    fputc('\n', stderr);
    va_end(ap);
}
int main(int argc, char **argv) {
    int arg = 1, fd, rc;
    struct stat st;
    void *p;
    if (argc > 1 && !strcmp(argv[1], "--verbose")) { tftf_server_set_logger(verbose); arg++; }
    if (argc != arg + 1) { fprintf(stderr, "usage: %s [--verbose] <payload.bin>\n", argv[0]); return 2; }
    fd = open(argv[arg], O_RDONLY);
    if (fd < 0 || fstat(fd, &st) || st.st_size < 1) return 2;
    p = mmap(NULL, (size_t)st.st_size, PROT_READ, MAP_PRIVATE, fd, 0);
    close(fd);
    if (p == MAP_FAILED) return 2;
    rc = tftf_server_start_blob(p, (size_t)st.st_size);
    if (rc) return 1;
    /* The port is in the fixed header. */
    printf("listening %u\n", (unsigned)((unsigned char *)p)[16] | ((unsigned)((unsigned char *)p)[17] << 8));
    fflush(stdout);
    for (;;) pause();
}
