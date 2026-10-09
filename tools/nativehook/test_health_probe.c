#define _POSIX_C_SOURCE 200809L
#include "inapk_server.h"

#include <fcntl.h>
#include <stdio.h>
#include <stdlib.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <unistd.h>

int main(int argc, char **argv) {
    struct stat st;
    int fd;
    void *payload;
    if (argc != 2) return 2;
    fd = open(argv[1], O_RDONLY);
    if (fd < 0 || fstat(fd, &st) || st.st_size < 1) return 2;
    payload = mmap(NULL, (size_t)st.st_size, PROT_READ, MAP_PRIVATE, fd, 0);
    close(fd);
    if (payload == MAP_FAILED) return 2;
    if (tftf_server_start_blob(payload, (size_t)st.st_size)) return 1;
    printf("%.4f\n", tftf_quest_fighter_health("nemesisprime_gs_voyager2015"));
    return 0;
}
