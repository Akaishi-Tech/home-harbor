#include "boot_state.h"
#include "globals.h"
#include "util.h"

EFI_STATUS read_boot_state(EFI_HANDLE image, char *slot, char *root_slot, char *mode, char *recovery_slot) {
    EFI_LOADED_IMAGE_PROTOCOL *loaded = NULL;
    EFI_SIMPLE_FILE_SYSTEM_PROTOCOL *fs = NULL;
    EFI_FILE_PROTOCOL *root = NULL;
    EFI_FILE_PROTOCOL *file = NULL;
    EFI_STATUS status;
    char buffer[2048];
    UINTN size = sizeof(buffer) - 1;

    slot[0] = 'A';
    slot[1] = 0;
    root_slot[0] = 'A';
    root_slot[1] = 0;
    mode[0] = 0;
    recovery_slot[0] = 'A';
    recovery_slot[1] = 0;

    status = gBS->HandleProtocol(image, &LoadedImageProtocolGuid, (void **)&loaded);
    if (EFI_ERROR(status) || !loaded) {
        return status;
    }
    status = gBS->HandleProtocol(loaded->DeviceHandle, &SimpleFileSystemGuid, (void **)&fs);
    if (EFI_ERROR(status) || !fs) {
        return status;
    }
    status = fs->OpenVolume(fs, &root);
    if (EFI_ERROR(status) || !root) {
        return status;
    }
    status = root->Open(root, &file, L"\\EFI\\HomeHarbor\\boot_state.json", EFI_FILE_MODE_READ, 0);
    if (EFI_ERROR(status) || !file) {
        root->Close(root);
        return status;
    }
    status = file->Read(file, &size, buffer);
    file->Close(file);
    root->Close(root);
    if (EFI_ERROR(status)) {
        return status;
    }
    buffer[size] = 0;

    find_json_string(buffer, size, "defaultSlot", slot, 4);
    if (!find_json_string(buffer, size, "defaultRootSlot", root_slot, 4)) {
        root_slot[0] = slot[0];
        root_slot[1] = 0;
    }
    find_json_string(buffer, size, "recoverySlot", recovery_slot, 4);
    return EFI_SUCCESS;
}
