ROOT := $(abspath ..)

EFI_OUTPUT ?= $(ROOT)/artifacts/HomeHarborBoot.efi
AVB_OUTPUT ?= $(ROOT)/artifacts/homeharbor-avb
INIT_OUTPUT ?= $(ROOT)/artifacts/homeharbor-verity

.PHONY: all bootloader avb init assets clean

all: bootloader avb init assets

bootloader:
	$(MAKE) -C bootloader OUTPUT="$(EFI_OUTPUT)" all

avb:
	$(MAKE) -C avb OUTPUT="$(AVB_OUTPUT)" all

init:
	$(MAKE) -C init OUTPUT="$(INIT_OUTPUT)" all

assets:
	$(MAKE) -C assets all

clean:
	$(MAKE) -C bootloader clean
	$(MAKE) -C avb clean
	$(MAKE) -C init clean
	$(MAKE) -C assets clean
