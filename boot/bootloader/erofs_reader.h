#ifndef HOMEHARBOR_EFI_EROFS_READER_H
#define HOMEHARBOR_EFI_EROFS_READER_H

#include "efi.h"

typedef struct {
    void *Context;
    UINT64 ImageSize;
    EFI_STATUS (*Read)(void *context, UINT64 offset, UINTN size, void *buffer);
    EFI_STATUS (*Allocate)(UINTN size, void **buffer);
    void (*Free)(void *buffer);
} HOMEHARBOR_EROFS_READER;

EFI_STATUS erofs_read_file(HOMEHARBOR_EROFS_READER *reader, const char *path, void **buffer, UINTN *size);

#endif
