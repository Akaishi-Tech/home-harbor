#ifndef HOMEHARBOR_EFI_PARTITIONS_H
#define HOMEHARBOR_EFI_PARTITIONS_H

#include "efi.h"

EFI_BLOCK_IO_PROTOCOL *find_partition_by_label(const CHAR16 *label);
EFI_STATUS read_raw_partition(const CHAR16 *label, void **buffer, UINTN *size);

#endif
