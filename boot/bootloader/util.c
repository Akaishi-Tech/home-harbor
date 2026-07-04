#include "globals.h"
#include "util.h"

static int byte_is_space(char c) {
    return c == ' ' || c == '\n' || c == '\r' || c == '\t';
}
void print(CHAR16 *message) {
    if (gST && gST->ConOut) {
        gST->ConOut->OutputString(gST->ConOut, message);
    }
}

int char16_equal(CHAR16 *a, const CHAR16 *b) {
    while (*a || *b) {
        if (*a != *b) {
            return 0;
        }
        a++;
        b++;
    }
    return 1;
}

int find_json_string(char *json, UINTN length, const char *key, char *output, UINTN output_length) {
    UINTN i;
    UINTN key_len = 0;
    while (key[key_len]) {
        key_len++;
    }
    if (output_length == 0) {
        return 0;
    }
    output[0] = 0;
    for (i = 0; i + key_len + 3 < length; i++) {
        UINTN k;
        if (json[i] != '"') {
            continue;
        }
        for (k = 0; k < key_len && i + 1 + k < length && json[i + 1 + k] == key[k]; k++) {
        }
        if (k != key_len || i + 1 + key_len >= length || json[i + 1 + key_len] != '"') {
            continue;
        }
        i = i + 2 + key_len;
        while (i < length && byte_is_space(json[i])) {
            i++;
        }
        if (i >= length || json[i] != ':') {
            continue;
        }
        i++;
        while (i < length && byte_is_space(json[i])) {
            i++;
        }
        if (i >= length || json[i] != '"') {
            return 0;
        }
        i++;
        for (k = 0; i < length && json[i] != '"' && k + 1 < output_length; i++, k++) {
            output[k] = json[i];
        }
        output[k] = 0;
        return k > 0;
    }
    return 0;
}

int ascii_starts_with(char *text, const char *prefix) {
    while (*prefix) {
        if (*text != *prefix) {
            return 0;
        }
        text++;
        prefix++;
    }
    return 1;
}

void ascii_copy(char *destination, UINTN destination_length, const char *source) {
    UINTN i;
    if (destination_length == 0) {
        return;
    }
    for (i = 0; source[i] && i + 1 < destination_length; i++) {
        destination[i] = source[i];
    }
    destination[i] = 0;
}

UINTN ascii_length(char *text) {
    UINTN length = 0;
    while (text[length]) {
        length++;
    }
    return length;
}

char payload_component_slot(char *payload, UINTN component_index) {
    UINTN i = 0;
    UINTN component = 0;

    while (payload[i]) {
        UINTN start = i;
        while (payload[i] && payload[i] != ':') {
            i++;
        }
        if (component == component_index) {
            if (i == start + 1 && (payload[start] == 'A' || payload[start] == 'a')) {
                return 'A';
            }
            if (i == start + 1 && (payload[start] == 'B' || payload[start] == 'b')) {
                return 'B';
            }
            return 0;
        }
        if (!payload[i]) {
            break;
        }
        i++;
        component++;
    }
    return 0;
}

UINTN trim_trailing_zeroes(UINT8 *buffer, UINTN size) {
    while (size > 0 && buffer[size - 1] == 0) {
        size--;
    }
    return size;
}

UINT16 read_u16(UINT8 *buffer, UINTN offset) {
    return ((UINT16)buffer[offset]) | (((UINT16)buffer[offset + 1]) << 8);
}

UINT32 read_u32(UINT8 *buffer, UINTN offset) {
    return ((UINT32)buffer[offset]) |
        (((UINT32)buffer[offset + 1]) << 8) |
        (((UINT32)buffer[offset + 2]) << 16) |
        (((UINT32)buffer[offset + 3]) << 24);
}

UINT64 read_u64(UINT8 *buffer, UINTN offset) {
    return ((UINT64)read_u32(buffer, offset)) |
        (((UINT64)read_u32(buffer, offset + 4)) << 32);
}

UINT32 read_be32(const UINT8 *buffer, UINTN offset) {
    return (((UINT32)buffer[offset]) << 24) |
        (((UINT32)buffer[offset + 1]) << 16) |
        (((UINT32)buffer[offset + 2]) << 8) |
        ((UINT32)buffer[offset + 3]);
}

UINT64 read_be64(const UINT8 *buffer, UINTN offset) {
    return (((UINT64)read_be32(buffer, offset)) << 32) |
        ((UINT64)read_be32(buffer, offset + 4));
}

UINTN max_un(UINTN a, UINTN b) {
    return a > b ? a : b;
}

UINTN char16_length(const CHAR16 *text) {
    UINTN length = 0;
    while (text[length]) {
        length++;
    }
    return length;
}

void copy_bytes(void *destination, const void *source, UINTN length) {
    UINT8 *dest = (UINT8 *)destination;
    const UINT8 *src = (const UINT8 *)source;
    UINTN i;
    for (i = 0; i < length; i++) {
        dest[i] = src[i];
    }
}

void zero_bytes(void *buffer, UINTN length) {
    UINT8 *bytes = (UINT8 *)buffer;
    UINTN i;
    for (i = 0; i < length; i++) {
        bytes[i] = 0;
    }
}

int bytes_equal(const UINT8 *a, const UINT8 *b, UINTN length) {
    UINT8 diff = 0;
    UINTN i;
    for (i = 0; i < length; i++) {
        diff |= (UINT8)(a[i] ^ b[i]);
    }
    return diff == 0;
}
