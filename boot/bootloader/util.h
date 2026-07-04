#ifndef HOMEHARBOR_EFI_UTIL_H
#define HOMEHARBOR_EFI_UTIL_H

#include "efi.h"

void print(CHAR16 *message);
int char16_equal(CHAR16 *a, const CHAR16 *b);
int find_json_string(char *json, UINTN length, const char *key, char *output, UINTN output_length);
int ascii_starts_with(char *text, const char *prefix);
void ascii_copy(char *destination, UINTN destination_length, const char *source);
UINTN ascii_length(char *text);
char payload_component_slot(char *payload, UINTN component_index);
UINTN trim_trailing_zeroes(UINT8 *buffer, UINTN size);
UINT16 read_u16(UINT8 *buffer, UINTN offset);
UINT32 read_u32(UINT8 *buffer, UINTN offset);
UINT64 read_u64(UINT8 *buffer, UINTN offset);
UINT32 read_be32(const UINT8 *buffer, UINTN offset);
UINT64 read_be64(const UINT8 *buffer, UINTN offset);
UINTN max_un(UINTN a, UINTN b);
UINTN char16_length(const CHAR16 *text);
void copy_bytes(void *destination, const void *source, UINTN length);
void zero_bytes(void *buffer, UINTN length);
int bytes_equal(const UINT8 *a, const UINT8 *b, UINTN length);

#endif
