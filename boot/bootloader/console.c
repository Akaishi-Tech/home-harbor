#include "console.h"
#include "globals.h"

void drain_console_input(void) {
    EFI_INPUT_KEY key;

    if (!gST || !gST->ConIn || !gST->ConIn->ReadKeyStroke) {
        return;
    }

    while (!EFI_ERROR(gST->ConIn->ReadKeyStroke(gST->ConIn, &key))) {
    }
}

void reset_console_input(void) {
    if (gST && gST->ConIn && gST->ConIn->Reset) {
        gST->ConIn->Reset(gST->ConIn, 0);
    }
    drain_console_input();
}

int read_console_key(CHAR16 *key) {
    EFI_INPUT_KEY input_key;
    EFI_STATUS status;

    if (!gST || !gST->ConIn || !gST->ConIn->ReadKeyStroke || !key) {
        return 0;
    }

    status = gST->ConIn->ReadKeyStroke(gST->ConIn, &input_key);
    if (EFI_ERROR(status) || input_key.UnicodeChar == 0) {
        return 0;
    }

    *key = input_key.UnicodeChar;
    return 1;
}

int wait_console_key(CHAR16 *key) {
    EFI_EVENT wait_event;
    UINTN index = 0;
    EFI_STATUS status;

    if (!gST || !gST->ConIn || !gST->ConIn->WaitForKey || !gBS || !gBS->WaitForEvent || !key) {
        return read_console_key(key);
    }

    while (1) {
        wait_event = gST->ConIn->WaitForKey;
        status = gBS->WaitForEvent(1, &wait_event, &index);
        if (EFI_ERROR(status)) {
            return 0;
        }
        if (read_console_key(key)) {
            return 1;
        }
    }
}
