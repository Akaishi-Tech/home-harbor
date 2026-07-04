#ifndef HOMEHARBOR_EFI_BOOT_STATE_H
#define HOMEHARBOR_EFI_BOOT_STATE_H

#include "efi.h"

EFI_STATUS read_boot_state(EFI_HANDLE image, char *slot, char *root_slot, char *mode, char *recovery_slot);

#endif
