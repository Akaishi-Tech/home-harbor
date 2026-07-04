#ifndef HOMEHARBOR_EFI_CONSOLE_H
#define HOMEHARBOR_EFI_CONSOLE_H

#include "efi.h"

void drain_console_input(void);
void reset_console_input(void);
int read_console_key(CHAR16 *key);
int wait_console_key(CHAR16 *key);

#endif
