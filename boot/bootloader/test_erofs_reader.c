#include "erofs_reader.h"
#include "globals.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

EFI_SYSTEM_TABLE *gST;
EFI_BOOT_SERVICES *gBS;
EFI_RUNTIME_SERVICES *gRT;

typedef struct {
    UINT8 *bytes;
    UINT64 size;
} HOST_IMAGE;

static EFI_STATUS host_read(void *context, UINT64 offset, UINTN size, void *buffer) {
    HOST_IMAGE *image = (HOST_IMAGE *)context;
    if (!image || !buffer || offset > image->size || size > image->size - offset) {
        return 1;
    }
    memcpy(buffer, image->bytes + offset, (size_t)size);
    return EFI_SUCCESS;
}

static EFI_STATUS host_allocate(UINTN size, void **buffer) {
    if (!buffer || size == 0) {
        return 1;
    }
    *buffer = malloc((size_t)size);
    return *buffer ? EFI_SUCCESS : 1;
}

static void host_free(void *buffer) {
    free(buffer);
}

static int read_all(const char *path, UINT8 **bytes, UINT64 *size) {
    FILE *file;
    long length;

    *bytes = NULL;
    *size = 0;
    file = fopen(path, "rb");
    if (!file) {
        perror(path);
        return 1;
    }
    if (fseek(file, 0, SEEK_END) != 0 || (length = ftell(file)) < 0 || fseek(file, 0, SEEK_SET) != 0) {
        fclose(file);
        return 1;
    }
    *bytes = malloc((size_t)length);
    if (!*bytes && length != 0) {
        fclose(file);
        return 1;
    }
    if (length != 0 && fread(*bytes, 1, (size_t)length, file) != (size_t)length) {
        free(*bytes);
        *bytes = NULL;
        fclose(file);
        return 1;
    }
    fclose(file);
    *size = (UINT64)length;
    return 0;
}

int main(int argc, char **argv) {
    HOST_IMAGE image;
    UINT8 *expected = NULL;
    UINT64 expected_size = 0;
    void *actual = NULL;
    UINTN actual_size = 0;
    HOMEHARBOR_EROFS_READER reader;
    int should_succeed;
    EFI_STATUS status;
    int result = 1;

    if (argc != 4) {
        fprintf(stderr, "usage: %s IMAGE EXPECTED 0|1\n", argv[0]);
        return 2;
    }

    should_succeed = strcmp(argv[3], "1") == 0;
    if (!should_succeed && strcmp(argv[3], "0") != 0) {
        fprintf(stderr, "expected third argument to be 0 or 1\n");
        return 2;
    }

    if (read_all(argv[1], &image.bytes, &image.size) != 0) {
        return 1;
    }

    reader.Context = &image;
    reader.ImageSize = image.size;
    reader.Read = host_read;
    reader.Allocate = host_allocate;
    reader.Free = host_free;

    status = erofs_read_file(&reader, "/boot/recovery_boot.efi", &actual, &actual_size);
    if (!should_succeed) {
        result = status == EFI_SUCCESS ? 1 : 0;
        if (actual) {
            host_free(actual);
        }
        free(image.bytes);
        return result;
    }

    if (status != EFI_SUCCESS) {
        fprintf(stderr, "reader failed to load /boot/recovery_boot.efi\n");
        goto cleanup;
    }
    if (read_all(argv[2], &expected, &expected_size) != 0) {
        goto cleanup;
    }
    if ((UINT64)actual_size != expected_size || memcmp(actual, expected, (size_t)expected_size) != 0) {
        fprintf(stderr, "reader output did not match expected UKI bytes\n");
        goto cleanup;
    }

    result = 0;

cleanup:
    if (actual) {
        host_free(actual);
    }
    free(expected);
    free(image.bytes);
    return result;
}
