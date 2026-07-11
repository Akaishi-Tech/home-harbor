#include <stdio.h>
#include <string.h>

#include "globals.h"
#include "variables.h"

static EFI_RUNTIME_SERVICES runtime_services;
static char captured_payload[128];
static UINTN captured_size;
static UINT32 captured_attributes;
static int set_calls;
static EFI_STATUS set_status;

static EFI_STATUS capture_set_variable(
    CHAR16 *name,
    EFI_GUID *guid,
    UINT32 attributes,
    UINTN size,
    void *data) {
    (void)guid;
    if (!name || name[0] != L'H' || size >= sizeof(captured_payload)) {
        return 1;
    }
    memcpy(captured_payload, data, (size_t)size);
    captured_payload[size] = '\0';
    captured_size = size;
    captured_attributes = attributes;
    set_calls++;
    return set_status;
}

static int expect_payload(const char *expected) {
    if (strcmp(captured_payload, expected) != 0 || captured_size != strlen(expected)) {
        fprintf(stderr, "unexpected HomeHarborBootCurrent payload: %s\n", captured_payload);
        return 0;
    }
    if (captured_attributes != (EFI_VARIABLE_BOOTSERVICE_ACCESS | EFI_VARIABLE_RUNTIME_ACCESS)) {
        fprintf(stderr, "unexpected HomeHarborBootCurrent attributes\n");
        return 0;
    }
    return 1;
}

int main(void) {
    const char *digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    char slot[] = "A";
    char root_slot[] = "B";
    char recovery_slot[] = "B";
    char normal[] = "normal";
    char recovery[] = "recovery";
    char bad_digest[] = "0123";

    memset(&runtime_services, 0, sizeof(runtime_services));
    runtime_services.SetVariable = capture_set_variable;
    gRT = &runtime_services;
    set_status = EFI_SUCCESS;

    if (write_efi_boot_current(slot, root_slot, normal, recovery_slot, digest) != EFI_SUCCESS ||
        !expect_payload("normal:A:B:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")) {
        return 1;
    }
    if (write_efi_boot_current(slot, root_slot, recovery, recovery_slot, digest) != EFI_SUCCESS ||
        !expect_payload("recovery:B:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")) {
        return 1;
    }

    set_calls = 0;
    if (write_efi_boot_current(slot, root_slot, normal, recovery_slot, bad_digest) == EFI_SUCCESS || set_calls != 0) {
        fprintf(stderr, "invalid vbmeta digest was accepted\n");
        return 1;
    }

    set_status = EFI_SECURITY_VIOLATION;
    if (write_efi_boot_current(slot, root_slot, normal, recovery_slot, digest) != EFI_SECURITY_VIOLATION) {
        fprintf(stderr, "EFI variable write failure was not propagated\n");
        return 1;
    }
    return 0;
}
