#ifndef HOMEHARBOR_EFI_GLOBALS_H
#define HOMEHARBOR_EFI_GLOBALS_H

#include "efi.h"

extern EFI_GUID LoadedImageProtocolGuid;
extern EFI_GUID DevicePathProtocolGuid;
extern EFI_GUID SimpleFileSystemGuid;
extern EFI_GUID BlockIoGuid;
extern EFI_GUID PartitionInfoGuid;
extern EFI_GUID HomeHarborBootVariableGuid;
extern EFI_GUID EfiGlobalVariableGuid;

extern EFI_SYSTEM_TABLE *gST;
extern EFI_BOOT_SERVICES *gBS;
extern EFI_RUNTIME_SERVICES *gRT;

#endif
