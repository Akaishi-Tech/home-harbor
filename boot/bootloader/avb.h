#ifndef HOMEHARBOR_EFI_AVB_H
#define HOMEHARBOR_EFI_AVB_H

#include "efi.h"

void verify_vbmeta_signature_preflight(const CHAR16 *vbmeta_label, int secure_boot_active);

#endif
