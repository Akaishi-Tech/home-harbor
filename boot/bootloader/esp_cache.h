#ifndef HOMEHARBOR_EFI_ESP_CACHE_H
#define HOMEHARBOR_EFI_ESP_CACHE_H

#include "efi.h"

EFI_STATUS write_cached_boot_image(EFI_HANDLE parent, const CHAR16 *file_name, void *buffer, UINTN size);
EFI_STATUS load_cached_boot_image(EFI_HANDLE parent, const CHAR16 *file_name, EFI_HANDLE *image);

#endif
