#include "avb.h"
#include "boot_image.h"
#include "boot_state.h"
#include "efi.h"
#include "globals.h"
#include "util.h"
#include "variables.h"

static int valid_slot(const char *slot) {
    return slot && (slot[0] == 'A' || slot[0] == 'B') && slot[1] == 0;
}

EFI_STATUS efi_main(EFI_HANDLE ImageHandle, EFI_SYSTEM_TABLE *SystemTable) {
    char slot[4];
    char root_slot[4];
    char recovery_slot[4];
    char mode[16];
    const CHAR16 *label = L"boot_a";
    const CHAR16 *vbmeta_label = L"vbmeta_a";
    char vbmeta_digest[VBMETA_DIGEST_BUFFER_SIZE];
    EFI_STATUS status;
    int secure_boot_active;

    gST = SystemTable;
    gBS = SystemTable->BootServices;
    gRT = SystemTable->RuntimeServices;
    gBS->SetWatchdogTimer(0, 0, 0, NULL);

    secure_boot_active = secure_boot_enabled();
    show_secure_boot_warning();

    print(L"HomeHarborBoot: selecting boot slot\r\n");
    read_boot_state(ImageHandle, slot, root_slot, mode, recovery_slot);
    if (read_efi_boot_next(slot, root_slot, mode, recovery_slot)) {
        print(L"HomeHarborBoot: consumed EFI boot-next variable\r\n");
    }

    if (mode[0] == 'r') {
        if (!valid_slot(recovery_slot)) {
            print(L"HomeHarborBoot: refusing invalid recovery slot\r\n");
            return EFI_SECURITY_VIOLATION;
        }
        if (recovery_slot[0] == 'B') {
            label = L"boot_b";
            vbmeta_label = L"vbmeta_b";
        } else {
            label = L"boot_a";
            vbmeta_label = L"vbmeta_a";
        }
    } else {
        if (!valid_slot(slot) || !valid_slot(root_slot)) {
            print(L"HomeHarborBoot: refusing invalid normal boot slot\r\n");
            return EFI_SECURITY_VIOLATION;
        }
        if (slot[0] == 'B') {
            label = L"boot_b";
        }
        if (root_slot[0] == 'B') {
            vbmeta_label = L"vbmeta_b";
        }
    }

    if (!verify_vbmeta_signature_preflight(vbmeta_label, secure_boot_active, vbmeta_digest)) {
        return EFI_SECURITY_VIOLATION;
    }
    status = write_efi_boot_current(slot, root_slot, mode, recovery_slot, vbmeta_digest);
    if (status != EFI_SUCCESS) {
        print(L"HomeHarborBoot: refusing to boot without a fresh vbmeta digest handoff\r\n");
        return EFI_ERROR(status) ? status : EFI_SECURITY_VIOLATION;
    }
    if (mode[0] != 'r') {
        prompt_for_data_passphrase_if_needed();
    }
    return boot_raw_partition(ImageHandle, label);
}
