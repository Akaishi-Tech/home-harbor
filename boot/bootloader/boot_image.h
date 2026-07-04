#ifndef HOMEHARBOR_EFI_BOOT_IMAGE_H
#define HOMEHARBOR_EFI_BOOT_IMAGE_H

#include "efi.h"

EFI_STATUS boot_raw_partition(EFI_HANDLE parent, const CHAR16 *label);
EFI_STATUS boot_recovery_erofs_partition(EFI_HANDLE parent, const CHAR16 *label);

#endif
