#define _POSIX_C_SOURCE 200809L

#include <ctype.h>
#include <errno.h>
#include <fcntl.h>
#include <pthread.h>
#include <stdatomic.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <time.h>
#include <unistd.h>

enum { COPY_CHUNK = 4 * 1024 * 1024 };

typedef struct {
  char *name;
  uint8_t create_version;
  uint8_t create_system;
  uint8_t extract_version;
  uint16_t flag_bits;
  uint16_t compress_type;
  uint16_t dostime;
  uint16_t dosdate;
  uint32_t crc;
  uint32_t compress_size;
  uint32_t file_size;
  const uint8_t *extra;
  uint16_t extra_len;
  const uint8_t *comment;
  uint16_t comment_len;
  uint16_t internal_attr;
  uint32_t external_attr;
  uint64_t source_data;
  int source_fd;
  char *replacement_path;
  uint64_t output_header;
  uint64_t output_data;
} Entry;

typedef struct {
  char *name;
  char *path;
  int used;
} Replacement;

typedef struct {
  int input_fd;
  int output_fd;
  uint64_t input_offset;
  uint64_t output_offset;
  size_t size;
} CopyTask;

typedef struct {
  CopyTask *tasks;
  size_t task_count;
  atomic_size_t next_task;
  atomic_int failed;
} WorkQueue;

static uint16_t read_u16(const uint8_t *p) {
  return (uint16_t)p[0] | (uint16_t)((uint16_t)p[1] << 8);
}

static uint32_t read_u32(const uint8_t *p) {
  return (uint32_t)p[0] | ((uint32_t)p[1] << 8) |
         ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
}

static void write_u16(uint8_t *p, uint16_t value) {
  p[0] = (uint8_t)value;
  p[1] = (uint8_t)(value >> 8);
}

static void write_u32(uint8_t *p, uint32_t value) {
  p[0] = (uint8_t)value;
  p[1] = (uint8_t)(value >> 8);
  p[2] = (uint8_t)(value >> 16);
  p[3] = (uint8_t)(value >> 24);
}

static int write_all_at(int fd, const void *buffer, size_t count, uint64_t offset) {
  const uint8_t *cursor = buffer;
  while (count > 0) {
    ssize_t written = pwrite(fd, cursor, count, (off_t)offset);
    if (written < 0) {
      if (errno == EINTR) continue;
      return -1;
    }
    cursor += written;
    offset += (uint64_t)written;
    count -= (size_t)written;
  }
  return 0;
}

static int copy_range(const CopyTask *task) {
  uint8_t buffer[256 * 1024];
  uint64_t input = task->input_offset;
  uint64_t output = task->output_offset;
  size_t remaining = task->size;
  while (remaining > 0) {
    size_t wanted = remaining < sizeof(buffer) ? remaining : sizeof(buffer);
    ssize_t got = pread(task->input_fd, buffer, wanted, (off_t)input);
    if (got < 0) {
      if (errno == EINTR) continue;
      return -1;
    }
    if (got == 0) {
      errno = EIO;
      return -1;
    }
    if (write_all_at(task->output_fd, buffer, (size_t)got, output) != 0) return -1;
    input += (uint64_t)got;
    output += (uint64_t)got;
    remaining -= (size_t)got;
  }
  return 0;
}

static void *worker_main(void *opaque) {
  WorkQueue *queue = opaque;
  for (;;) {
    size_t index = atomic_fetch_add(&queue->next_task, 1);
    if (index >= queue->task_count) break;
    if (copy_range(&queue->tasks[index]) != 0) atomic_store(&queue->failed, 1);
  }
  return NULL;
}

static uint32_t crc_table[256];

static void initialize_crc(void) {
  for (uint32_t index = 0; index < 256; ++index) {
    uint32_t value = index;
    for (int bit = 0; bit < 8; ++bit)
      value = (value & 1) ? (value >> 1) ^ 0xedb88320u : value >> 1;
    crc_table[index] = value;
  }
}

static int crc_file(const char *path, uint32_t *crc_out, uint32_t *size_out) {
  int fd = open(path, O_RDONLY);
  if (fd < 0) return -1;
  struct stat status;
  if (fstat(fd, &status) != 0 || status.st_size < 0 || status.st_size > UINT32_MAX) {
    close(fd);
    errno = EFBIG;
    return -1;
  }
  uint8_t buffer[256 * 1024];
  uint32_t crc = UINT32_MAX;
  for (;;) {
    ssize_t got = read(fd, buffer, sizeof(buffer));
    if (got < 0) {
      if (errno == EINTR) continue;
      close(fd);
      return -1;
    }
    if (got == 0) break;
    for (ssize_t index = 0; index < got; ++index)
      crc = crc_table[(crc ^ buffer[index]) & 0xffu] ^ (crc >> 8);
  }
  close(fd);
  *crc_out = crc ^ UINT32_MAX;
  *size_out = (uint32_t)status.st_size;
  return 0;
}

static int is_signature(const char *name) {
  if (strncasecmp(name, "META-INF/", 9) != 0) return 0;
  size_t length = strlen(name);
  const char *suffixes[] = { ".SF", ".RSA", ".DSA", ".EC", "MANIFEST.MF" };
  for (size_t index = 0; index < sizeof(suffixes) / sizeof(suffixes[0]); ++index) {
    size_t suffix_length = strlen(suffixes[index]);
    if (length >= suffix_length && strcasecmp(name + length - suffix_length, suffixes[index]) == 0)
      return 1;
  }
  return 0;
}

static int name_in_list(const char *name, char **values, size_t count) {
  for (size_t index = 0; index < count; ++index)
    if (strcmp(name, values[index]) == 0) return 1;
  return 0;
}

static int starts_with_any(const char *name, char **values, size_t count) {
  for (size_t index = 0; index < count; ++index) {
    size_t length = strlen(values[index]);
    if (strncmp(name, values[index], length) == 0) return 1;
  }
  return 0;
}

static Replacement *find_replacement(Replacement *values, size_t count, const char *name) {
  for (size_t index = 0; index < count; ++index)
    if (strcmp(name, values[index].name) == 0) return &values[index];
  return NULL;
}

static int append_entry(Entry **entries, size_t *count, size_t *capacity, Entry value) {
  if (*count == *capacity) {
    size_t next = *capacity ? *capacity * 2 : 128;
    Entry *grown = realloc(*entries, next * sizeof(*grown));
    if (!grown) return -1;
    *entries = grown;
    *capacity = next;
  }
  (*entries)[(*count)++] = value;
  return 0;
}

static int parse_source(const uint8_t *source, size_t source_size,
                        int source_fd, int drop_signatures,
                        char **drops, size_t drop_count,
                        char **drop_prefixes, size_t drop_prefix_count,
                        Replacement *replacements, size_t replacement_count,
                        Entry **entries_out, size_t *entry_count_out) {
  if (source_size < 22) return -1;
  size_t search_start = source_size > 65557 ? source_size - 65557 : 0;
  size_t eocd = source_size - 22;
  while (eocd > search_start && read_u32(source + eocd) != 0x06054b50u) --eocd;
  if (read_u32(source + eocd) != 0x06054b50u) return -1;
  uint16_t source_count = read_u16(source + eocd + 10);
  uint32_t central_offset = read_u32(source + eocd + 16);
  size_t cursor = central_offset;
  Entry *entries = NULL;
  size_t entry_count = 0, entry_capacity = 0;

  for (uint16_t index = 0; index < source_count; ++index) {
    if (cursor + 46 > source_size || read_u32(source + cursor) != 0x02014b50u) goto invalid;
    uint16_t name_len = read_u16(source + cursor + 28);
    uint16_t extra_len = read_u16(source + cursor + 30);
    uint16_t comment_len = read_u16(source + cursor + 32);
    size_t next = cursor + 46u + name_len + extra_len + comment_len;
    if (next > source_size) goto invalid;
    char *name = malloc((size_t)name_len + 1);
    if (!name) goto invalid;
    memcpy(name, source + cursor + 46, name_len);
    name[name_len] = '\0';

    int keep = !(drop_signatures && is_signature(name)) &&
               !name_in_list(name, drops, drop_count) &&
               !starts_with_any(name, drop_prefixes, drop_prefix_count);
    if (keep) {
      uint32_t local = read_u32(source + cursor + 42);
      uint16_t flag_bits = read_u16(source + cursor + 8);
      if (local + 30u > source_size || read_u32(source + local) != 0x04034b50u ||
          (flag_bits & 1u) != 0) {
        free(name);
        goto invalid;
      }
      uint16_t local_name = read_u16(source + local + 26);
      uint16_t local_extra = read_u16(source + local + 28);
      uint64_t data_offset = (uint64_t)local + 30u + local_name + local_extra;
      uint32_t size = read_u32(source + cursor + 20);
      if (data_offset + size > source_size) {
        free(name);
        goto invalid;
      }
      Replacement *replacement = find_replacement(replacements, replacement_count, name);
      Entry entry = {
        .name = name,
        .create_version = source[cursor + 4],
        .create_system = source[cursor + 5],
        .extract_version = source[cursor + 6],
        .flag_bits = flag_bits,
        .compress_type = read_u16(source + cursor + 10),
        .dostime = read_u16(source + cursor + 12),
        .dosdate = read_u16(source + cursor + 14),
        .crc = read_u32(source + cursor + 16),
        .compress_size = size,
        .file_size = read_u32(source + cursor + 24),
        .extra = source + cursor + 46 + name_len,
        .extra_len = extra_len,
        .comment = source + cursor + 46 + name_len + extra_len,
        .comment_len = comment_len,
        .internal_attr = read_u16(source + cursor + 36),
        .external_attr = read_u32(source + cursor + 38),
        .source_data = data_offset,
        .source_fd = source_fd,
        .replacement_path = NULL,
      };
      if (replacement) {
        entry.replacement_path = replacement->path;
        replacement->used = 1;
        if (crc_file(replacement->path, &entry.crc, &entry.compress_size) != 0) {
          free(name);
          goto invalid;
        }
        entry.file_size = entry.compress_size;
        entry.flag_bits = 0;
        entry.compress_type = 0;
      }
      if (append_entry(&entries, &entry_count, &entry_capacity, entry) != 0) {
        free(name);
        goto invalid;
      }
    } else {
      free(name);
    }
    cursor = next;
  }

  for (size_t index = 0; index < replacement_count; ++index) {
    if (replacements[index].used) continue;
    Entry entry = {0};
    entry.name = strdup(replacements[index].name);
    entry.create_version = 20;
    entry.create_system = 3;
    entry.extract_version = 20;
    entry.external_attr = 25165824;
    entry.source_fd = source_fd;
    entry.replacement_path = replacements[index].path;
    const char *epoch = getenv("SOURCE_DATE_EPOCH");
    time_t now = epoch && *epoch ? (time_t)strtoll(epoch, NULL, 10) : time(NULL);
    struct tm utc;
    gmtime_r(&now, &utc);
    entry.dostime = (uint16_t)(utc.tm_hour * 2048 + utc.tm_min * 32 + utc.tm_sec / 2);
    entry.dosdate = (uint16_t)((utc.tm_year + 1900 - 1980) * 512 + (utc.tm_mon + 1) * 32 + utc.tm_mday);
    if (strcmp(entry.name, "assets/tftf_offline_payload.bin") == 0) {
      entry.dostime = 0;
      entry.dosdate = 33;
      entry.external_attr = 27525120;
    }
    if (!entry.name || crc_file(entry.replacement_path, &entry.crc, &entry.compress_size) != 0) {
      free(entry.name);
      goto invalid;
    }
    entry.file_size = entry.compress_size;
    if (append_entry(&entries, &entry_count, &entry_capacity, entry) != 0) {
      free(entry.name);
      goto invalid;
    }
  }
  *entries_out = entries;
  *entry_count_out = entry_count;
  return 0;

invalid:
  for (size_t index = 0; index < entry_count; ++index) free(entries[index].name);
  free(entries);
  return -1;
}

static int write_local_header(int fd, const Entry *entry) {
  size_t name_len = strlen(entry->name);
  size_t header_size = 30 + name_len + entry->extra_len;
  uint8_t *header = calloc(1, header_size);
  if (!header) return -1;
  write_u32(header, 0x04034b50u);
  header[4] = entry->extract_version;
  write_u16(header + 6, entry->flag_bits & (uint16_t)~8u);
  write_u16(header + 8, entry->compress_type);
  write_u16(header + 10, entry->dostime);
  write_u16(header + 12, entry->dosdate);
  write_u32(header + 14, entry->crc);
  write_u32(header + 18, entry->compress_size);
  write_u32(header + 22, entry->file_size);
  write_u16(header + 26, (uint16_t)name_len);
  write_u16(header + 28, entry->extra_len);
  memcpy(header + 30, entry->name, name_len);
  memcpy(header + 30 + name_len, entry->extra, entry->extra_len);
  int result = write_all_at(fd, header, header_size, entry->output_header);
  free(header);
  return result;
}

static int write_central_header(int fd, const Entry *entry, uint64_t offset) {
  size_t name_len = strlen(entry->name);
  size_t header_size = 46 + name_len + entry->extra_len + entry->comment_len;
  uint8_t *header = calloc(1, header_size);
  if (!header) return -1;
  write_u32(header, 0x02014b50u);
  header[4] = entry->create_version;
  header[5] = entry->create_system;
  header[6] = entry->extract_version;
  write_u16(header + 8, entry->flag_bits & (uint16_t)~8u);
  write_u16(header + 10, entry->compress_type);
  write_u16(header + 12, entry->dostime);
  write_u16(header + 14, entry->dosdate);
  write_u32(header + 16, entry->crc);
  write_u32(header + 20, entry->compress_size);
  write_u32(header + 24, entry->file_size);
  write_u16(header + 28, (uint16_t)name_len);
  write_u16(header + 30, entry->extra_len);
  write_u16(header + 32, entry->comment_len);
  write_u16(header + 36, entry->internal_attr);
  write_u32(header + 38, entry->external_attr ? entry->external_attr : 25165824);
  write_u32(header + 42, (uint32_t)entry->output_header);
  memcpy(header + 46, entry->name, name_len);
  memcpy(header + 46 + name_len, entry->extra, entry->extra_len);
  memcpy(header + 46 + name_len + entry->extra_len, entry->comment, entry->comment_len);
  int result = write_all_at(fd, header, header_size, offset);
  free(header);
  return result;
}

static void usage(const char *program) {
  fprintf(stderr, "usage: %s SOURCE DEST [--workers N] [--drop-signatures] "
                  "[--drop NAME] [--drop-prefix PREFIX] [--replace NAME FILE]...\n", program);
}

int main(int argc, char **argv) {
  if (argc < 3) {
    usage(argv[0]);
    return 2;
  }
  char **drops = calloc((size_t)argc, sizeof(*drops));
  char **drop_prefixes = calloc((size_t)argc, sizeof(*drop_prefixes));
  Replacement *replacements = calloc((size_t)argc, sizeof(*replacements));
  if (!drops || !drop_prefixes || !replacements) return 1;
  size_t drop_count = 0, drop_prefix_count = 0, replacement_count = 0;
  int drop_signatures = 0;
  long worker_count = sysconf(_SC_NPROCESSORS_ONLN);
  if (worker_count < 1) worker_count = 1;

  for (int index = 3; index < argc; ++index) {
    if (strcmp(argv[index], "--drop-signatures") == 0) drop_signatures = 1;
    else if (strcmp(argv[index], "--workers") == 0 && index + 1 < argc) worker_count = strtol(argv[++index], NULL, 10);
    else if (strcmp(argv[index], "--drop") == 0 && index + 1 < argc) drops[drop_count++] = argv[++index];
    else if (strcmp(argv[index], "--drop-prefix") == 0 && index + 1 < argc) drop_prefixes[drop_prefix_count++] = argv[++index];
    else if (strcmp(argv[index], "--replace") == 0 && index + 2 < argc) {
      replacements[replacement_count].name = argv[++index];
      replacements[replacement_count].path = argv[++index];
      ++replacement_count;
    } else {
      usage(argv[0]);
      return 2;
    }
  }
  if (worker_count < 1) worker_count = 1;
  if (worker_count > 64) worker_count = 64;
  initialize_crc();

  int source_fd = open(argv[1], O_RDONLY);
  if (source_fd < 0) {
    perror(argv[1]);
    return 1;
  }
  struct stat source_status;
  if (fstat(source_fd, &source_status) != 0 || source_status.st_size < 22) {
    perror("source APK");
    return 1;
  }
  size_t source_size = (size_t)source_status.st_size;
  uint8_t *source = mmap(NULL, source_size, PROT_READ, MAP_PRIVATE, source_fd, 0);
  if (source == MAP_FAILED) {
    perror("mmap source APK");
    return 1;
  }
  Entry *entries = NULL;
  size_t entry_count = 0;
  if (parse_source(source, source_size, source_fd, drop_signatures, drops, drop_count,
                   drop_prefixes, drop_prefix_count, replacements, replacement_count,
                   &entries, &entry_count) != 0 || entry_count > UINT16_MAX) {
    fprintf(stderr, "invalid or unsupported source APK\n");
    return 1;
  }

  uint64_t cursor = 0;
  size_t task_count = 0;
  for (size_t index = 0; index < entry_count; ++index) {
    Entry *entry = &entries[index];
    entry->output_header = cursor;
    entry->output_data = cursor + 30 + strlen(entry->name) + entry->extra_len;
    cursor = entry->output_data + entry->compress_size;
    task_count += (entry->compress_size + COPY_CHUNK - 1) / COPY_CHUNK;
  }
  uint64_t central_offset = cursor;
  for (size_t index = 0; index < entry_count; ++index)
    cursor += 46 + strlen(entries[index].name) + entries[index].extra_len + entries[index].comment_len;
  uint64_t central_size = cursor - central_offset;
  uint64_t output_size = cursor + 22;
  if (output_size > UINT32_MAX) {
    fprintf(stderr, "ZIP64 output is not supported\n");
    return 1;
  }

  int output_fd = open(argv[2], O_CREAT | O_TRUNC | O_RDWR, 0664);
  if (output_fd < 0 || ftruncate(output_fd, (off_t)output_size) != 0) {
    perror(argv[2]);
    return 1;
  }
  CopyTask *tasks = calloc(task_count ? task_count : 1, sizeof(*tasks));
  int *replacement_fds = calloc(entry_count ? entry_count : 1, sizeof(*replacement_fds));
  if (!tasks || !replacement_fds) return 1;
  for (size_t index = 0; index < entry_count; ++index) replacement_fds[index] = -1;
  size_t task_index = 0;
  for (size_t index = 0; index < entry_count; ++index) {
    Entry *entry = &entries[index];
    if (write_local_header(output_fd, entry) != 0) {
      perror("write local ZIP header");
      return 1;
    }
    int input_fd = source_fd;
    uint64_t input_offset = entry->source_data;
    if (entry->replacement_path) {
      input_fd = open(entry->replacement_path, O_RDONLY);
      if (input_fd < 0) {
        perror(entry->replacement_path);
        return 1;
      }
      replacement_fds[index] = input_fd;
      input_offset = 0;
    }
    for (uint64_t at = 0; at < entry->compress_size; at += COPY_CHUNK) {
      size_t size = entry->compress_size - at < COPY_CHUNK ? (size_t)(entry->compress_size - at) : COPY_CHUNK;
      tasks[task_index++] = (CopyTask) { input_fd, output_fd, input_offset + at, entry->output_data + at, size };
    }
  }

  WorkQueue queue = { .tasks = tasks, .task_count = task_count };
  atomic_init(&queue.next_task, 0);
  atomic_init(&queue.failed, 0);
  pthread_t *workers = calloc((size_t)worker_count, sizeof(*workers));
  if (!workers) return 1;
  for (long index = 0; index < worker_count; ++index)
    if (pthread_create(&workers[index], NULL, worker_main, &queue) != 0) return 1;
  for (long index = 0; index < worker_count; ++index) pthread_join(workers[index], NULL);
  if (atomic_load(&queue.failed)) {
    perror("parallel APK data copy");
    return 1;
  }

  cursor = central_offset;
  for (size_t index = 0; index < entry_count; ++index) {
    if (write_central_header(output_fd, &entries[index], cursor) != 0) {
      perror("write central ZIP header");
      return 1;
    }
    cursor += 46 + strlen(entries[index].name) + entries[index].extra_len + entries[index].comment_len;
  }
  uint8_t eocd[22] = {0};
  write_u32(eocd, 0x06054b50u);
  write_u16(eocd + 8, (uint16_t)entry_count);
  write_u16(eocd + 10, (uint16_t)entry_count);
  write_u32(eocd + 12, (uint32_t)central_size);
  write_u32(eocd + 16, (uint32_t)central_offset);
  if (write_all_at(output_fd, eocd, sizeof(eocd), cursor) != 0 || fsync(output_fd) != 0) {
    perror("finish APK");
    return 1;
  }
  printf("parallel ZIP writer: %ld workers, %zu entries, %zu data chunks\n",
         worker_count, entry_count, task_count);

  for (size_t index = 0; index < entry_count; ++index) {
    if (replacement_fds[index] >= 0) close(replacement_fds[index]);
    free(entries[index].name);
  }
  free(replacement_fds);
  free(workers);
  free(tasks);
  free(entries);
  munmap(source, source_size);
  close(output_fd);
  close(source_fd);
  free(replacements);
  free(drop_prefixes);
  free(drops);
  return 0;
}
