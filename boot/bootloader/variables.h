#ifndef HOMEHARBOR_EFI_VARIABLES_H
#define HOMEHARBOR_EFI_VARIABLES_H

#include "efi.h"

int read_efi_boot_next(char *slot, char *root_slot, char *mode, char *recovery_slot);
EFI_STATUS write_efi_boot_current(
    char *slot,
    char *root_slot,
    char *mode,
    char *recovery_slot,
    const char *vbmeta_digest);
int read_uint8_variable(CHAR16 *name, EFI_GUID *guid, UINT8 *value);
int read_uint64_variable(CHAR16 *name, EFI_GUID *guid, UINT64 *value, UINT32 *attributes);
int secure_boot_enabled(void);
EFI_STATUS enter_firmware_settings(void);
void show_secure_boot_warning(void);
void prompt_for_data_passphrase_if_needed(void);

#endif
