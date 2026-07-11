#ifndef HOMEHARBOR_EFI_AVB_H
#define HOMEHARBOR_EFI_AVB_H

#include "efi.h"

#define VBMETA_DIGEST_HEX_LENGTH 64U
#define VBMETA_DIGEST_BUFFER_SIZE (VBMETA_DIGEST_HEX_LENGTH + 1U)

int verify_vbmeta_signature_preflight(
    const CHAR16 *vbmeta_label,
    int secure_boot_active,
    char vbmeta_digest[VBMETA_DIGEST_BUFFER_SIZE]);

#endif
