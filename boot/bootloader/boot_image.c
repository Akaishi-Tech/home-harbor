#include "boot_image.h"
#include "erofs_reader.h"
#include "esp_cache.h"
#include "globals.h"
#include "partitions.h"
#include "pe.h"
#include "util.h"

typedef struct {
    EFI_BLOCK_IO_PROTOCOL *Block;
    UINT8 *Scratch;
} EFI_BLOCK_READER_CONTEXT;

static EFI_STATUS loader_allocate(UINTN size, void **buffer) {
    return gBS->AllocatePool(EFI_LOADER_DATA, size, buffer);
}

static void loader_free(void *buffer) {
    gBS->FreePool(buffer);
}

static EFI_STATUS read_partition_bytes(void *context, UINT64 offset, UINTN size, void *buffer) {
    EFI_BLOCK_READER_CONTEXT *reader = (EFI_BLOCK_READER_CONTEXT *)context;
    EFI_BLOCK_IO_PROTOCOL *block = reader->Block;
    UINTN block_size = block->Media->BlockSize;
    UINT8 *output = (UINT8 *)buffer;

    while (size > 0) {
        UINT64 lba = offset / block_size;
        UINTN block_offset = (UINTN)(offset % block_size);
        UINTN chunk = block_size - block_offset;
        EFI_STATUS status;

        if (chunk > size) {
            chunk = size;
        }
        status = block->ReadBlocks(block, block->Media->MediaId, lba, block_size, reader->Scratch);
        if (EFI_ERROR(status)) {
            return status;
        }
        copy_bytes(output, reader->Scratch + block_offset, chunk);
        output += chunk;
        offset += chunk;
        size -= chunk;
    }

    return EFI_SUCCESS;
}

static EFI_STATUS start_boot_buffer(EFI_HANDLE parent, void *buffer, UINTN size) {
    const CHAR16 *cache_path = L"\\EFI\\HomeHarbor\\current.efi";
    EFI_HANDLE image = NULL;
    EFI_STATUS status;

    size = detect_pe_file_size((UINT8 *)buffer, size);
    if (size < 4096) {
        print(L"HomeHarborBoot: boot image is invalid\r\n");
        return 1;
    }
    status = gBS->LoadImage(0, parent, NULL, buffer, size, &image);
    if (EFI_ERROR(status) || !image) {
        print(L"HomeHarborBoot: memory LoadImage failed; trying ESP cache\r\n");
        status = write_cached_boot_image(parent, cache_path, buffer, size);
        if (!EFI_ERROR(status)) {
            status = load_cached_boot_image(parent, cache_path, &image);
        }
    }
    if (EFI_ERROR(status) || !image) {
        print(L"HomeHarborBoot: UEFI LoadImage failed\r\n");
        return status;
    }
    return gBS->StartImage(image, NULL, NULL);
}

EFI_STATUS boot_raw_partition(EFI_HANDLE parent, const CHAR16 *label) {
    EFI_BLOCK_IO_PROTOCOL *block = find_partition_by_label(label);
    void *buffer = NULL;
    UINTN size;
    EFI_STATUS status;

    if (!block || !block->Media || block->Media->BlockSize == 0) {
        print(L"HomeHarborBoot: target boot partition was not found\r\n");
        return 1;
    }
    size = (UINTN)((block->Media->LastBlock + 1) * block->Media->BlockSize);
    if (size == 0 || size > BOOT_IMAGE_MAX_BYTES) {
        print(L"HomeHarborBoot: target boot partition size is invalid\r\n");
        return 1;
    }
    status = gBS->AllocatePool(EFI_LOADER_DATA, size, &buffer);
    if (EFI_ERROR(status) || !buffer) {
        print(L"HomeHarborBoot: could not allocate boot image buffer\r\n");
        return status;
    }
    status = block->ReadBlocks(block, block->Media->MediaId, 0, size, buffer);
    if (EFI_ERROR(status)) {
        print(L"HomeHarborBoot: could not read boot image partition\r\n");
        gBS->FreePool(buffer);
        return status;
    }
    status = start_boot_buffer(parent, buffer, size);
    gBS->FreePool(buffer);
    return status;
}

EFI_STATUS boot_recovery_erofs_partition(EFI_HANDLE parent, const CHAR16 *label) {
    EFI_BLOCK_IO_PROTOCOL *block = find_partition_by_label(label);
    EFI_BLOCK_READER_CONTEXT context;
    HOMEHARBOR_EROFS_READER reader;
    void *buffer = NULL;
    UINTN size = 0;
    UINT64 partition_size;
    EFI_STATUS status;

    if (!block || !block->Media || block->Media->BlockSize == 0) {
        print(L"HomeHarborBoot: target recovery partition was not found\r\n");
        return 1;
    }
    partition_size = (block->Media->LastBlock + 1) * (UINT64)block->Media->BlockSize;
    if (partition_size == 0 || partition_size > RECOVERY_IMAGE_MAX_BYTES) {
        print(L"HomeHarborBoot: target recovery partition size is invalid\r\n");
        return 1;
    }

    context.Block = block;
    context.Scratch = NULL;
    status = gBS->AllocatePool(EFI_LOADER_DATA, block->Media->BlockSize, (void **)&context.Scratch);
    if (EFI_ERROR(status) || !context.Scratch) {
        print(L"HomeHarborBoot: could not allocate recovery reader buffer\r\n");
        return status;
    }

    reader.Context = &context;
    reader.ImageSize = partition_size;
    reader.Read = read_partition_bytes;
    reader.Allocate = loader_allocate;
    reader.Free = loader_free;
    status = erofs_read_file(&reader, "/boot/recovery_boot.efi", &buffer, &size);
    gBS->FreePool(context.Scratch);
    if (EFI_ERROR(status) || !buffer || size == 0) {
        print(L"HomeHarborBoot: recovery UKI was not found in recovery EROFS\r\n");
        return status;
    }

    status = start_boot_buffer(parent, buffer, size);
    gBS->FreePool(buffer);
    return status;
}
