ROOT := $(abspath ..)

EFI_OUTPUT ?= $(ROOT)/artifacts/HomeHarborBoot.efi
AVB_OUTPUT ?= $(ROOT)/artifacts/homeharbor-avb
INIT_OUTPUT ?= $(ROOT)/artifacts/homeharbor-verity

.PHONY: all bootloader avb init assets clean

all: bootloader avb init assets

bootloader:
	$(MAKE) -C "$(ROOT)" efi-loader EFI_OUTPUT="$(EFI_OUTPUT)"

avb:
	$(MAKE) -C "$(ROOT)" avb-helper AVB_OUTPUT="$(AVB_OUTPUT)"

init:
	$(MAKE) -C "$(ROOT)" init-helper INIT_OUTPUT="$(INIT_OUTPUT)"

assets:
	$(MAKE) -C assets all

clean:
	$(MAKE) -C ../tools/system-build/external/system-utils/boot/bootloader clean
	$(MAKE) -C ../tools/system-build/external/system-utils/boot/avb clean
	$(MAKE) -C ../tools/system-build/external/system-utils/boot/init clean
	$(MAKE) -C assets clean
