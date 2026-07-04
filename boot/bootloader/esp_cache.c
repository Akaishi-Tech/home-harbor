#include "esp_cache.h"
#include "globals.h"
#include "util.h"

static void set_device_path_length(EFI_DEVICE_PATH_PROTOCOL *node, UINTN length) {
    node->Length[0] = (UINT8)(length & 0xff);
    node->Length[1] = (UINT8)((length >> 8) & 0xff);
}

static UINTN device_path_node_length(EFI_DEVICE_PATH_PROTOCOL *node) {
    return ((UINTN)node->Length[0]) | (((UINTN)node->Length[1]) << 8);
}

static int is_device_path_end(EFI_DEVICE_PATH_PROTOCOL *node) {
    return node->Type == END_DEVICE_PATH_TYPE;
}

static UINTN device_path_size_without_end(EFI_DEVICE_PATH_PROTOCOL *path) {
    EFI_DEVICE_PATH_PROTOCOL *node = path;
    UINTN size = 0;
    while (!is_device_path_end(node)) {
        UINTN length = device_path_node_length(node);
        if (length < sizeof(EFI_DEVICE_PATH_PROTOCOL)) {
            return 0;
        }
        size += length;
        node = (EFI_DEVICE_PATH_PROTOCOL *)((UINT8 *)node + length);
    }
    return size;
}

static EFI_STATUS build_esp_file_device_path(EFI_HANDLE parent, const CHAR16 *file_name, EFI_DEVICE_PATH_PROTOCOL **path) {
    EFI_LOADED_IMAGE_PROTOCOL *loaded = NULL;
    EFI_DEVICE_PATH_PROTOCOL *base_path = NULL;
    EFI_DEVICE_PATH_PROTOCOL *file_node;
    EFI_DEVICE_PATH_PROTOCOL *end_node;
    EFI_STATUS status;
    UINTN base_size;
    UINTN file_name_bytes;
    UINTN file_node_size;
    UINTN total_size;

    *path = NULL;
    status = gBS->HandleProtocol(parent, &LoadedImageProtocolGuid, (void **)&loaded);
    if (EFI_ERROR(status) || !loaded) {
        return status;
    }
    status = gBS->HandleProtocol(loaded->DeviceHandle, &DevicePathProtocolGuid, (void **)&base_path);
    if (EFI_ERROR(status) || !base_path) {
        return status;
    }

    base_size = device_path_size_without_end(base_path);
    if (base_size == 0) {
        return 1;
    }
    file_name_bytes = (char16_length(file_name) + 1) * sizeof(CHAR16);
    file_node_size = sizeof(EFI_DEVICE_PATH_PROTOCOL) + file_name_bytes;
    total_size = base_size + file_node_size + sizeof(EFI_DEVICE_PATH_PROTOCOL);
    status = gBS->AllocatePool(EFI_LOADER_DATA, total_size, (void **)path);
    if (EFI_ERROR(status) || !*path) {
        return status;
    }

    copy_bytes(*path, base_path, base_size);
    file_node = (EFI_DEVICE_PATH_PROTOCOL *)((UINT8 *)*path + base_size);
    file_node->Type = MEDIA_DEVICE_PATH;
    file_node->SubType = MEDIA_FILEPATH_DP;
    set_device_path_length(file_node, file_node_size);
    copy_bytes((UINT8 *)file_node + sizeof(EFI_DEVICE_PATH_PROTOCOL), file_name, file_name_bytes);

    end_node = (EFI_DEVICE_PATH_PROTOCOL *)((UINT8 *)file_node + file_node_size);
    end_node->Type = END_DEVICE_PATH_TYPE;
    end_node->SubType = END_ENTIRE_DEVICE_PATH_SUBTYPE;
    set_device_path_length(end_node, sizeof(EFI_DEVICE_PATH_PROTOCOL));
    return EFI_SUCCESS;
}
EFI_STATUS write_cached_boot_image(EFI_HANDLE parent, const CHAR16 *file_name, void *buffer, UINTN size) {
    EFI_LOADED_IMAGE_PROTOCOL *loaded = NULL;
    EFI_SIMPLE_FILE_SYSTEM_PROTOCOL *fs = NULL;
    EFI_FILE_PROTOCOL *root = NULL;
    EFI_FILE_PROTOCOL *file = NULL;
    EFI_STATUS status;
    UINTN offset = 0;

    status = gBS->HandleProtocol(parent, &LoadedImageProtocolGuid, (void **)&loaded);
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

    status = root->Open(root, &file, (CHAR16 *)file_name, EFI_FILE_MODE_READ | EFI_FILE_MODE_WRITE, 0);
    if (!EFI_ERROR(status) && file) {
        file->Delete(file);
        file = NULL;
    }

    status = root->Open(
        root,
        &file,
        (CHAR16 *)file_name,
        EFI_FILE_MODE_READ | EFI_FILE_MODE_WRITE | EFI_FILE_MODE_CREATE,
        EFI_FILE_ARCHIVE);
    if (EFI_ERROR(status) || !file) {
        root->Close(root);
        return status;
    }

    while (offset < size) {
        UINTN remaining = size - offset;
        UINTN chunk = remaining > BOOT_CACHE_CHUNK_BYTES ? BOOT_CACHE_CHUNK_BYTES : remaining;
        UINTN written = chunk;
        status = file->Write(file, &written, (UINT8 *)buffer + offset);
        if (EFI_ERROR(status) || written == 0) {
            file->Close(file);
            root->Close(root);
            return EFI_ERROR(status) ? status : 1;
        }
        offset += written;
    }

    file->Close(file);
    root->Close(root);
    return EFI_SUCCESS;
}
EFI_STATUS load_cached_boot_image(EFI_HANDLE parent, const CHAR16 *file_name, EFI_HANDLE *image) {
    EFI_DEVICE_PATH_PROTOCOL *file_path = NULL;
    EFI_STATUS status = build_esp_file_device_path(parent, file_name, &file_path);
    if (EFI_ERROR(status) || !file_path) {
        return status;
    }
    status = gBS->LoadImage(0, parent, file_path, NULL, 0, image);
    gBS->FreePool(file_path);
    return status;
}
