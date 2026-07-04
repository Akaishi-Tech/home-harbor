#ifndef HOMEHARBOR_EFI_PE_H
#define HOMEHARBOR_EFI_PE_H

#include "efi.h"

UINTN detect_pe_file_size(UINT8 *buffer, UINTN partition_size);

#endif
