#ifndef HOMEHARBOR_EFI_H
#define HOMEHARBOR_EFI_H

#include <stdint.h>
#include <stddef.h>

typedef uint8_t BOOLEAN;
typedef uint16_t CHAR16;
typedef uint64_t UINT64;
typedef int64_t INT64;
typedef uint64_t UINTN;
typedef uint32_t UINT32;
typedef uint16_t UINT16;
typedef uint8_t UINT8;
typedef uint64_t EFI_STATUS;
typedef void *EFI_HANDLE;
typedef void *EFI_EVENT;
typedef uint64_t EFI_TPL;
typedef uint64_t EFI_PHYSICAL_ADDRESS;
typedef uint64_t EFI_VIRTUAL_ADDRESS;

#define EFI_SUCCESS 0
#define EFI_ERROR(x) ((x) & 0x8000000000000000ULL)
#define EFI_FILE_MODE_READ 0x0000000000000001ULL
#define EFI_FILE_MODE_WRITE 0x0000000000000002ULL
#define EFI_FILE_MODE_CREATE 0x8000000000000000ULL
#define EFI_FILE_ARCHIVE 0x0000000000000020ULL
#define EFI_VARIABLE_NON_VOLATILE 0x00000001U
#define EFI_OPEN_PROTOCOL_BY_HANDLE_PROTOCOL 0x00000001U
#define EFI_VARIABLE_BOOTSERVICE_ACCESS 0x00000002U
#define EFI_VARIABLE_RUNTIME_ACCESS 0x00000004U
#define BY_PROTOCOL 2
#define EFI_LOADER_DATA 2
#define MEDIA_DEVICE_PATH 0x04
#define MEDIA_FILEPATH_DP 0x04
#define END_DEVICE_PATH_TYPE 0x7f
#define END_ENTIRE_DEVICE_PATH_SUBTYPE 0xff
#define BOOT_IMAGE_MAX_BYTES (512ULL * 1024ULL * 1024ULL)
#define RECOVERY_IMAGE_MAX_BYTES (2048ULL * 1024ULL * 1024ULL)
#define BOOT_CACHE_CHUNK_BYTES (4ULL * 1024ULL * 1024ULL)
#define SECURE_BOOT_WARNING_TIMEOUT_SECONDS 10U
#define VBMETA_PREFLIGHT_WARNING_TIMEOUT_SECONDS 10U
#define EFI_OS_INDICATIONS_BOOT_TO_FW_UI 0x0000000000000001ULL
#define EFI_RESET_COLD 0U
#define VBMETA_IMAGE_MAX_BYTES (16ULL * 1024ULL * 1024ULL)
#define AVB_VBMETA_HEADER_SIZE 256U
#define AVB_ALGORITHM_TYPE_SHA256_RSA2048 1U
#define AVB_ALGORITHM_TYPE_SHA256_RSA4096 2U
#define SHA256_DIGEST_BYTES 32U
#define AVB_MAX_RSA_BYTES 512U

typedef struct {
    UINT32 Data1;
    UINT16 Data2;
    UINT16 Data3;
    UINT8 Data4[8];
} EFI_GUID;

typedef struct {
    UINT8 Type;
    UINT8 SubType;
    UINT8 Length[2];
} EFI_DEVICE_PATH_PROTOCOL;

typedef struct {
    UINT64 Signature;
    UINT32 Revision;
    UINT32 HeaderSize;
    UINT32 CRC32;
    UINT32 Reserved;
} EFI_TABLE_HEADER;

typedef struct EFI_SIMPLE_TEXT_OUTPUT_PROTOCOL EFI_SIMPLE_TEXT_OUTPUT_PROTOCOL;
struct EFI_SIMPLE_TEXT_OUTPUT_PROTOCOL {
    void *Reset;
    EFI_STATUS (*OutputString)(EFI_SIMPLE_TEXT_OUTPUT_PROTOCOL *This, CHAR16 *String);
};

typedef struct {
    UINT16 ScanCode;
    CHAR16 UnicodeChar;
} EFI_INPUT_KEY;

typedef struct EFI_SIMPLE_TEXT_INPUT_PROTOCOL EFI_SIMPLE_TEXT_INPUT_PROTOCOL;
struct EFI_SIMPLE_TEXT_INPUT_PROTOCOL {
    EFI_STATUS (*Reset)(EFI_SIMPLE_TEXT_INPUT_PROTOCOL *This, BOOLEAN ExtendedVerification);
    EFI_STATUS (*ReadKeyStroke)(EFI_SIMPLE_TEXT_INPUT_PROTOCOL *This, EFI_INPUT_KEY *Key);
    EFI_EVENT WaitForKey;
};

typedef struct EFI_BOOT_SERVICES EFI_BOOT_SERVICES;
struct EFI_BOOT_SERVICES {
    EFI_TABLE_HEADER Hdr;
    EFI_TPL (*RaiseTPL)(EFI_TPL NewTpl);
    void (*RestoreTPL)(EFI_TPL OldTpl);
    EFI_STATUS (*AllocatePages)(UINT32 Type, UINT32 MemoryType, UINTN Pages, EFI_PHYSICAL_ADDRESS *Memory);
    EFI_STATUS (*FreePages)(EFI_PHYSICAL_ADDRESS Memory, UINTN Pages);
    EFI_STATUS (*GetMemoryMap)(UINTN *MemoryMapSize, void *MemoryMap, UINTN *MapKey, UINTN *DescriptorSize, UINT32 *DescriptorVersion);
    EFI_STATUS (*AllocatePool)(UINT32 PoolType, UINTN Size, void **Buffer);
    EFI_STATUS (*FreePool)(void *Buffer);
    EFI_STATUS (*CreateEvent)(UINT32 Type, EFI_TPL NotifyTpl, void *NotifyFunction, void *NotifyContext, EFI_EVENT *Event);
    EFI_STATUS (*SetTimer)(EFI_EVENT Event, UINT32 Type, UINT64 TriggerTime);
    EFI_STATUS (*WaitForEvent)(UINTN NumberOfEvents, EFI_EVENT *Event, UINTN *Index);
    EFI_STATUS (*SignalEvent)(EFI_EVENT Event);
    EFI_STATUS (*CloseEvent)(EFI_EVENT Event);
    EFI_STATUS (*CheckEvent)(EFI_EVENT Event);
    EFI_STATUS (*InstallProtocolInterface)(EFI_HANDLE *Handle, EFI_GUID *Protocol, UINT32 InterfaceType, void *Interface);
    EFI_STATUS (*ReinstallProtocolInterface)(EFI_HANDLE Handle, EFI_GUID *Protocol, void *OldInterface, void *NewInterface);
    EFI_STATUS (*UninstallProtocolInterface)(EFI_HANDLE Handle, EFI_GUID *Protocol, void *Interface);
    EFI_STATUS (*HandleProtocol)(EFI_HANDLE Handle, EFI_GUID *Protocol, void **Interface);
    void *Reserved;
    EFI_STATUS (*RegisterProtocolNotify)(EFI_GUID *Protocol, EFI_EVENT Event, void **Registration);
    EFI_STATUS (*LocateHandle)(UINT32 SearchType, EFI_GUID *Protocol, void *SearchKey, UINTN *BufferSize, EFI_HANDLE *Buffer);
    EFI_STATUS (*LocateDevicePath)(EFI_GUID *Protocol, EFI_DEVICE_PATH_PROTOCOL **DevicePath, EFI_HANDLE *Device);
    EFI_STATUS (*InstallConfigurationTable)(EFI_GUID *Guid, void *Table);
    EFI_STATUS (*LoadImage)(BOOLEAN BootPolicy, EFI_HANDLE ParentImageHandle, EFI_DEVICE_PATH_PROTOCOL *DevicePath, void *SourceBuffer, UINTN SourceSize, EFI_HANDLE *ImageHandle);
    EFI_STATUS (*StartImage)(EFI_HANDLE ImageHandle, UINTN *ExitDataSize, CHAR16 **ExitData);
    EFI_STATUS (*Exit)(EFI_HANDLE ImageHandle, EFI_STATUS ExitStatus, UINTN ExitDataSize, CHAR16 *ExitData);
    EFI_STATUS (*UnloadImage)(EFI_HANDLE ImageHandle);
    EFI_STATUS (*ExitBootServices)(EFI_HANDLE ImageHandle, UINTN MapKey);
    EFI_STATUS (*GetNextMonotonicCount)(UINT64 *Count);
    EFI_STATUS (*Stall)(UINTN Microseconds);
    EFI_STATUS (*SetWatchdogTimer)(UINTN Timeout, UINT64 WatchdogCode, UINTN DataSize, CHAR16 *WatchdogData);
    EFI_STATUS (*ConnectController)(EFI_HANDLE ControllerHandle, EFI_HANDLE *DriverImageHandle, EFI_DEVICE_PATH_PROTOCOL *RemainingDevicePath, BOOLEAN Recursive);
    EFI_STATUS (*DisconnectController)(EFI_HANDLE ControllerHandle, EFI_HANDLE DriverImageHandle, EFI_HANDLE ChildHandle);
    EFI_STATUS (*OpenProtocol)(EFI_HANDLE Handle, EFI_GUID *Protocol, void **Interface, EFI_HANDLE AgentHandle, EFI_HANDLE ControllerHandle, UINT32 Attributes);
    EFI_STATUS (*CloseProtocol)(EFI_HANDLE Handle, EFI_GUID *Protocol, EFI_HANDLE AgentHandle, EFI_HANDLE ControllerHandle);
    EFI_STATUS (*OpenProtocolInformation)(EFI_HANDLE Handle, EFI_GUID *Protocol, void *EntryBuffer, UINTN *EntryCount);
    EFI_STATUS (*ProtocolsPerHandle)(EFI_HANDLE Handle, EFI_GUID ***ProtocolBuffer, UINTN *ProtocolBufferCount);
    EFI_STATUS (*LocateHandleBuffer)(UINT32 SearchType, EFI_GUID *Protocol, void *SearchKey, UINTN *NoHandles, EFI_HANDLE **Buffer);
    EFI_STATUS (*LocateProtocol)(EFI_GUID *Protocol, void *Registration, void **Interface);
    EFI_STATUS (*InstallMultipleProtocolInterfaces)(EFI_HANDLE *Handle, ...);
    EFI_STATUS (*UninstallMultipleProtocolInterfaces)(EFI_HANDLE Handle, ...);
    EFI_STATUS (*CalculateCrc32)(void *Data, UINTN DataSize, UINT32 *Crc32);
    void (*CopyMem)(void *Destination, void *Source, UINTN Length);
    void (*SetMem)(void *Buffer, UINTN Size, UINT8 Value);
    EFI_STATUS (*CreateEventEx)(UINT32 Type, EFI_TPL NotifyTpl, void *NotifyFunction, const void *NotifyContext, EFI_GUID *EventGroup, EFI_EVENT *Event);
};

typedef struct EFI_RUNTIME_SERVICES EFI_RUNTIME_SERVICES;
struct EFI_RUNTIME_SERVICES {
    EFI_TABLE_HEADER Hdr;
    EFI_STATUS (*GetTime)(void *Time, void *Capabilities);
    EFI_STATUS (*SetTime)(void *Time);
    EFI_STATUS (*GetWakeupTime)(BOOLEAN *Enabled, BOOLEAN *Pending, void *Time);
    EFI_STATUS (*SetWakeupTime)(BOOLEAN Enable, void *Time);
    EFI_STATUS (*SetVirtualAddressMap)(UINTN MemoryMapSize, UINTN DescriptorSize, UINT32 DescriptorVersion, void *VirtualMap);
    EFI_STATUS (*ConvertPointer)(UINTN DebugDisposition, void **Address);
    EFI_STATUS (*GetVariable)(CHAR16 *VariableName, EFI_GUID *VendorGuid, UINT32 *Attributes, UINTN *DataSize, void *Data);
    EFI_STATUS (*GetNextVariableName)(UINTN *VariableNameSize, CHAR16 *VariableName, EFI_GUID *VendorGuid);
    EFI_STATUS (*SetVariable)(CHAR16 *VariableName, EFI_GUID *VendorGuid, UINT32 Attributes, UINTN DataSize, void *Data);
    EFI_STATUS (*GetNextHighMonotonicCount)(UINT32 *HighCount);
    void (*ResetSystem)(UINT32 ResetType, EFI_STATUS ResetStatus, UINTN DataSize, void *ResetData);
    EFI_STATUS (*UpdateCapsule)(void **CapsuleHeaderArray, UINTN CapsuleCount, EFI_PHYSICAL_ADDRESS ScatterGatherList);
    EFI_STATUS (*QueryCapsuleCapabilities)(void **CapsuleHeaderArray, UINTN CapsuleCount, UINT64 *MaximumCapsuleSize, UINT32 *ResetType);
    EFI_STATUS (*QueryVariableInfo)(UINT32 Attributes, UINT64 *MaximumVariableStorageSize, UINT64 *RemainingVariableStorageSize, UINT64 *MaximumVariableSize);
};

typedef struct {
    EFI_TABLE_HEADER Hdr;
    CHAR16 *FirmwareVendor;
    UINT32 FirmwareRevision;
    EFI_HANDLE ConsoleInHandle;
    EFI_SIMPLE_TEXT_INPUT_PROTOCOL *ConIn;
    EFI_HANDLE ConsoleOutHandle;
    EFI_SIMPLE_TEXT_OUTPUT_PROTOCOL *ConOut;
    EFI_HANDLE StandardErrorHandle;
    EFI_SIMPLE_TEXT_OUTPUT_PROTOCOL *StdErr;
    EFI_RUNTIME_SERVICES *RuntimeServices;
    EFI_BOOT_SERVICES *BootServices;
    UINTN NumberOfTableEntries;
    void *ConfigurationTable;
} EFI_SYSTEM_TABLE;

typedef struct {
    UINT32 Revision;
    EFI_HANDLE ParentHandle;
    EFI_SYSTEM_TABLE *SystemTable;
    EFI_HANDLE DeviceHandle;
    EFI_DEVICE_PATH_PROTOCOL *FilePath;
    void *Reserved;
    UINT32 LoadOptionsSize;
    void *LoadOptions;
    void *ImageBase;
    UINT64 ImageSize;
    UINT32 ImageCodeType;
    UINT32 ImageDataType;
    EFI_STATUS (*Unload)(EFI_HANDLE ImageHandle);
} EFI_LOADED_IMAGE_PROTOCOL;

typedef struct EFI_FILE_PROTOCOL EFI_FILE_PROTOCOL;
struct EFI_FILE_PROTOCOL {
    UINT64 Revision;
    EFI_STATUS (*Open)(EFI_FILE_PROTOCOL *This, EFI_FILE_PROTOCOL **NewHandle, CHAR16 *FileName, UINT64 OpenMode, UINT64 Attributes);
    EFI_STATUS (*Close)(EFI_FILE_PROTOCOL *This);
    EFI_STATUS (*Delete)(EFI_FILE_PROTOCOL *This);
    EFI_STATUS (*Read)(EFI_FILE_PROTOCOL *This, UINTN *BufferSize, void *Buffer);
    EFI_STATUS (*Write)(EFI_FILE_PROTOCOL *This, UINTN *BufferSize, void *Buffer);
};

typedef struct {
    UINT64 Revision;
    EFI_STATUS (*OpenVolume)(void *This, EFI_FILE_PROTOCOL **Root);
} EFI_SIMPLE_FILE_SYSTEM_PROTOCOL;

typedef struct {
    UINT32 MediaId;
    BOOLEAN RemovableMedia;
    BOOLEAN MediaPresent;
    BOOLEAN LogicalPartition;
    BOOLEAN ReadOnly;
    BOOLEAN WriteCaching;
    UINT32 BlockSize;
    UINT32 IoAlign;
    UINT64 LastBlock;
} EFI_BLOCK_IO_MEDIA;

typedef struct {
    UINT64 Revision;
    EFI_BLOCK_IO_MEDIA *Media;
    EFI_STATUS (*Reset)(void *This, BOOLEAN ExtendedVerification);
    EFI_STATUS (*ReadBlocks)(void *This, UINT32 MediaId, UINT64 Lba, UINTN BufferSize, void *Buffer);
    EFI_STATUS (*WriteBlocks)(void *This, UINT32 MediaId, UINT64 Lba, UINTN BufferSize, void *Buffer);
    EFI_STATUS (*FlushBlocks)(void *This);
} EFI_BLOCK_IO_PROTOCOL;

typedef struct {
    EFI_GUID PartitionTypeGUID;
    EFI_GUID UniquePartitionGUID;
    UINT64 StartingLBA;
    UINT64 EndingLBA;
    UINT64 Attributes;
    CHAR16 PartitionName[36];
} EFI_PARTITION_ENTRY;

typedef struct {
    UINT32 Revision;
    UINT32 Type;
    UINT8 System;
    UINT8 Reserved[7];
    union {
        UINT8 Mbr[16];
        EFI_PARTITION_ENTRY Gpt;
    } Info;
} EFI_PARTITION_INFO_PROTOCOL;

#endif
