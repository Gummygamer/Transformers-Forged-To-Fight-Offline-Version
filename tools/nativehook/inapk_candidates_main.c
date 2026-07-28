#include "inapk_server.h"

#include <stdio.h>

int main(int argc, char **argv) {
    char paths[32][4096];
    int count, i;
    if (argc != 3) {
        fprintf(stderr, "usage: %s <maps> <cmdline>\n", argv[0]);
        return 2;
    }
    count = tftf_apk_candidates(argv[1], argv[2], paths, 32);
    for (i = 0; i < count; i++) puts(paths[i]);
    return 0;
}
