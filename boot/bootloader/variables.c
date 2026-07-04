#include "console.h"
#include "globals.h"
#include "util.h"
#include "variables.h"

int read_efi_boot_next(char *slot, char *root_slot, char *mode, char *recovery_slot) {
    UINT8 buffer[64];
    UINTN size = sizeof(buffer) - 1;
    UINT32 attributes = 0;
    EFI_STATUS status;
    char requested_slot;
    char requested_root_slot;

    if (!gRT || !gRT->GetVariable || !gRT->SetVariable) {
        return 0;
    }

    status = gRT->GetVariable(L"HomeHarborBootNext", &HomeHarborBootVariableGuid, &attributes, &size, buffer);
    if (EFI_ERROR(status) || size == 0) {
        return 0;
    }
    if (size >= sizeof(buffer)) {
        size = sizeof(buffer) - 1;
    }
    buffer[size] = 0;
    gRT->SetVariable(L"HomeHarborBootNext", &HomeHarborBootVariableGuid, 0, 0, NULL);

    if (ascii_starts_with((char *)buffer, "recovery")) {
        requested_slot = payload_component_slot((char *)buffer, 1);
        ascii_copy(mode, 16, "recovery");
        if (requested_slot) {
            recovery_slot[0] = requested_slot;
            recovery_slot[1] = 0;
        }
        return 1;
    }

    if (ascii_starts_with((char *)buffer, "normal")) {
        requested_slot = payload_component_slot((char *)buffer, 1);
        requested_root_slot = payload_component_slot((char *)buffer, 2);
        mode[0] = 0;
        if (requested_slot) {
            slot[0] = requested_slot;
            slot[1] = 0;
            root_slot[0] = requested_root_slot ? requested_root_slot : requested_slot;
            root_slot[1] = 0;
            return 1;
        }
    }

    return 0;
}

EFI_STATUS write_efi_boot_current(char *slot, char *root_slot, char *mode, char *recovery_slot) {
    char payload[32];
    UINTN i = 0;
    UINTN size;

    if (!gRT || !gRT->SetVariable) {
        return 1;
    }

    if (mode[0] == 'r') {
        const char *prefix = "recovery:";
        while (prefix[i]) {
            payload[i] = prefix[i];
            i++;
        }
        payload[i++] = recovery_slot[0];
    } else {
        const char *prefix = "normal:";
        while (prefix[i]) {
            payload[i] = prefix[i];
            i++;
        }
        payload[i++] = slot[0];
        payload[i++] = ':';
        payload[i++] = root_slot[0];
    }
    payload[i] = 0;
    size = ascii_length(payload);

    return gRT->SetVariable(L"HomeHarborBootCurrent",
        &HomeHarborBootVariableGuid,
        EFI_VARIABLE_BOOTSERVICE_ACCESS | EFI_VARIABLE_RUNTIME_ACCESS,
        size,
        payload);
}

int read_uint8_variable(CHAR16 *name, EFI_GUID *guid, UINT8 *value) {
    UINTN size = sizeof(UINT8);
    UINT32 attributes = 0;
    EFI_STATUS status;

    if (!gRT || !gRT->GetVariable || !value) {
        return 0;
    }

    status = gRT->GetVariable(name, guid, &attributes, &size, value);
    return !EFI_ERROR(status) && size == sizeof(UINT8);
}

static int read_ascii_variable(CHAR16 *name, EFI_GUID *guid, char *buffer, UINTN buffer_length) {
    UINTN size;
    UINT32 attributes = 0;
    EFI_STATUS status;

    if (!gRT || !gRT->GetVariable || !buffer || buffer_length == 0) {
        return 0;
    }

    size = buffer_length - 1;
    status = gRT->GetVariable(name, guid, &attributes, &size, buffer);
    if (EFI_ERROR(status)) {
        buffer[0] = 0;
        return 0;
    }
    if (size >= buffer_length) {
        size = buffer_length - 1;
    }
    buffer[size] = 0;
    return size > 0;
}

static void clear_data_passphrase_variable(void) {
    if (!gRT || !gRT->SetVariable) {
        return;
    }

    gRT->SetVariable(
        L"HomeHarborDataPassphrase",
        &HomeHarborBootVariableGuid,
        EFI_VARIABLE_BOOTSERVICE_ACCESS | EFI_VARIABLE_RUNTIME_ACCESS,
        0,
        NULL);
    gRT->SetVariable(
        L"HomeHarborDataPassphrase",
        &HomeHarborBootVariableGuid,
        EFI_VARIABLE_NON_VOLATILE | EFI_VARIABLE_BOOTSERVICE_ACCESS | EFI_VARIABLE_RUNTIME_ACCESS,
        0,
        NULL);
}

static EFI_STATUS write_data_passphrase_variable(char *passphrase) {
    UINTN size;

    if (!gRT || !gRT->SetVariable || !passphrase) {
        return 1;
    }

    size = ascii_length(passphrase);
    if (size == 0) {
        return 1;
    }

    return gRT->SetVariable(
        L"HomeHarborDataPassphrase",
        &HomeHarborBootVariableGuid,
        EFI_VARIABLE_NON_VOLATILE | EFI_VARIABLE_BOOTSERVICE_ACCESS | EFI_VARIABLE_RUNTIME_ACCESS,
        size,
        passphrase);
}

static int read_secret_line(char *buffer, UINTN buffer_length) {
    UINTN length = 0;
    CHAR16 key;

    if (!buffer || buffer_length == 0) {
        return 0;
    }
    buffer[0] = 0;
    while (1) {
        if (!wait_console_key(&key)) {
            return 0;
        }
        if (key == L'\r' || key == L'\n') {
            print(L"\r\n");
            buffer[length] = 0;
            return length > 0;
        }
        if (key == 8 || key == 127) {
            if (length > 0) {
                length--;
                buffer[length] = 0;
                print(L"\b \b");
            }
            continue;
        }
        if (key >= 32 && key <= 126 && length + 1 < buffer_length) {
            buffer[length++] = (char)key;
            buffer[length] = 0;
            print(L"*");
        }
    }
}

void prompt_for_data_passphrase_if_needed(void) {
    char mode[32];
    char passphrase[512];

    if (!read_ascii_variable(L"HomeHarborDataUnlockMode", &HomeHarborBootVariableGuid, mode, sizeof(mode))) {
        return;
    }
    if (!ascii_starts_with(mode, "passphrase")) {
        return;
    }

    clear_data_passphrase_variable();
    reset_console_input();
    print(L"\r\nHomeHarbor data passphrase: ");
    if (!read_secret_line(passphrase, sizeof(passphrase))) {
        print(L"HomeHarborBoot: empty data passphrase; continuing without early unlock secret.\r\n");
        return;
    }
    if (write_data_passphrase_variable(passphrase) != EFI_SUCCESS) {
        print(L"HomeHarborBoot: failed to pass data unlock secret to initramfs.\r\n");
    }
    zero_bytes(passphrase, sizeof(passphrase));
}

int read_uint64_variable(CHAR16 *name, EFI_GUID *guid, UINT64 *value, UINT32 *attributes) {
    UINTN size = sizeof(UINT64);
    UINT32 local_attributes = 0;
    EFI_STATUS status;

    if (!gRT || !gRT->GetVariable || !value) {
        return 0;
    }

    status = gRT->GetVariable(name, guid, attributes ? attributes : &local_attributes, &size, value);
    return !EFI_ERROR(status) && size == sizeof(UINT64);
}

int secure_boot_enabled(void) {
    UINT8 secure_boot = 0;

    if (!read_uint8_variable(L"SecureBoot", &EfiGlobalVariableGuid, &secure_boot)) {
        return 0;
    }

    return secure_boot == 1;
}
static int secure_boot_warning_disabled(void) {
    UINT8 disabled = 0;

    if (!read_uint8_variable(L"HomeHarborSecureBootWarningDisabled", &HomeHarborBootVariableGuid, &disabled)) {
        return 0;
    }

    return disabled == 1;
}

static EFI_STATUS disable_secure_boot_warning(void) {
    UINT8 disabled = 1;

    if (!gRT || !gRT->SetVariable) {
        return 1;
    }

    return gRT->SetVariable(
        L"HomeHarborSecureBootWarningDisabled",
        &HomeHarborBootVariableGuid,
        EFI_VARIABLE_NON_VOLATILE | EFI_VARIABLE_BOOTSERVICE_ACCESS | EFI_VARIABLE_RUNTIME_ACCESS,
        sizeof(disabled),
        &disabled);
}
static CHAR16 read_secure_boot_warning_choice(void) {
    UINTN ticks;
    CHAR16 key;

    for (ticks = 0; ticks < SECURE_BOOT_WARNING_TIMEOUT_SECONDS * 10U; ticks++) {
        if (read_console_key(&key)) {
            if (key == L'1' || key == L'\r') {
                return L'1';
            }
            if (key == L'2') {
                return L'2';
            }
            if (key == L'3') {
                return L'3';
            }
        }
        if (!gBS || !gBS->Stall) {
            return L'1';
        }
        gBS->Stall(100000);
    }

    return L'1';
}
EFI_STATUS enter_firmware_settings(void) {
    UINT64 supported = 0;
    UINT64 indications = 0;
    UINT32 attributes = EFI_VARIABLE_NON_VOLATILE | EFI_VARIABLE_BOOTSERVICE_ACCESS | EFI_VARIABLE_RUNTIME_ACCESS;
    EFI_STATUS status;

    if (!gRT || !gRT->GetVariable || !gRT->SetVariable || !gRT->ResetSystem) {
        return 1;
    }

    if (!read_uint64_variable(L"OsIndicationsSupported", &EfiGlobalVariableGuid, &supported, NULL) ||
        (supported & EFI_OS_INDICATIONS_BOOT_TO_FW_UI) == 0) {
        return 1;
    }

    read_uint64_variable(L"OsIndications", &EfiGlobalVariableGuid, &indications, &attributes);
    indications |= EFI_OS_INDICATIONS_BOOT_TO_FW_UI;
    status = gRT->SetVariable(
        L"OsIndications",
        &EfiGlobalVariableGuid,
        attributes,
        sizeof(indications),
        &indications);
    if (EFI_ERROR(status)) {
        return status;
    }

    print(L"HomeHarborBoot: rebooting to firmware settings...\r\n");
    if (gBS && gBS->Stall) {
        gBS->Stall(1000000);
    }
    gRT->ResetSystem(EFI_RESET_COLD, EFI_SUCCESS, 0, NULL);
    return 1;
}
void show_secure_boot_warning(void) {
    CHAR16 choice;

    if (secure_boot_enabled() || secure_boot_warning_disabled()) {
        return;
    }

    drain_console_input();
    print(L"\r\nWARNING: Secure Boot is not enabled.\r\n");
    print(L"HomeHarbor boot integrity protections are reduced.\r\n\r\n");
    print(L"1. Continue this time (10s timeout)\r\n");
    print(L"2. Do not show this again\r\n");
    print(L"3. Enter Firmware settings to config\r\n\r\n");
    print(L"Select 1, 2, or 3: ");

    choice = read_secure_boot_warning_choice();
    print(L"\r\n");

    if (choice == L'2') {
        if (disable_secure_boot_warning() != EFI_SUCCESS) {
            print(L"HomeHarborBoot: could not save Secure Boot warning preference; continuing.\r\n");
        } else {
            print(L"HomeHarborBoot: Secure Boot warning disabled; continuing.\r\n");
        }
        return;
    }

    if (choice == L'3') {
        if (enter_firmware_settings() != EFI_SUCCESS) {
            print(L"HomeHarborBoot: firmware settings handoff is not supported; continuing.\r\n");
            if (gBS && gBS->Stall) {
                gBS->Stall(3000000);
            }
        }
        return;
    }

    print(L"HomeHarborBoot: continuing with Secure Boot disabled.\r\n");
}
