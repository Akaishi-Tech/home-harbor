#include "globals.h"
#include "partitions.h"
#include "util.h"

EFI_BLOCK_IO_PROTOCOL *find_partition_by_label(const CHAR16 *label) {
    EFI_HANDLE *handles = NULL;
    UINTN count = 0;
    UINTN i;
    EFI_STATUS status = gBS->LocateHandleBuffer(BY_PROTOCOL, &PartitionInfoGuid, NULL, &count, &handles);
    if (EFI_ERROR(status) || !handles) {
        return NULL;
    }
    for (i = 0; i < count; i++) {
        EFI_PARTITION_INFO_PROTOCOL *part = NULL;
        EFI_BLOCK_IO_PROTOCOL *block = NULL;
        status = gBS->HandleProtocol(handles[i], &PartitionInfoGuid, (void **)&part);
        if (EFI_ERROR(status) || !part || part->Type != 2) {
            continue;
        }
        if (!char16_equal(part->Info.Gpt.PartitionName, label)) {
            continue;
        }
        status = gBS->HandleProtocol(handles[i], &BlockIoGuid, (void **)&block);
        if (!EFI_ERROR(status) && block && block->Media && block->Media->MediaPresent) {
            gBS->FreePool(handles);
            return block;
        }
    }
    gBS->FreePool(handles);
    return NULL;
}
EFI_STATUS read_raw_partition(const CHAR16 *label, void **buffer, UINTN *size) {
    EFI_BLOCK_IO_PROTOCOL *block = find_partition_by_label(label);
    EFI_STATUS status;

    *buffer = NULL;
    *size = 0;
    if (!block || !block->Media || block->Media->BlockSize == 0) {
        return 1;
    }

    *size = (UINTN)((block->Media->LastBlock + 1) * block->Media->BlockSize);
    if (*size == 0 || *size > VBMETA_IMAGE_MAX_BYTES) {
        return 1;
    }

    status = gBS->AllocatePool(EFI_LOADER_DATA, *size, buffer);
    if (EFI_ERROR(status) || !*buffer) {
        return status;
    }

    status = block->ReadBlocks(block, block->Media->MediaId, 0, *size, *buffer);
    if (EFI_ERROR(status)) {
        gBS->FreePool(*buffer);
        *buffer = NULL;
        *size = 0;
    }
    return status;
}
