#define _GNU_SOURCE

#include <ctype.h>
#include <errno.h>
#include <fcntl.h>
#include <ftw.h>
#include <glob.h>
#include <limits.h>
#include <stdarg.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mount.h>
#include <sys/reboot.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <unistd.h>

#define HOMEHARBOR_GUID "8be4df61-93ca-4d2c-bb7c-0f9d9aee5f3a"
#define RUN_DIR "/run/homeharbor"
#define ROOT_PATH RUN_DIR "/root"
#define STATE_PATH RUN_DIR "/init-state.env"

#ifndef HOMEHARBOR_ENABLE_INITRAMFS_EMERGENCY_SHELL
#define HOMEHARBOR_ENABLE_INITRAMFS_EMERGENCY_SHELL 0
#endif

typedef struct {
    char *hash_algorithm;
    char *data_block_size;
    char *hash_block_size;
    char *data_blocks;
    char *tree_offset;
    char *salt;
    char *root_digest;
    char *vbmeta_digest;
} VerityDescriptor;

typedef struct {
    char *suffix;
    char *sha256;
} AddonSha;

typedef struct {
    char *luks_uuid;
    char *mapper;
} DataDevice;

typedef struct {
    char *super;
    char *boot_state;
    char *boot_mode;
    char *boot_slot;
    char *boot_config;
    char *root_logical;
    char *modules_logical;
    char *firmware_logical;
    char *vbmeta_partition;
    char *vbmeta_digest;
    char *vbmeta_a_digest;
    char *vbmeta_b_digest;
    char *modules_a_verity;
    char *modules_b_verity;
    char *firmware_a_verity;
    char *firmware_b_verity;
    char *slot;
    char *kernel_release;
    char *version;
    char *addons;

    char *root_digest;
    char *modules_digest;
    char *firmware_digest;
    char *recovery_digest;
    char *recovery_slot;

    AddonSha *addon_shas;
    size_t addon_sha_count;
    char **addon_keys;
    size_t addon_key_count;

    int external_mounts;
    int data_mount_requested;
    char *data_unlock_mode;
    char *data_filesystem;
    char *data_raid_backend;
    char *data_mdadm_name;
    char *data_mdadm_uuid;
    int data_device_count;
    char *data_first_mapper;
    DataDevice *data_devices;
    size_t data_device_capacity;
} HomeHarborState;

static HomeHarborState state;

static char *xstrdup(const char *value) {
    char *copy = strdup(value ? value : "");
    if (!copy) {
        perror("strdup");
        exit(1);
    }
    return copy;
}

static void set_string(char **target, const char *value) {
    free(*target);
    *target = xstrdup(value ? value : "");
}

static const char *nonempty_or(const char *value, const char *fallback) {
    return value && value[0] ? value : fallback;
}

static bool is_empty(const char *value) {
    return !value || value[0] == '\0';
}

static char *lower_copy(const char *value) {
    char *copy = xstrdup(value);
    for (char *p = copy; *p; p++) {
        *p = (char)tolower((unsigned char)*p);
    }
    return copy;
}

static char *upper_addon_suffix(const char *addon) {
    char *suffix = xstrdup(addon);
    for (char *p = suffix; *p; p++) {
        if (*p == '.' || *p == '-') {
            *p = '_';
        } else {
            *p = (char)toupper((unsigned char)*p);
        }
    }
    return suffix;
}

static char *addon_suffix(const char *addon) {
    char *suffix = xstrdup(addon);
    for (char *p = suffix; *p; p++) {
        if (*p == '.' || *p == '-') {
            *p = '_';
        }
    }
    return suffix;
}

static bool chars_match(const char *value, int (*predicate)(int)) {
    if (is_empty(value)) {
        return false;
    }
    for (const unsigned char *p = (const unsigned char *)value; *p; p++) {
        if (!predicate(*p)) {
            return false;
        }
    }
    return true;
}

static int is_logical_char(int c) {
    return isalnum(c) || c == '.' || c == '_' || c == '-';
}

static int is_kernel_release_char(int c) {
    return isalnum(c) || c == '.' || c == '_' || c == '+' || c == '-';
}

static int is_addon_char(int c) {
    return islower(c) || isdigit(c) || c == '.' || c == '_' || c == '-';
}

static int is_hex_char(int c) {
    return isxdigit(c);
}

static int is_upper_addon_suffix_char(int c) {
    return isupper(c) || isdigit(c) || c == '_';
}

static int is_uuid_char(int c) {
    return isxdigit(c) || c == '-';
}

static int is_mdadm_uuid_char(int c) {
    return isxdigit(c) || c == ':' || c == '-';
}

static int is_digit_char(int c) {
    return isdigit(c);
}

#if HOMEHARBOR_ENABLE_INITRAMFS_EMERGENCY_SHELL
static void launch_interactive_shell(void) {
    const char *shell = access("/bin/ash", X_OK) == 0 ? "/bin/ash" : "/bin/sh";
    pid_t pid = fork();
    int status = 0;

    if (pid == 0) {
        execl(shell, shell, (char *)NULL);
        _exit(127);
    }
    if (pid > 0) {
        (void)waitpid(pid, &status, 0);
    }
}
#else
static void halt_on_failure(void) __attribute__((noreturn));
static void halt_on_failure(void) {
    fputs("HomeHarbor emergency shell is disabled for this build; halting.\n", stderr);
    sync();
    (void)reboot(RB_POWER_OFF);
    (void)reboot(RB_HALT_SYSTEM);
    for (;;) {
        pause();
    }
}
#endif

static void fail(const char *format, ...) __attribute__((noreturn));
static void fail(const char *format, ...) {
    va_list args;
    va_start(args, format);
    vfprintf(stderr, format, args);
    va_end(args);
    fputc('\n', stderr);
#if HOMEHARBOR_ENABLE_INITRAMFS_EMERGENCY_SHELL
    fputs("HomeHarbor emergency shell is enabled for this debug build.\n", stderr);
    launch_interactive_shell();
    exit(1);
#else
    halt_on_failure();
#endif
}

static void mkdir_p(const char *path, mode_t mode) {
    char *copy = xstrdup(path);
    size_t len = strlen(copy);

    if (len == 0) {
        free(copy);
        return;
    }
    if (copy[len - 1] == '/') {
        copy[len - 1] = '\0';
    }
    for (char *p = copy + 1; *p; p++) {
        if (*p == '/') {
            *p = '\0';
            if (mkdir(copy, mode) != 0 && errno != EEXIST) {
                fail("HomeHarbor could not create directory: %s", copy);
            }
            *p = '/';
        }
    }
    if (mkdir(copy, mode) != 0 && errno != EEXIST) {
        fail("HomeHarbor could not create directory: %s", copy);
    }
    free(copy);
}

static bool is_block_device(const char *path) {
    struct stat st;
    return path && stat(path, &st) == 0 && S_ISBLK(st.st_mode);
}

static bool file_exists(const char *path) {
    struct stat st;
    return path && stat(path, &st) == 0;
}

static int unlink_tree_cb(const char *path, const struct stat *st, int type, struct FTW *ftwbuf) {
    (void)st;
    (void)type;
    (void)ftwbuf;
    return remove(path);
}

static void remove_tree(const char *path) {
    if (!file_exists(path)) {
        return;
    }
    (void)nftw(path, unlink_tree_cb, 16, FTW_DEPTH | FTW_PHYS);
}

static int run_command(const char *const argv[], const unsigned char *input, size_t input_len, char **stdout_capture) {
    int stdin_pipe[2] = {-1, -1};
    int stdout_pipe[2] = {-1, -1};
    pid_t pid;
    int status = 127;

    if (input && pipe(stdin_pipe) != 0) {
        return 127;
    }
    if (stdout_capture && pipe(stdout_pipe) != 0) {
        if (stdin_pipe[0] >= 0) {
            close(stdin_pipe[0]);
            close(stdin_pipe[1]);
        }
        return 127;
    }

    pid = fork();
    if (pid == 0) {
        if (input) {
            close(stdin_pipe[1]);
            dup2(stdin_pipe[0], STDIN_FILENO);
            close(stdin_pipe[0]);
        }
        if (stdout_capture) {
            close(stdout_pipe[0]);
            dup2(stdout_pipe[1], STDOUT_FILENO);
            close(stdout_pipe[1]);
        }
        execvp(argv[0], (char *const *)argv);
        _exit(127);
    }
    if (pid < 0) {
        if (input) {
            close(stdin_pipe[0]);
            close(stdin_pipe[1]);
        }
        if (stdout_capture) {
            close(stdout_pipe[0]);
            close(stdout_pipe[1]);
        }
        return 127;
    }

    if (input) {
        close(stdin_pipe[0]);
        size_t written = 0;
        while (written < input_len) {
            ssize_t chunk = write(stdin_pipe[1], input + written, input_len - written);
            if (chunk <= 0) {
                break;
            }
            written += (size_t)chunk;
        }
        close(stdin_pipe[1]);
    }

    if (stdout_capture) {
        close(stdout_pipe[1]);
        size_t cap = 4096;
        size_t len = 0;
        char *buffer = malloc(cap);
        if (!buffer) {
            close(stdout_pipe[0]);
            return 127;
        }
        for (;;) {
            if (len + 2048 + 1 > cap) {
                cap *= 2;
                char *next = realloc(buffer, cap);
                if (!next) {
                    free(buffer);
                    close(stdout_pipe[0]);
                    return 127;
                }
                buffer = next;
            }
            ssize_t read_bytes = read(stdout_pipe[0], buffer + len, cap - len - 1);
            if (read_bytes <= 0) {
                break;
            }
            len += (size_t)read_bytes;
        }
        close(stdout_pipe[0]);
        buffer[len] = '\0';
        *stdout_capture = buffer;
    }

    if (pid > 0 && waitpid(pid, &status, 0) >= 0 && WIFEXITED(status)) {
        return WEXITSTATUS(status);
    }
    return 127;
}

static int run_status(const char *const argv[]) {
    return run_command(argv, NULL, 0, NULL);
}

static char *run_capture_required(const char *const argv[], const char *message) {
    char *output = NULL;
    int exit_code = run_command(argv, NULL, 0, &output);
    if (exit_code != 0) {
        free(output);
        fail("%s", message);
    }
    return output;
}

static char *read_text_file(const char *path) {
    int fd = open(path, O_RDONLY | O_CLOEXEC);
    size_t cap = 4096;
    size_t len = 0;
    char *buffer;

    if (fd < 0) {
        return NULL;
    }
    buffer = malloc(cap);
    if (!buffer) {
        close(fd);
        return NULL;
    }
    for (;;) {
        if (len + 2048 + 1 > cap) {
            cap *= 2;
            char *next = realloc(buffer, cap);
            if (!next) {
                free(buffer);
                close(fd);
                return NULL;
            }
            buffer = next;
        }
        ssize_t read_bytes = read(fd, buffer + len, cap - len - 1);
        if (read_bytes <= 0) {
            break;
        }
        len += (size_t)read_bytes;
    }
    close(fd);
    buffer[len] = '\0';
    return buffer;
}

static unsigned char *read_bytes_skip(const char *path, size_t skip, size_t max_len, size_t *out_len) {
    int fd = open(path, O_RDONLY | O_CLOEXEC);
    size_t cap = max_len ? max_len : 256;
    size_t len = 0;
    unsigned char discard[128];
    unsigned char *buffer;

    *out_len = 0;
    if (fd < 0) {
        return NULL;
    }
    while (skip > 0) {
        size_t want = skip < sizeof(discard) ? skip : sizeof(discard);
        ssize_t read_bytes = read(fd, discard, want);
        if (read_bytes <= 0) {
            close(fd);
            return NULL;
        }
        skip -= (size_t)read_bytes;
    }
    buffer = malloc(cap + 1);
    if (!buffer) {
        close(fd);
        return NULL;
    }
    for (;;) {
        if (max_len && len >= max_len) {
            break;
        }
        if (!max_len && len + 128 + 1 > cap) {
            cap *= 2;
            unsigned char *next = realloc(buffer, cap + 1);
            if (!next) {
                free(buffer);
                close(fd);
                return NULL;
            }
            buffer = next;
        }
        size_t want = max_len ? max_len - len : cap - len;
        ssize_t read_bytes = read(fd, buffer + len, want);
        if (read_bytes <= 0) {
            break;
        }
        len += (size_t)read_bytes;
    }
    close(fd);
    buffer[len] = '\0';
    *out_len = len;
    return buffer;
}

static void write_file_bytes(const char *path, const void *data, size_t len, mode_t mode) {
    char *parent = xstrdup(path);
    char *slash = strrchr(parent, '/');
    if (slash && slash != parent) {
        *slash = '\0';
        mkdir_p(parent, 0755);
    } else if (slash == parent) {
        slash[1] = '\0';
        mkdir_p(parent, 0755);
    }
    free(parent);
    int fd = open(path, O_WRONLY | O_CREAT | O_TRUNC | O_CLOEXEC, mode);
    if (fd < 0) {
        fail("HomeHarbor could not write %s", path);
    }
    const unsigned char *bytes = data;
    size_t written = 0;
    while (written < len) {
        ssize_t chunk = write(fd, bytes + written, len - written);
        if (chunk <= 0) {
            close(fd);
            fail("HomeHarbor could not write %s", path);
        }
        written += (size_t)chunk;
    }
    close(fd);
    chmod(path, mode);
}

static void write_text(const char *path, const char *text, mode_t mode) {
    write_file_bytes(path, text, strlen(text), mode);
}

static char *cmdline_arg(const char *name, const char *fallback) {
    char *cmdline = read_text_file("/proc/cmdline");
    char *save = NULL;
    char *token;
    size_t name_len = strlen(name);

    if (!cmdline) {
        return xstrdup(fallback);
    }
    for (token = strtok_r(cmdline, " \t\r\n", &save); token; token = strtok_r(NULL, " \t\r\n", &save)) {
        if (strncmp(token, name, name_len) == 0 && token[name_len] == '=') {
            char *value = xstrdup(token + name_len + 1);
            free(cmdline);
            return value;
        }
        if (strcmp(token, name) == 0) {
            free(cmdline);
            return xstrdup("1");
        }
    }
    free(cmdline);
    return xstrdup(fallback);
}

static char *resolve_device(const char *path, int timeout_seconds) {
    for (int i = 0; i <= timeout_seconds; i++) {
        if (is_block_device(path)) {
            char real[PATH_MAX];
            if (realpath(path, real)) {
                return xstrdup(real);
            }
            return xstrdup(path);
        }
        if (i < timeout_seconds) {
            sleep(1);
        }
    }
    return xstrdup(path);
}

static char *boot_env_path(const char *config, const char *mount_root) {
    if (strncmp(config, "/var/", 5) == 0) {
        char *path = NULL;
        if (asprintf(&path, "%s/%s", mount_root, config + 5) < 0) {
            return NULL;
        }
        return path;
    }
    if (config[0] == '/') {
        char *path = NULL;
        if (asprintf(&path, "%s/%s", mount_root, config + 1) < 0) {
            return NULL;
        }
        return path;
    }
    return NULL;
}

static void validate_logical(const char *label, const char *value) {
    if (!chars_match(value, is_logical_char)) {
        fail("HomeHarbor %s logical partition is invalid", label);
    }
}

static void validate_sha256(const char *label, const char *value) {
    if (!chars_match(value, is_hex_char) || strlen(value) != 64) {
        fail("HomeHarbor %s SHA-256 digest is invalid", label);
    }
}

static void set_addon_sha_by_suffix(const char *suffix, const char *sha256) {
    for (size_t i = 0; i < state.addon_sha_count; i++) {
        if (strcmp(state.addon_shas[i].suffix, suffix) == 0) {
            set_string(&state.addon_shas[i].sha256, sha256);
            return;
        }
    }
    AddonSha *next = reallocarray(state.addon_shas, state.addon_sha_count + 1, sizeof(AddonSha));
    if (!next) {
        fail("HomeHarbor kernel addon state allocation failed");
    }
    state.addon_shas = next;
    state.addon_shas[state.addon_sha_count].suffix = xstrdup(suffix);
    state.addon_shas[state.addon_sha_count].sha256 = xstrdup(sha256);
    state.addon_sha_count++;
}

static const char *get_addon_sha(const char *addon) {
    char *suffix = addon_suffix(addon);
    const char *value = "";
    for (size_t i = 0; i < state.addon_sha_count; i++) {
        if (strcmp(state.addon_shas[i].suffix, suffix) == 0) {
            value = state.addon_shas[i].sha256;
            break;
        }
    }
    free(suffix);
    return value;
}

static void clear_addon_keys(void) {
    for (size_t i = 0; i < state.addon_key_count; i++) {
        free(state.addon_keys[i]);
    }
    free(state.addon_keys);
    state.addon_keys = NULL;
    state.addon_key_count = 0;
}

static void parse_addon_keys(void) {
    clear_addon_keys();
    if (is_empty(state.addons)) {
        return;
    }
    char *copy = xstrdup(state.addons);
    char *save = NULL;
    for (char *part = strtok_r(copy, ",", &save); part; part = strtok_r(NULL, ",", &save)) {
        if (!chars_match(part, is_addon_char)) {
            free(copy);
            fail("HomeHarbor kernel addon key is invalid");
        }
        for (size_t i = 0; i < state.addon_key_count; i++) {
            if (strcmp(state.addon_keys[i], part) == 0) {
                free(copy);
                fail("HomeHarbor kernel addon list contains a duplicate key");
            }
        }
        char **next = reallocarray(state.addon_keys, state.addon_key_count + 1, sizeof(char *));
        if (!next) {
            free(copy);
            fail("HomeHarbor kernel addon state allocation failed");
        }
        state.addon_keys = next;
        state.addon_keys[state.addon_key_count++] = xstrdup(part);
    }
    free(copy);
}

static void validate_addons(void) {
    parse_addon_keys();
    for (size_t i = 0; i < state.addon_key_count; i++) {
        char label[256];
        snprintf(label, sizeof(label), "kernel addon %s", state.addon_keys[i]);
        validate_sha256(label, get_addon_sha(state.addon_keys[i]));
    }
}

static void set_data_device_value(int index, const char *key, const char *value) {
    if (index < 0) {
        fail("HomeHarbor data device index is invalid");
    }
    if ((size_t)(index + 1) > state.data_device_capacity) {
        size_t next_capacity = state.data_device_capacity ? state.data_device_capacity : 4;
        while (next_capacity < (size_t)(index + 1)) {
            next_capacity *= 2;
        }
        DataDevice *next = reallocarray(state.data_devices, next_capacity, sizeof(DataDevice));
        if (!next) {
            fail("HomeHarbor data device state allocation failed");
        }
        for (size_t i = state.data_device_capacity; i < next_capacity; i++) {
            next[i].luks_uuid = NULL;
            next[i].mapper = NULL;
        }
        state.data_devices = next;
        state.data_device_capacity = next_capacity;
    }
    if (strcmp(key, "uuid") == 0) {
        set_string(&state.data_devices[index].luks_uuid, value);
    } else {
        set_string(&state.data_devices[index].mapper, value);
        if (is_empty(state.data_first_mapper)) {
            set_string(&state.data_first_mapper, value);
        }
    }
}

static void parse_key_value_file(const char *path, void (*callback)(const char *, const char *)) {
    FILE *file = fopen(path, "r");
    char *line = NULL;
    size_t cap = 0;

    if (!file) {
        fail("HomeHarbor could not read %s", path);
    }
    while (getline(&line, &cap, file) >= 0) {
        size_t len = strlen(line);
        while (len > 0 && (line[len - 1] == '\n' || line[len - 1] == '\r')) {
            line[--len] = '\0';
        }
        if (len == 0 || line[0] == '#') {
            continue;
        }
        char *equals = strchr(line, '=');
        if (!equals) {
            free(line);
            fclose(file);
            fail("HomeHarbor env file has an invalid line");
        }
        *equals = '\0';
        callback(line, equals + 1);
    }
    free(line);
    fclose(file);
}

static void read_boot_env_line(const char *key, const char *value) {
    if (strcmp(key, "HOMEHARBOR_BOOT_SLOT") == 0) {
        set_string(&state.boot_slot, value);
    } else if (strcmp(key, "HOMEHARBOR_BOOT_MODE") == 0) {
        set_string(&state.boot_mode, value);
    } else if (strcmp(key, "HOMEHARBOR_SLOT") == 0) {
        set_string(&state.slot, value);
    } else if (strcmp(key, "HOMEHARBOR_ROOT_LOGICAL") == 0) {
        set_string(&state.root_logical, value);
    } else if (strcmp(key, "HOMEHARBOR_KERNEL_RELEASE") == 0) {
        set_string(&state.kernel_release, value);
    } else if (strcmp(key, "HOMEHARBOR_MODULES_LOGICAL") == 0) {
        set_string(&state.modules_logical, value);
    } else if (strcmp(key, "HOMEHARBOR_FIRMWARE_LOGICAL") == 0) {
        set_string(&state.firmware_logical, value);
    } else if (strcmp(key, "HOMEHARBOR_VBMETA_PARTITION") == 0) {
        set_string(&state.vbmeta_partition, value);
    } else if (strcmp(key, "HOMEHARBOR_VBMETA_DIGEST") == 0) {
        set_string(&state.vbmeta_digest, value);
    } else if (strcmp(key, "HOMEHARBOR_ROOT_DESCRIPTOR_DIGEST") == 0) {
        set_string(&state.root_digest, value);
    } else if (strcmp(key, "HOMEHARBOR_MODULES_DESCRIPTOR_DIGEST") == 0) {
        set_string(&state.modules_digest, value);
    } else if (strcmp(key, "HOMEHARBOR_FIRMWARE_DESCRIPTOR_DIGEST") == 0) {
        set_string(&state.firmware_digest, value);
    } else if (strcmp(key, "HOMEHARBOR_VERSION") == 0) {
        set_string(&state.version, value);
    } else if (strcmp(key, "HOMEHARBOR_ADDONS") == 0) {
        set_string(&state.addons, value);
    } else if (strncmp(key, "HOMEHARBOR_ADDON_", 17) == 0 && strlen(key) > 24 && strcmp(key + strlen(key) - 7, "_SHA256") == 0) {
        size_t suffix_len = strlen(key) - 17 - 7;
        char *suffix = strndup(key + 17, suffix_len);
        if (!suffix || !chars_match(suffix, is_upper_addon_suffix_char)) {
            free(suffix);
            fail("HomeHarbor kernel addon boot env key is invalid");
        }
        validate_sha256("kernel addon", value);
        for (char *p = suffix; *p; p++) {
            *p = (char)tolower((unsigned char)*p);
        }
        set_addon_sha_by_suffix(suffix, value);
        free(suffix);
    }
}

static void read_boot_env(const char *path) {
    parse_key_value_file(path, read_boot_env_line);
}

static void read_data_env_line(const char *key, const char *value) {
    if (strcmp(key, "HOMEHARBOR_DATA_UNLOCK_MODE") == 0) {
        if (strcmp(value, "passphrase") != 0 && strcmp(value, "tpm2") != 0) {
            fail("HomeHarbor data unlock mode is invalid");
        }
        set_string(&state.data_unlock_mode, value);
    } else if (strcmp(key, "HOMEHARBOR_DATA_FILESYSTEM") == 0) {
        if (strcmp(value, "btrfs") != 0 && strcmp(value, "xfs") != 0 && strcmp(value, "zfs") != 0) {
            fail("HomeHarbor data file system is invalid");
        }
        set_string(&state.data_filesystem, value);
    } else if (strcmp(key, "HOMEHARBOR_DATA_RAID_BACKEND") == 0) {
        if (strcmp(value, "filesystem") != 0 && strcmp(value, "mdadm") != 0) {
            fail("HomeHarbor data RAID backend is invalid");
        }
        set_string(&state.data_raid_backend, value);
    } else if (strcmp(key, "HOMEHARBOR_DATA_MDADM_NAME") == 0) {
        if (!chars_match(value, is_logical_char)) {
            fail("HomeHarbor data mdadm name is invalid");
        }
        set_string(&state.data_mdadm_name, value);
    } else if (strcmp(key, "HOMEHARBOR_DATA_MDADM_UUID") == 0) {
        if (!chars_match(value, is_mdadm_uuid_char)) {
            fail("HomeHarbor data mdadm UUID is invalid");
        }
        set_string(&state.data_mdadm_uuid, value);
    } else if (strcmp(key, "HOMEHARBOR_DATA_DEVICE_COUNT") == 0) {
        if (!chars_match(value, is_digit_char)) {
            fail("HomeHarbor data device count is invalid");
        }
        state.data_device_count = atoi(value);
    } else if (strncmp(key, "HOMEHARBOR_DATA_LUKS_UUID_", 26) == 0) {
        const char *index_text = key + 26;
        if (!chars_match(index_text, is_digit_char) || !chars_match(value, is_uuid_char)) {
            fail("HomeHarbor data LUKS UUID is invalid");
        }
        set_data_device_value(atoi(index_text), "uuid", value);
    } else if (strncmp(key, "HOMEHARBOR_DATA_MAPPER_", 23) == 0) {
        const char *index_text = key + 23;
        if (!chars_match(index_text, is_digit_char) || !chars_match(value, is_logical_char)) {
            fail("HomeHarbor data mapper name is invalid");
        }
        set_data_device_value(atoi(index_text), "mapper", value);
    }
}

static void reset_data_defaults(void) {
    set_string(&state.data_unlock_mode, "");
    set_string(&state.data_filesystem, "btrfs");
    set_string(&state.data_raid_backend, "filesystem");
    set_string(&state.data_mdadm_name, "homeharbor-data");
    set_string(&state.data_mdadm_uuid, "");
    set_string(&state.data_first_mapper, "");
    state.data_device_count = 0;
}

static void read_data_unlock_env(const char *path) {
    reset_data_defaults();
    parse_key_value_file(path, read_data_env_line);
}

static char payload_slot(const char *payload, int index) {
    char *copy = xstrdup(payload);
    char *save = NULL;
    char *part;
    int position = 0;
    char result = '\0';

    for (part = strtok_r(copy, ":", &save); part; part = strtok_r(NULL, ":", &save)) {
        if (position == index) {
            if (strcmp(part, "A") == 0 || strcmp(part, "a") == 0) {
                result = 'A';
            } else if (strcmp(part, "B") == 0 || strcmp(part, "b") == 0) {
                result = 'B';
            }
            break;
        }
        position++;
    }
    free(copy);
    return result;
}

static char *efi_var_path(const char *name) {
    char *path = NULL;
    if (asprintf(&path, "/sys/firmware/efi/efivars/%s-" HOMEHARBOR_GUID, name) < 0) {
        return NULL;
    }
    return path;
}

static void ensure_efivarfs(void) {
    if (!file_exists("/sys/firmware/efi/efivars")) {
        mkdir_p("/sys/firmware/efi/efivars", 0755);
        const char *const argv[] = {"mount", "-t", "efivarfs", "efivarfs", "/sys/firmware/efi/efivars", NULL};
        (void)run_status(argv);
    }
}

static char *read_boot_current(void) {
    ensure_efivarfs();
    char *path = efi_var_path("HomeHarborBootCurrent");
    size_t len = 0;
    unsigned char *bytes = read_bytes_skip(path, 4, 64, &len);
    free(path);
    if (!bytes) {
        return NULL;
    }
    size_t out = 0;
    for (size_t i = 0; i < len; i++) {
        if (bytes[i] != '\0') {
            bytes[out++] = bytes[i];
        }
    }
    bytes[out] = '\0';
    return (char *)bytes;
}

static void apply_boot_current(const char *expected_mode) {
    char *payload = read_boot_current();
    if (!payload) {
        fail("HomeHarbor current boot EFI variable is missing or invalid");
    }

    if (strcmp(expected_mode, "normal") == 0 && strncmp(payload, "normal:", 7) == 0) {
        char boot = payload_slot(payload, 1);
        char root = payload_slot(payload, 2);
        if (!boot) {
            free(payload);
            fail("HomeHarbor current boot slot is invalid");
        }
        if (!root) {
            free(payload);
            fail("HomeHarbor current root slot is invalid");
        }
        char boot_lower = (char)tolower((unsigned char)boot);
        char root_lower = (char)tolower((unsigned char)root);
        char text[128];
        snprintf(text, sizeof(text), "%c", boot);
        set_string(&state.boot_slot, text);
        snprintf(text, sizeof(text), "%c", root);
        set_string(&state.slot, text);
        snprintf(text, sizeof(text), "/var/lib/homeharbor/ota/boot_%c.env", boot_lower);
        set_string(&state.boot_config, text);
        snprintf(text, sizeof(text), "root_%c", root_lower);
        set_string(&state.root_logical, text);
        snprintf(text, sizeof(text), "modules_%c", boot_lower);
        set_string(&state.modules_logical, text);
        snprintf(text, sizeof(text), "firmware_%c", boot_lower);
        set_string(&state.firmware_logical, text);
        snprintf(text, sizeof(text), "vbmeta_%c", root_lower);
        set_string(&state.vbmeta_partition, text);
    } else if (strcmp(expected_mode, "recovery") == 0 && strncmp(payload, "recovery:", 9) == 0) {
        char recovery = payload_slot(payload, 1);
        if (!recovery) {
            free(payload);
            fail("HomeHarbor current recovery slot is invalid");
        }
        char text[2] = {recovery, '\0'};
        set_string(&state.recovery_slot, text);
    } else {
        free(payload);
        fail("HomeHarbor current boot EFI variable is missing or invalid");
    }

    free(payload);
}

static void validate_boot_env(void) {
    if (!(strcmp(nonempty_or(state.boot_mode, ""), "") == 0 ||
          strcmp(state.boot_mode, "legacy") == 0 ||
          strcmp(state.boot_mode, "raw-uki") == 0 ||
          strcmp(state.boot_mode, "secure-boot-raw-uki") == 0)) {
        fail("HomeHarbor boot mode is invalid");
    }
    if (!(is_empty(state.boot_slot) || strcmp(state.boot_slot, "A") == 0 || strcmp(state.boot_slot, "B") == 0)) {
        fail("HomeHarbor boot slot is invalid");
    }
    if (!(is_empty(state.slot) || strcmp(state.slot, "A") == 0 || strcmp(state.slot, "B") == 0)) {
        fail("HomeHarbor root slot is invalid");
    }
    if (!chars_match(state.kernel_release, is_kernel_release_char)) {
        fail("HomeHarbor kernel release is invalid");
    }
    validate_logical("root", state.root_logical);
    validate_logical("modules", state.modules_logical);
    validate_logical("firmware", state.firmware_logical);
    validate_logical("vbmeta", state.vbmeta_partition);
    validate_addons();
}

static void read_cmdline_addons(void) {
    parse_addon_keys();
    for (size_t i = 0; i < state.addon_key_count; i++) {
        char *suffix = addon_suffix(state.addon_keys[i]);
        char *arg = NULL;
        if (asprintf(&arg, "homeharbor.addon_%s_sha256", suffix) < 0) {
            free(suffix);
            fail("HomeHarbor kernel addon command line allocation failed");
        }
        char *sha = cmdline_arg(arg, "");
        if (!is_empty(sha)) {
            char label[256];
            snprintf(label, sizeof(label), "kernel addon %s", state.addon_keys[i]);
            validate_sha256(label, sha);
            set_addon_sha_by_suffix(suffix, sha);
        }
        free(sha);
        free(arg);
        free(suffix);
    }
}

static void record_boot_addons(FILE *file) {
    for (size_t i = 0; i < state.addon_key_count; i++) {
        char *suffix = upper_addon_suffix(state.addon_keys[i]);
        fprintf(file, "HOMEHARBOR_ADDON_%s_SHA256=%s\n", suffix, get_addon_sha(state.addon_keys[i]));
        free(suffix);
    }
}

static void record_boot_env(void) {
    mkdir_p(RUN_DIR, 0755);
    FILE *file = fopen(RUN_DIR "/boot.env", "w");
    if (!file) {
        fail("HomeHarbor could not record boot environment");
    }
    fprintf(file, "HOMEHARBOR_BOOT_MODE=%s\n", nonempty_or(state.boot_mode, "legacy"));
    fprintf(file, "HOMEHARBOR_BOOT_CONFIG=%s\n", nonempty_or(state.boot_config, ""));
    if (!is_empty(state.boot_slot)) {
        fprintf(file, "HOMEHARBOR_BOOT_SLOT=%s\n", state.boot_slot);
    }
    if (!is_empty(state.slot)) {
        fprintf(file, "HOMEHARBOR_SLOT=%s\n", state.slot);
    }
    fprintf(file, "HOMEHARBOR_ROOT_LOGICAL=%s\n", state.root_logical);
    fprintf(file, "HOMEHARBOR_KERNEL_RELEASE=%s\n", state.kernel_release);
    fprintf(file, "HOMEHARBOR_MODULES_LOGICAL=%s\n", state.modules_logical);
    fprintf(file, "HOMEHARBOR_FIRMWARE_LOGICAL=%s\n", state.firmware_logical);
    fprintf(file, "HOMEHARBOR_VBMETA_PARTITION=%s\n", state.vbmeta_partition);
    fprintf(file, "HOMEHARBOR_VBMETA_DIGEST=%s\n", nonempty_or(state.vbmeta_digest, ""));
    fprintf(file, "HOMEHARBOR_ROOT_DESCRIPTOR_DIGEST=%s\n", nonempty_or(state.root_digest, ""));
    fprintf(file, "HOMEHARBOR_MODULES_DESCRIPTOR_DIGEST=%s\n", nonempty_or(state.modules_digest, ""));
    fprintf(file, "HOMEHARBOR_FIRMWARE_DESCRIPTOR_DIGEST=%s\n", nonempty_or(state.firmware_digest, ""));
    if (!is_empty(state.version)) {
        fprintf(file, "HOMEHARBOR_VERSION=%s\n", state.version);
    }
    if (!is_empty(state.addons)) {
        fprintf(file, "HOMEHARBOR_ADDONS=%s\n", state.addons);
        record_boot_addons(file);
    }
    fclose(file);
}

static void save_init_state(void) {
    mkdir_p(RUN_DIR, 0755);
    FILE *file = fopen(STATE_PATH, "w");
    if (!file) {
        fail("HomeHarbor could not record init state");
    }
    fprintf(file, "HOMEHARBOR_EXTERNAL_MOUNTS=%d\n", state.external_mounts);
    fprintf(file, "HOMEHARBOR_DATA_MOUNT_REQUESTED=%d\n", state.data_mount_requested);
    fprintf(file, "HOMEHARBOR_DATA_FILESYSTEM=%s\n", nonempty_or(state.data_filesystem, "btrfs"));
    fprintf(file, "HOMEHARBOR_DATA_RAID_BACKEND=%s\n", nonempty_or(state.data_raid_backend, "filesystem"));
    fprintf(file, "HOMEHARBOR_DATA_MDADM_NAME=%s\n", nonempty_or(state.data_mdadm_name, "homeharbor-data"));
    fprintf(file, "HOMEHARBOR_DATA_FIRST_MAPPER=%s\n", nonempty_or(state.data_first_mapper, ""));
    fprintf(file, "HOMEHARBOR_BOOT_STATE=%s\n", nonempty_or(state.boot_state, "/dev/disk/by-label/state"));
    if (!is_empty(state.addons)) {
        fprintf(file, "HOMEHARBOR_ADDONS=%s\n", state.addons);
        record_boot_addons(file);
    }
    fclose(file);
}

static void load_init_state_line(const char *key, const char *value) {
    if (strcmp(key, "HOMEHARBOR_EXTERNAL_MOUNTS") == 0) {
        state.external_mounts = atoi(value);
    } else if (strcmp(key, "HOMEHARBOR_DATA_MOUNT_REQUESTED") == 0) {
        state.data_mount_requested = atoi(value);
    } else if (strcmp(key, "HOMEHARBOR_DATA_FILESYSTEM") == 0) {
        set_string(&state.data_filesystem, value);
    } else if (strcmp(key, "HOMEHARBOR_DATA_RAID_BACKEND") == 0) {
        set_string(&state.data_raid_backend, value);
    } else if (strcmp(key, "HOMEHARBOR_DATA_MDADM_NAME") == 0) {
        set_string(&state.data_mdadm_name, value);
    } else if (strcmp(key, "HOMEHARBOR_DATA_FIRST_MAPPER") == 0) {
        set_string(&state.data_first_mapper, value);
    } else if (strcmp(key, "HOMEHARBOR_BOOT_STATE") == 0) {
        set_string(&state.boot_state, value);
    } else if (strcmp(key, "HOMEHARBOR_ADDONS") == 0) {
        set_string(&state.addons, value);
    } else {
        read_boot_env_line(key, value);
    }
}

static void load_init_state(void) {
    reset_data_defaults();
    if (file_exists(STATE_PATH)) {
        parse_key_value_file(STATE_PATH, load_init_state_line);
    }
}

static void delete_efi_var_path(const char *path) {
    const char *const chattr_argv[] = {"chattr", "-i", path, NULL};
    (void)run_status(chattr_argv);
    (void)unlink(path);
}

static unsigned char *consume_efi_data_passphrase(size_t *len) {
    char *path = efi_var_path("HomeHarborDataPassphrase");
    unsigned char *bytes = read_bytes_skip(path, 4, 0, len);
    if (bytes) {
        delete_efi_var_path(path);
    }
    free(path);
    return bytes;
}

static void assemble_data_mdadm(void) {
    if (is_empty(state.data_mdadm_uuid)) {
        fail("HomeHarbor data mdadm UUID is missing");
    }
    mkdir_p("/dev/md", 0755);
    char array_path[256];
    char uuid_arg[512];
    snprintf(array_path, sizeof(array_path), "/dev/md/%s", nonempty_or(state.data_mdadm_name, "homeharbor-data"));
    snprintf(uuid_arg, sizeof(uuid_arg), "--uuid=%s", state.data_mdadm_uuid);

    size_t argc = 5 + (size_t)state.data_device_count + 1;
    const char **argv = calloc(argc, sizeof(char *));
    if (!argv) {
        fail("HomeHarbor mdadm argument allocation failed");
    }
    size_t pos = 0;
    argv[pos++] = "mdadm";
    argv[pos++] = "--assemble";
    argv[pos++] = array_path;
    argv[pos++] = "--run";
    argv[pos++] = uuid_arg;
    char **mapper_paths = calloc((size_t)state.data_device_count, sizeof(char *));
    if (!mapper_paths) {
        free(argv);
        fail("HomeHarbor mdadm mapper allocation failed");
    }
    for (int i = 0; i < state.data_device_count; i++) {
        const char *mapper = i < (int)state.data_device_capacity ? state.data_devices[i].mapper : NULL;
        if (is_empty(mapper)) {
            fail("HomeHarbor data mapper is missing");
        }
        if (asprintf(&mapper_paths[i], "/dev/mapper/%s", mapper) < 0) {
            fail("HomeHarbor mdadm mapper allocation failed");
        }
        if (!is_block_device(mapper_paths[i])) {
            fail("HomeHarbor data mapper device not found: %s", mapper);
        }
        argv[pos++] = mapper_paths[i];
    }
    argv[pos] = NULL;
    if (run_status(argv) != 0) {
        fail("HomeHarbor mdadm data assembly failed");
    }
    for (int i = 0; i < state.data_device_count; i++) {
        free(mapper_paths[i]);
    }
    free(mapper_paths);
    free(argv);
}

static void open_data_storage(void) {
    state.data_mount_requested = 0;
    char *statedev = resolve_device(nonempty_or(state.boot_state, "/dev/disk/by-label/state"), 10);
    if (!is_block_device(statedev)) {
        free(statedev);
        return;
    }

    const char *data_mount = "/run/homeharbor-data-state";
    mkdir_p(data_mount, 0755);
    const char *mount_argv[] = {"mount", "-o", "ro", statedev, data_mount, NULL};
    if (run_status(mount_argv) != 0) {
        free(statedev);
        return;
    }
    char *data_env = boot_env_path("/var/lib/homeharbor/storage/boot-unlock.env", data_mount);
    if (!data_env || !file_exists(data_env)) {
        const char *umount_argv[] = {"umount", data_mount, NULL};
        (void)run_status(umount_argv);
        free(data_env);
        free(statedev);
        return;
    }
    read_data_unlock_env(data_env);
    const char *umount_argv[] = {"umount", data_mount, NULL};
    (void)run_status(umount_argv);
    free(data_env);
    free(statedev);

    if (state.data_device_count <= 0) {
        return;
    }

    char *passphrase_file = NULL;
    if (strcmp(nonempty_or(state.data_unlock_mode, ""), "passphrase") == 0) {
        size_t passphrase_len = 0;
        unsigned char *passphrase = consume_efi_data_passphrase(&passphrase_len);
        if (passphrase && passphrase_len > 0) {
            passphrase_file = xstrdup("/run/homeharbor-data-passphrase.key");
            write_file_bytes(passphrase_file, passphrase, passphrase_len, 0600);
        }
        free(passphrase);
    }

    for (int i = 0; i < state.data_device_count; i++) {
        const char *luks_uuid = i < (int)state.data_device_capacity ? state.data_devices[i].luks_uuid : NULL;
        const char *mapper = i < (int)state.data_device_capacity ? state.data_devices[i].mapper : NULL;
        if (is_empty(luks_uuid)) {
            fail("HomeHarbor data LUKS UUID is missing");
        }
        if (is_empty(mapper)) {
            fail("HomeHarbor data mapper is missing");
        }
        char mapper_path[256];
        snprintf(mapper_path, sizeof(mapper_path), "/dev/mapper/%s", mapper);
        if (file_exists(mapper_path)) {
            continue;
        }
        char source_link[512];
        snprintf(source_link, sizeof(source_link), "/dev/disk/by-uuid/%s", luks_uuid);
        char *source = resolve_device(source_link, 10);
        if (!is_block_device(source)) {
            free(source);
            fail("HomeHarbor data LUKS device not found: %s", luks_uuid);
        }
        int ok = 0;
        if (strcmp(nonempty_or(state.data_unlock_mode, ""), "tpm2") == 0) {
            const char *token_argv[] = {"cryptsetup", "open", "--token-only", source, mapper, NULL};
            const char *fallback_argv[] = {"cryptsetup", "open", source, mapper, NULL};
            ok = run_status(token_argv) == 0 || run_status(fallback_argv) == 0;
            if (!ok) {
                free(source);
                fail("HomeHarbor TPM2 data unlock failed");
            }
        } else if (passphrase_file) {
            const char *argv[] = {"cryptsetup", "open", "--key-file", passphrase_file, source, mapper, NULL};
            ok = run_status(argv) == 0;
            if (!ok) {
                free(source);
                fail("HomeHarbor data passphrase unlock failed");
            }
        } else {
            const char *argv[] = {"cryptsetup", "open", source, mapper, NULL};
            ok = run_status(argv) == 0;
            if (!ok) {
                free(source);
                fail("HomeHarbor data passphrase unlock failed");
            }
        }
        free(source);
    }

    if (passphrase_file) {
        (void)unlink(passphrase_file);
        free(passphrase_file);
    }
    if (strcmp(nonempty_or(state.data_raid_backend, "filesystem"), "mdadm") == 0) {
        assemble_data_mdadm();
    }
    state.data_mount_requested = 1;
}

static void append_format(char **buffer, size_t *length, const char *format, ...) {
    va_list args;
    char *line = NULL;
    va_start(args, format);
    if (vasprintf(&line, format, args) < 0) {
        va_end(args);
        fail("HomeHarbor string allocation failed");
    }
    va_end(args);
    size_t line_len = strlen(line);
    char *next = realloc(*buffer, *length + line_len + 1);
    if (!next) {
        free(line);
        fail("HomeHarbor string allocation failed");
    }
    memcpy(next + *length, line, line_len + 1);
    *buffer = next;
    *length += line_len;
    free(line);
}

static char *super_table(const char *superdev, const char *logical) {
    const char *argv[] = {"lpdump", superdev, NULL};
    char *output = run_capture_required(argv, "HomeHarbor lpdump failed");
    char *table = NULL;
    size_t table_len = 0;
    bool found = false;
    bool in_partition = false;
    long long next_sector = 0;
    int count = 0;

    char *save = NULL;
    for (char *line = strtok_r(output, "\n", &save); line; line = strtok_r(NULL, "\n", &save)) {
        size_t len = strlen(line);
        if (len > 0 && line[len - 1] == '\r') {
            line[len - 1] = '\0';
        }
        if (strncmp(line, "  Name: ", 8) == 0) {
            const char *name = line + 8;
            in_partition = strcmp(name, logical) == 0;
            if (in_partition) {
                found = true;
                next_sector = 0;
                count = 0;
                free(table);
                table = NULL;
                table_len = 0;
            }
            continue;
        }
        if (in_partition && strncmp(line, "------------------------", 24) == 0) {
            in_partition = false;
            continue;
        }
        if (!in_partition) {
            continue;
        }
        char *trimmed = line;
        while (isspace((unsigned char)*trimmed)) {
            trimmed++;
        }
        long long start = 0;
        long long end = 0;
        long long physical = 0;
        char dots[4] = "";
        char mode[32] = "";
        char device_name[256] = "";
        if (sscanf(trimmed, "%lld %3s %lld %31s %255s %lld", &start, dots, &end, mode, device_name, &physical) != 6) {
            continue;
        }
        if (strcmp(dots, "..") != 0 || strcmp(mode, "linear") != 0) {
            continue;
        }
        if (end < start) {
            free(output);
            free(table);
            fail("homeharbor super map: extent ends before it starts for %s: %s", logical, trimmed);
        }
        if (count == 0 && start != 0) {
            free(output);
            free(table);
            fail("homeharbor super map: first extent for %s does not start at sector 0", logical);
        }
        if (count > 0 && start != next_sector) {
            free(output);
            free(table);
            fail("homeharbor super map: extents for %s are not contiguous", logical);
        }
        append_format(&table, &table_len, "%lld %lld linear %s %lld\n", start, end - start + 1, superdev, physical);
        next_sector = end + 1;
        count++;
    }
    free(output);
    if (!found) {
        free(table);
        fail("homeharbor super map: logical partition not found: %s", logical);
    }
    if (count == 0) {
        free(table);
        fail("homeharbor super map: logical partition has no linear extents: %s", logical);
    }
    return table;
}

static void dm_remove(const char *mapper_name) {
    if (is_empty(mapper_name)) {
        return;
    }
    const char *deferred[] = {"dmsetup", "remove", "--deferred", mapper_name, NULL};
    const char *normal[] = {"dmsetup", "remove", mapper_name, NULL};
    if (run_status(deferred) != 0) {
        (void)run_status(normal);
    }
}

static void super_create(const char *mapper_name, const char *superdev, const char *logical, bool readonly) {
    const char *info[] = {"dmsetup", "info", mapper_name, NULL};
    if (run_status(info) == 0) {
        dm_remove(mapper_name);
    }
    char *table = super_table(superdev, logical);
    const char *argv_ro[] = {"dmsetup", "-r", "create", mapper_name, NULL};
    const char *argv_rw[] = {"dmsetup", "create", mapper_name, NULL};
    const char *const *argv = readonly ? argv_ro : argv_rw;
    int exit_code = run_command(argv, (const unsigned char *)table, strlen(table), NULL);
    if (exit_code != 0) {
        dm_remove(mapper_name);
        exit_code = run_command(argv, (const unsigned char *)table, strlen(table), NULL);
    }
    free(table);
    if (exit_code != 0) {
        fail("HomeHarbor logical partition map failed: %s", logical);
    }
    char mapper_path[256];
    snprintf(mapper_path, sizeof(mapper_path), "/dev/mapper/%s", mapper_name);
    for (int i = 0; i < 10; i++) {
        if (is_block_device(mapper_path)) {
            return;
        }
        sleep(1);
    }
    fail("homeharbor super map: mapper device did not appear: %s", mapper_path);
}

static void parse_descriptor_line(VerityDescriptor *descriptor, const char *key, const char *value) {
    if (strcmp(key, "VBMETA_DIGEST") == 0) {
        set_string(&descriptor->vbmeta_digest, value);
    } else if (strcmp(key, "TREE_OFFSET") == 0) {
        set_string(&descriptor->tree_offset, value);
    } else if (strcmp(key, "DATA_BLOCK_SIZE") == 0) {
        set_string(&descriptor->data_block_size, value);
    } else if (strcmp(key, "HASH_BLOCK_SIZE") == 0) {
        set_string(&descriptor->hash_block_size, value);
    } else if (strcmp(key, "DATA_BLOCKS") == 0) {
        set_string(&descriptor->data_blocks, value);
    } else if (strcmp(key, "HASH_ALGORITHM") == 0) {
        set_string(&descriptor->hash_algorithm, value);
    } else if (strcmp(key, "SALT") == 0) {
        set_string(&descriptor->salt, value);
    } else if (strcmp(key, "ROOT_DIGEST") == 0) {
        set_string(&descriptor->root_digest, value);
    }
}

static VerityDescriptor parse_descriptor_output(char *output) {
    VerityDescriptor descriptor = {0};
    char *save = NULL;
    for (char *line = strtok_r(output, "\n", &save); line; line = strtok_r(NULL, "\n", &save)) {
        char *equals = strchr(line, '=');
        if (!equals) {
            continue;
        }
        *equals = '\0';
        parse_descriptor_line(&descriptor, line, equals + 1);
    }
    if (is_empty(descriptor.hash_algorithm) || is_empty(descriptor.data_block_size) ||
        is_empty(descriptor.hash_block_size) || is_empty(descriptor.data_blocks) ||
        is_empty(descriptor.tree_offset) || is_empty(descriptor.salt) ||
        is_empty(descriptor.root_digest) || is_empty(descriptor.vbmeta_digest)) {
        fail("HomeHarbor AVB descriptor output is incomplete");
    }
    return descriptor;
}

static void free_descriptor(VerityDescriptor *descriptor) {
    free(descriptor->hash_algorithm);
    free(descriptor->data_block_size);
    free(descriptor->hash_block_size);
    free(descriptor->data_blocks);
    free(descriptor->tree_offset);
    free(descriptor->salt);
    free(descriptor->root_digest);
    free(descriptor->vbmeta_digest);
    memset(descriptor, 0, sizeof(*descriptor));
}

static VerityDescriptor avb_descriptor(const char *vbmeta, const char *partition, const char *expected_digest, const char *message) {
    const char *argv[] = {"/usr/lib/homeharbor/homeharbor-avb", "descriptor", vbmeta, partition, nonempty_or(expected_digest, ""), NULL};
    char *output = run_capture_required(argv, message);
    VerityDescriptor descriptor = parse_descriptor_output(output);
    free(output);
    return descriptor;
}

static void verity_open(const char *label, const char *data_device, const char *target, const VerityDescriptor *descriptor) {
    char hash_arg[128];
    char data_block_arg[128];
    char hash_block_arg[128];
    char data_blocks_arg[128];
    char hash_offset_arg[128];
    char salt_arg[512];
    snprintf(hash_arg, sizeof(hash_arg), "--hash=%s", descriptor->hash_algorithm);
    snprintf(data_block_arg, sizeof(data_block_arg), "--data-block-size=%s", descriptor->data_block_size);
    snprintf(hash_block_arg, sizeof(hash_block_arg), "--hash-block-size=%s", descriptor->hash_block_size);
    snprintf(data_blocks_arg, sizeof(data_blocks_arg), "--data-blocks=%s", descriptor->data_blocks);
    snprintf(hash_offset_arg, sizeof(hash_offset_arg), "--hash-offset=%s", descriptor->tree_offset);
    snprintf(salt_arg, sizeof(salt_arg), "--salt=%s", descriptor->salt);
    const char *argv[] = {
        "veritysetup",
        "--no-superblock",
        hash_arg,
        data_block_arg,
        hash_block_arg,
        data_blocks_arg,
        hash_offset_arg,
        salt_arg,
        "open",
        data_device,
        target,
        data_device,
        descriptor->root_digest,
        NULL
    };
    if (run_status(argv) != 0) {
        fail("HomeHarbor %s dm-verity open failed", label);
    }
}

static void open_super_verity(const char *label, const char *logical, const char *linear, const char *target, const char *superdev, const char *vbmeta) {
    dm_remove(target);
    dm_remove(linear);
    super_create(linear, superdev, logical, true);
    VerityDescriptor descriptor = avb_descriptor(vbmeta, logical, state.vbmeta_digest, "HomeHarbor AVB descriptor lookup failed");
    set_string(&state.vbmeta_digest, descriptor.vbmeta_digest);
    if (strcmp(label, "root") == 0) {
        set_string(&state.root_digest, descriptor.root_digest);
    } else if (strcmp(label, "modules") == 0) {
        set_string(&state.modules_digest, descriptor.root_digest);
    } else if (strcmp(label, "firmware") == 0) {
        set_string(&state.firmware_digest, descriptor.root_digest);
    }
    char device[256];
    snprintf(device, sizeof(device), "/dev/mapper/%s", linear);
    verity_open(label, device, target, &descriptor);
    free_descriptor(&descriptor);
}

static void parse_verity_arg(const char *label, const char *arg, VerityDescriptor *descriptor) {
    char *copy = xstrdup(arg);
    char *save = NULL;
    char *parts[7] = {0};
    int count = 0;
    for (char *part = strtok_r(copy, ":", &save); part && count < 7; part = strtok_r(NULL, ":", &save)) {
        parts[count++] = part;
    }
    if (count != 7 || strtok_r(NULL, ":", &save) != NULL) {
        free(copy);
        fail("HomeHarbor %s verity geometry is invalid", label);
    }
    if (strcmp(parts[0], "sha256") != 0 && strcmp(parts[0], "sha1") != 0) {
        free(copy);
        fail("HomeHarbor %s verity hash algorithm is invalid", label);
    }
    for (int i = 1; i <= 4; i++) {
        if (!chars_match(parts[i], is_digit_char)) {
            free(copy);
            fail("HomeHarbor %s verity geometry is invalid", label);
        }
    }
    if (!chars_match(parts[5], is_hex_char) || !chars_match(parts[6], is_hex_char)) {
        free(copy);
        fail("HomeHarbor %s verity digest is invalid", label);
    }
    set_string(&descriptor->hash_algorithm, parts[0]);
    set_string(&descriptor->data_block_size, parts[1]);
    set_string(&descriptor->hash_block_size, parts[2]);
    set_string(&descriptor->data_blocks, parts[3]);
    set_string(&descriptor->tree_offset, parts[4]);
    set_string(&descriptor->salt, parts[5]);
    set_string(&descriptor->root_digest, parts[6]);
    free(copy);
}

static void open_block_verity_arg(const char *label, const char *data_device, const char *target, const char *arg) {
    VerityDescriptor descriptor = {0};
    parse_verity_arg(label, arg, &descriptor);
    if (strcmp(label, "modules") == 0) {
        set_string(&state.modules_digest, descriptor.root_digest);
    } else if (strcmp(label, "firmware") == 0) {
        set_string(&state.firmware_digest, descriptor.root_digest);
    } else if (strcmp(label, "recovery") == 0) {
        set_string(&state.recovery_digest, descriptor.root_digest);
    }
    dm_remove(target);
    verity_open(label, data_device, target, &descriptor);
    free_descriptor(&descriptor);
}

static void open_super_verity_arg(const char *label, const char *logical, const char *linear, const char *target, const char *arg, const char *superdev) {
    dm_remove(target);
    dm_remove(linear);
    super_create(linear, superdev, logical, true);
    char device[256];
    snprintf(device, sizeof(device), "/dev/mapper/%s", linear);
    open_block_verity_arg(label, device, target, arg);
}

static const char *slot_verity_arg(const char *label, const char *logical) {
    const char *underscore = strrchr(logical, '_');
    const char *slot = underscore ? underscore + 1 : "";
    if (strcmp(label, "modules") == 0) {
        if (strcmp(slot, "a") == 0) {
            return state.modules_a_verity;
        }
        if (strcmp(slot, "b") == 0) {
            return state.modules_b_verity;
        }
    } else if (strcmp(label, "firmware") == 0) {
        if (strcmp(slot, "a") == 0) {
            return state.firmware_a_verity;
        }
        if (strcmp(slot, "b") == 0) {
            return state.firmware_b_verity;
        }
    }
    return "";
}

static void record_recovery_boot_env(const char *slot_label, const char *vbmeta_digest, const char *root_digest) {
    mkdir_p(RUN_DIR, 0755);
    FILE *file = fopen(RUN_DIR "/boot.env", "w");
    if (!file) {
        fail("HomeHarbor could not record recovery boot environment");
    }
    fprintf(file, "HOMEHARBOR_BOOT_SLOT=RECOVERY\n");
    fprintf(file, "HOMEHARBOR_SLOT=RECOVERY\n");
    fprintf(file, "HOMEHARBOR_RECOVERY_SLOT=%s\n", nonempty_or(state.recovery_slot, ""));
    fprintf(file, "HOMEHARBOR_ROOT_LOGICAL=recovery_%s\n", slot_label);
    if (vbmeta_digest) {
        fprintf(file, "HOMEHARBOR_VBMETA_PARTITION=vbmeta_%s\n", slot_label);
        fprintf(file, "HOMEHARBOR_VBMETA_DIGEST=%s\n", vbmeta_digest);
    }
    fprintf(file, "HOMEHARBOR_ROOT_DESCRIPTOR_DIGEST=%s\n", root_digest);
    fclose(file);
}

static void open_recovery_verity(void) {
    char *slot_arg = cmdline_arg("homeharbor.recovery_slot", "");
    if (!is_empty(slot_arg)) {
        set_string(&state.recovery_slot, slot_arg);
    } else {
        apply_boot_current("recovery");
    }
    free(slot_arg);

    char *slot_label = lower_copy(nonempty_or(state.recovery_slot, ""));
    char default_root[256];
    char default_vbmeta[256];
    snprintf(default_root, sizeof(default_root), "/dev/disk/by-partlabel/recovery_%s", slot_label);
    snprintf(default_vbmeta, sizeof(default_vbmeta), "/dev/disk/by-partlabel/vbmeta_%s", slot_label);

    char *recovery_root = cmdline_arg("homeharbor.recovery_root", default_root);
    char *recovery_verity = cmdline_arg("homeharbor.recovery_verity", "");
    char *recovery_a_verity = cmdline_arg("homeharbor.recovery_a_verity", "");
    char *recovery_b_verity = cmdline_arg("homeharbor.recovery_b_verity", "");
    char *recovery_vbmeta = cmdline_arg("homeharbor.recovery_vbmeta", default_vbmeta);
    char *recovery_vbmeta_digest = cmdline_arg("homeharbor.recovery_vbmeta_digest", "");
    char *recovery_vbmeta_a_digest = cmdline_arg("homeharbor.recovery_vbmeta_a_digest", "");
    char *recovery_vbmeta_b_digest = cmdline_arg("homeharbor.recovery_vbmeta_b_digest", "");

    const char *recovery_verity_arg = recovery_verity;
    if (is_empty(recovery_verity_arg)) {
        if (strcmp(slot_label, "a") == 0) {
            recovery_verity_arg = recovery_a_verity;
        } else if (strcmp(slot_label, "b") == 0) {
            recovery_verity_arg = recovery_b_verity;
        }
    }
    if (is_empty(recovery_vbmeta_digest)) {
        if (strcmp(slot_label, "a") == 0) {
            set_string(&recovery_vbmeta_digest, recovery_vbmeta_a_digest);
        } else if (strcmp(slot_label, "b") == 0) {
            set_string(&recovery_vbmeta_digest, recovery_vbmeta_b_digest);
        }
    }

    char *recovery_rootdev = resolve_device(recovery_root, 10);
    if (!is_block_device(recovery_rootdev)) {
        fail("HomeHarbor recovery root device not found: %s", recovery_root);
    }
    if (!is_empty(recovery_verity_arg)) {
        open_block_verity_arg("recovery", recovery_rootdev, "homeharbor-recovery-root", recovery_verity_arg);
        record_recovery_boot_env(slot_label, NULL, state.recovery_digest);
        write_text(ROOT_PATH, "/dev/mapper/homeharbor-recovery-root\n", 0644);
        goto done;
    }

    char *recovery_vbmetadev = resolve_device(recovery_vbmeta, 10);
    if (!is_block_device(recovery_vbmetadev)) {
        fail("HomeHarbor recovery vbmeta device not found: %s", recovery_vbmeta);
    }
    dm_remove("homeharbor-recovery-root");
    char partition[256];
    snprintf(partition, sizeof(partition), "recovery_%s", slot_label);
    VerityDescriptor descriptor = avb_descriptor(
        recovery_vbmetadev,
        partition,
        recovery_vbmeta_digest,
        "HomeHarbor recovery AVB descriptor lookup failed");
    verity_open("recovery", recovery_rootdev, "homeharbor-recovery-root", &descriptor);
    record_recovery_boot_env(slot_label, descriptor.vbmeta_digest, descriptor.root_digest);
    write_text(ROOT_PATH, "/dev/mapper/homeharbor-recovery-root\n", 0644);
    free_descriptor(&descriptor);
    free(recovery_vbmetadev);

done:
    free(recovery_rootdev);
    free(recovery_root);
    free(recovery_verity);
    free(recovery_a_verity);
    free(recovery_b_verity);
    free(recovery_vbmeta);
    free(recovery_vbmeta_digest);
    free(recovery_vbmeta_a_digest);
    free(recovery_vbmeta_b_digest);
    free(slot_label);
}

static void initialize_hook_state(void) {
    set_string(&state.super, cmdline_arg("homeharbor.super", "/dev/disk/by-partlabel/super"));
    set_string(&state.boot_state, cmdline_arg("homeharbor.boot_state", "/dev/disk/by-label/state"));
    set_string(&state.boot_mode, cmdline_arg("homeharbor.boot_mode", "legacy"));
    set_string(&state.boot_slot, cmdline_arg("homeharbor.boot_slot", ""));
    set_string(&state.boot_config, cmdline_arg("homeharbor.boot_config", "/var/lib/homeharbor/ota/boot_a.env"));
    set_string(&state.root_logical, cmdline_arg("homeharbor.root_logical", ""));
    set_string(&state.modules_logical, cmdline_arg("homeharbor.modules_logical", ""));
    set_string(&state.firmware_logical, cmdline_arg("homeharbor.firmware_logical", ""));
    set_string(&state.vbmeta_partition, cmdline_arg("homeharbor.vbmeta_partition", ""));
    set_string(&state.vbmeta_digest, cmdline_arg("homeharbor.vbmeta_digest", ""));
    set_string(&state.vbmeta_a_digest, cmdline_arg("homeharbor.vbmeta_a_digest", ""));
    set_string(&state.vbmeta_b_digest, cmdline_arg("homeharbor.vbmeta_b_digest", ""));
    set_string(&state.modules_a_verity, cmdline_arg("homeharbor.modules_a_verity", ""));
    set_string(&state.modules_b_verity, cmdline_arg("homeharbor.modules_b_verity", ""));
    set_string(&state.firmware_a_verity, cmdline_arg("homeharbor.firmware_a_verity", ""));
    set_string(&state.firmware_b_verity, cmdline_arg("homeharbor.firmware_b_verity", ""));
    set_string(&state.slot, cmdline_arg("homeharbor.slot", ""));
    set_string(&state.kernel_release, cmdline_arg("homeharbor.kernel_release", ""));
    set_string(&state.version, cmdline_arg("homeharbor.version", ""));
    set_string(&state.addons, cmdline_arg("homeharbor.addons", ""));
    reset_data_defaults();
}

static void run_hook(void) {
    initialize_hook_state();
    char *rd_verity = cmdline_arg("rd.homeharbor.verity", "1");
    if (strcmp(rd_verity, "0") == 0) {
        free(rd_verity);
        return;
    }
    free(rd_verity);

    char *recovery = cmdline_arg("homeharbor.recovery", "0");
    if (strcmp(recovery, "1") == 0) {
        free(recovery);
        printf(":: opening HomeHarbor recovery root\n");
        open_recovery_verity();
        save_init_state();
        return;
    }
    free(recovery);

    char *boot_generic = cmdline_arg("homeharbor.boot_generic", "0");
    if (strcmp(boot_generic, "1") == 0) {
        apply_boot_current("normal");
    }
    free(boot_generic);

    if (is_empty(state.boot_slot)) {
        if (strcmp(nonempty_or(state.modules_logical, ""), "modules_a") == 0) {
            set_string(&state.boot_slot, "A");
        } else if (strcmp(nonempty_or(state.modules_logical, ""), "modules_b") == 0) {
            set_string(&state.boot_slot, "B");
        }
    }
    if (is_empty(state.vbmeta_digest)) {
        if (strcmp(nonempty_or(state.vbmeta_partition, ""), "vbmeta_a") == 0) {
            set_string(&state.vbmeta_digest, state.vbmeta_a_digest);
        } else if (strcmp(nonempty_or(state.vbmeta_partition, ""), "vbmeta_b") == 0) {
            set_string(&state.vbmeta_digest, state.vbmeta_b_digest);
        }
    }

    bool need_boot_env = is_empty(state.root_logical) || is_empty(state.modules_logical) ||
        is_empty(state.firmware_logical) || is_empty(state.vbmeta_partition);
    if (need_boot_env) {
        if (strcmp(nonempty_or(state.boot_mode, ""), "raw-uki") == 0 ||
            strcmp(nonempty_or(state.boot_mode, ""), "secure-boot-raw-uki") == 0) {
            fail("HomeHarbor sealed boot cmdline is incomplete");
        }
        char *statedev = resolve_device(nonempty_or(state.boot_state, "/dev/disk/by-label/state"), 10);
        if (!is_block_device(statedev)) {
            fail("HomeHarbor boot state device not found: %s", state.boot_state);
        }
        const char *boot_mount = "/run/homeharbor-boot-state";
        mkdir_p(boot_mount, 0755);
        const char *mount_argv[] = {"mount", "-o", "ro", statedev, boot_mount, NULL};
        if (run_status(mount_argv) != 0) {
            fail("HomeHarbor boot state mount failed");
        }
        char *env_path = boot_env_path(nonempty_or(state.boot_config, ""), boot_mount);
        if (!env_path) {
            fail("HomeHarbor boot env path is invalid");
        }
        if (!file_exists(env_path)) {
            fail("HomeHarbor boot env not found: %s", state.boot_config);
        }
        read_boot_env(env_path);
        const char *umount_argv[] = {"umount", boot_mount, NULL};
        (void)run_status(umount_argv);
        free(env_path);
        free(statedev);
    }

    read_cmdline_addons();
    validate_boot_env();

    printf(":: mapping HomeHarbor verified partitions from Android super\n");
    char *superdev = resolve_device(nonempty_or(state.super, "/dev/disk/by-partlabel/super"), 10);
    if (!is_block_device(superdev)) {
        fail("HomeHarbor super device not found: %s", state.super);
    }
    char default_vbmeta[256];
    snprintf(default_vbmeta, sizeof(default_vbmeta), "/dev/disk/by-partlabel/%s", state.vbmeta_partition);
    char *vbmeta_arg = cmdline_arg("homeharbor.vbmeta", default_vbmeta);
    char *vbmeta_device = resolve_device(vbmeta_arg, 10);
    if (!is_block_device(vbmeta_device)) {
        fail("HomeHarbor vbmeta device not found: %s", vbmeta_arg);
    }

    const char *modules_verity_arg = slot_verity_arg("modules", state.modules_logical);
    const char *firmware_verity_arg = slot_verity_arg("firmware", state.firmware_logical);
    open_super_verity("root", state.root_logical, "homeharbor-super-root", "homeharbor-root", superdev, vbmeta_device);
    if (!is_empty(modules_verity_arg)) {
        open_super_verity_arg("modules", state.modules_logical, "homeharbor-super-modules", "homeharbor-modules", modules_verity_arg, superdev);
    } else {
        open_super_verity("modules", state.modules_logical, "homeharbor-super-modules", "homeharbor-modules", superdev, vbmeta_device);
    }
    if (!is_empty(firmware_verity_arg)) {
        open_super_verity_arg("firmware", state.firmware_logical, "homeharbor-super-firmware", "homeharbor-firmware", firmware_verity_arg, superdev);
    } else {
        open_super_verity("firmware", state.firmware_logical, "homeharbor-super-firmware", "homeharbor-firmware", superdev, vbmeta_device);
    }
    open_data_storage();

    state.external_mounts = 1;
    record_boot_env();
    save_init_state();
    write_text(ROOT_PATH, "/dev/mapper/homeharbor-root\n", 0644);

    free(vbmeta_device);
    free(vbmeta_arg);
    free(superdev);
}

static void append_lowerdir(char **lowerdirs, const char *path) {
    if (strchr(path, ':')) {
        fail("HomeHarbor overlay lowerdir path contains ':'");
    }
    char *next = NULL;
    if (is_empty(*lowerdirs)) {
        next = xstrdup(path);
    } else if (asprintf(&next, "%s:%s", *lowerdirs, path) < 0) {
        fail("HomeHarbor overlay lowerdir allocation failed");
    }
    free(*lowerdirs);
    *lowerdirs = next;
}

static char *sha256sum_file(const char *path) {
    const char *argv[] = {"sha256sum", path, NULL};
    char *output = run_capture_required(argv, "HomeHarbor kernel addon hash check failed");
    char *space = strpbrk(output, " \t\r\n");
    if (space) {
        *space = '\0';
    }
    return output;
}

static void mount_kernel_addon(const char *addon, char **lowerdirs) {
    const char *sha = get_addon_sha(addon);
    char label[256];
    snprintf(label, sizeof(label), "kernel addon %s", addon);
    validate_sha256(label, sha);
    char image[512];
    snprintf(image, sizeof(image), "/run/homeharbor-addon-state/lib/homeharbor/ota/addons/store/%s.erofs", sha);
    if (!file_exists(image)) {
        fail("HomeHarbor kernel addon image is missing: %s", addon);
    }
    char *actual = sha256sum_file(image);
    if (strcmp(actual, sha) != 0) {
        free(actual);
        fail("HomeHarbor kernel addon image hash mismatch: %s", addon);
    }
    free(actual);

    char mount_dir[512];
    snprintf(mount_dir, sizeof(mount_dir), "/run/homeharbor-kernel-addons/%s", addon);
    remove_tree(mount_dir);
    mkdir_p(mount_dir, 0755);
    const char *argv[] = {"mount", "-o", "ro,loop", "-t", "erofs", image, mount_dir, NULL};
    if (run_status(argv) != 0) {
        fail("HomeHarbor kernel addon mount failed: %s", addon);
    }
    char *addon_usr = NULL;
    if (asprintf(&addon_usr, "%s/usr", mount_dir) < 0) {
        fail("HomeHarbor kernel addon path allocation failed");
    }
    struct stat st;
    if (stat(addon_usr, &st) != 0 || !S_ISDIR(st.st_mode)) {
        free(addon_usr);
        fail("HomeHarbor kernel addon does not contain /usr: %s", addon);
    }
    append_lowerdir(lowerdirs, addon_usr);
    free(addon_usr);
}

static char *mount_kernel_addons(void) {
    char *lowerdirs = NULL;
    parse_addon_keys();
    if (state.addon_key_count == 0) {
        return lowerdirs;
    }
    validate_addons();
    char *statedev = resolve_device(nonempty_or(state.boot_state, "/dev/disk/by-label/state"), 10);
    if (!is_block_device(statedev)) {
        fail("HomeHarbor addon state device not found: %s", state.boot_state);
    }
    const char *mount_dir = "/run/homeharbor-addon-state";
    remove_tree(mount_dir);
    mkdir_p(mount_dir, 0755);
    const char *argv[] = {"mount", "-o", "ro", statedev, mount_dir, NULL};
    if (run_status(argv) != 0) {
        fail("HomeHarbor addon state mount failed");
    }
    for (size_t i = 0; i < state.addon_key_count; i++) {
        mount_kernel_addon(state.addon_keys[i], &lowerdirs);
    }
    free(statedev);
    return lowerdirs;
}

static void append_system_app_usr_lowerdirs(char **lowerdirs) {
    glob_t matches;
    memset(&matches, 0, sizeof(matches));
    int rc = glob("/new_root/homeharbor-data/system-apps/active/*/usr", GLOB_NOSORT, NULL, &matches);
    if (rc == 0) {
        for (size_t i = 0; i < matches.gl_pathc; i++) {
            struct stat st;
            if (stat(matches.gl_pathv[i], &st) == 0 && S_ISDIR(st.st_mode)) {
                append_lowerdir(lowerdirs, matches.gl_pathv[i]);
            }
        }
    }
    globfree(&matches);
}

static void mount_usr_overlay(void) {
    char *lowerdirs = mount_kernel_addons();
    append_system_app_usr_lowerdirs(&lowerdirs);
    if (is_empty(lowerdirs)) {
        free(lowerdirs);
        return;
    }
    append_lowerdir(&lowerdirs, "/new_root/usr");
    const char *overlay_mount = "/run/homeharbor-usr-overlay";
    remove_tree(overlay_mount);
    mkdir_p(overlay_mount, 0755);
    char *option = NULL;
    if (asprintf(&option, "lowerdir=%s", lowerdirs) < 0) {
        free(lowerdirs);
        fail("HomeHarbor /usr overlay option allocation failed");
    }
    const char *mount_argv[] = {"mount", "-t", "overlay", "overlay", "-o", option, overlay_mount, NULL};
    if (run_status(mount_argv) != 0) {
        free(option);
        free(lowerdirs);
        fail("HomeHarbor /usr overlay mount failed");
    }
    const char *move_argv[] = {"mount", "--move", overlay_mount, "/new_root/usr", NULL};
    if (run_status(move_argv) != 0) {
        const char *umount_argv[] = {"umount", overlay_mount, NULL};
        (void)run_status(umount_argv);
        free(option);
        free(lowerdirs);
        fail("HomeHarbor /usr overlay move failed");
    }
    free(option);
    free(lowerdirs);
}

static void run_latehook(void) {
    load_init_state();
    if (state.external_mounts != 1) {
        return;
    }
    mkdir_p("/new_root/usr/lib/modules", 0755);
    mkdir_p("/new_root/usr/lib/firmware", 0755);

    if (state.data_mount_requested == 1 && !is_empty(state.data_first_mapper) &&
        strcmp(nonempty_or(state.data_filesystem, "btrfs"), "zfs") != 0) {
        mkdir_p("/new_root/homeharbor-data", 0755);
        const char *options = strcmp(nonempty_or(state.data_filesystem, "btrfs"), "btrfs") == 0
            ? "noatime,compress=zstd"
            : "noatime";
        char source[256];
        if (strcmp(nonempty_or(state.data_raid_backend, "filesystem"), "mdadm") == 0) {
            snprintf(source, sizeof(source), "/dev/md/%s", nonempty_or(state.data_mdadm_name, "homeharbor-data"));
        } else {
            snprintf(source, sizeof(source), "/dev/mapper/%s", state.data_first_mapper);
        }
        if (!is_block_device(source)) {
            fail("HomeHarbor data mount source is missing");
        }
        const char *argv[] = {"mount", "-t", nonempty_or(state.data_filesystem, "btrfs"), "-o", options, source, "/new_root/homeharbor-data", NULL};
        if (run_status(argv) != 0) {
            fail("HomeHarbor data mount failed");
        }
    }

    mount_usr_overlay();
    const char *modules_argv[] = {"mount", "-o", "ro", "-t", "erofs", "/dev/mapper/homeharbor-modules", "/new_root/usr/lib/modules", NULL};
    if (run_status(modules_argv) != 0) {
        fail("HomeHarbor modules mount failed");
    }
    const char *firmware_argv[] = {"mount", "-o", "ro", "-t", "erofs", "/dev/mapper/homeharbor-firmware", "/new_root/usr/lib/firmware", NULL};
    if (run_status(firmware_argv) != 0) {
        fail("HomeHarbor firmware mount failed");
    }
}

int main(int argc, char **argv) {
    if (argc != 2) {
        fprintf(stderr, "usage: homeharbor-verity hook|latehook\n");
        return 2;
    }
    if (strcmp(argv[1], "hook") == 0) {
        run_hook();
        return 0;
    }
    if (strcmp(argv[1], "latehook") == 0) {
        run_latehook();
        return 0;
    }
    fprintf(stderr, "unknown command: %s\n", argv[1]);
    return 2;
}
