#include "avb.h"
#include "boot_image.h"
#include "boot_state.h"
#include "efi.h"
#include "globals.h"
#include "util.h"
#include "variables.h"

EFI_STATUS efi_main(EFI_HANDLE ImageHandle, EFI_SYSTEM_TABLE *SystemTable) {
    char slot[4];
    char root_slot[4];
    char recovery_slot[4];
    char mode[16];
    const CHAR16 *label = L"boot_a";
    const CHAR16 *vbmeta_label = L"vbmeta_a";
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
        if (recovery_slot[0] == 'B' || recovery_slot[0] == 'b') {
            label = L"recovery_b";
            vbmeta_label = L"vbmeta_b";
        } else {
            label = L"recovery_a";
            vbmeta_label = L"vbmeta_a";
        }
    } else if (slot[0] == 'B' || slot[0] == 'b') {
        label = L"boot_b";
        vbmeta_label = L"vbmeta_b";
    }

    verify_vbmeta_signature_preflight(vbmeta_label, secure_boot_active);
    write_efi_boot_current(slot, root_slot, mode, recovery_slot);
    if (mode[0] == 'r') {
        return boot_recovery_erofs_partition(ImageHandle, label);
    }
    prompt_for_data_passphrase_if_needed();
    return boot_raw_partition(ImageHandle, label);
}
