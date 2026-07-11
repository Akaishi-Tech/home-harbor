ROOT := $(abspath ../..)
OUTPUT ?= $(ROOT)/artifacts/HomeHarborBoot.efi
BUILD_DIR ?= $(ROOT)/.work/efi-loader

CLANG ?= clang
HOST_CC ?= cc

SOURCES := \
	HomeHarborBoot.c \
	avb.c \
	boot_image.c \
	boot_state.c \
	console.c \
	erofs_reader.c \
	esp_cache.c \
	globals.c \
	partitions.c \
	pe.c \
	util.c \
	variables.c

HEADERS := $(wildcard *.h)
OBJECTS := $(SOURCES:%.c=$(BUILD_DIR)/%.obj)
AVB_PUBLIC_KEY_HEADER := $(BUILD_DIR)/homeharbor-avb-public-key.h

CFLAGS := \
	--target=x86_64-unknown-windows \
	-ffreestanding \
	-fshort-wchar \
	-fno-builtin \
	-fno-stack-protector \
	-mno-red-zone \
	-nostdlib \
	-I$(CURDIR)

LDFLAGS := \
	--target=x86_64-unknown-windows \
	-fuse-ld=lld-link \
	-nostdlib \
	-Wl,/entry:efi_main \
	-Wl,/subsystem:efi_application \
	-Wl,/nodefaultlib

.PHONY: all clean test FORCE

all: $(OUTPUT)

test: $(BUILD_DIR)/test_boot_current
	$(BUILD_DIR)/test_boot_current

$(BUILD_DIR)/test_boot_current: test_boot_current.c variables.c variables.h globals.c globals.h efi.h
	@mkdir -p "$(BUILD_DIR)"
	$(HOST_CC) -std=c11 -O2 -Wall -Wextra -fshort-wchar -ffunction-sections -fdata-sections -Wl,--gc-sections -I"$(CURDIR)" \
		test_boot_current.c variables.c globals.c -o "$@"

$(OUTPUT): $(OBJECTS)
	@mkdir -p "$(dir $@)"
	$(CLANG) $(LDFLAGS) -o "$@" $(OBJECTS)
	@echo "Built $@"

$(BUILD_DIR)/%.obj: %.c $(HEADERS)
	@mkdir -p "$(BUILD_DIR)"
	$(CLANG) $(CFLAGS) -c "$<" -o "$@"

$(BUILD_DIR)/avb.obj: avb.c $(HEADERS) $(AVB_PUBLIC_KEY_HEADER)
	@mkdir -p "$(BUILD_DIR)"
	$(CLANG) $(CFLAGS) -include "$(AVB_PUBLIC_KEY_HEADER)" -c "$<" -o "$@"

$(AVB_PUBLIC_KEY_HEADER): FORCE $(ROOT)/src/HomeHarbor.ImageBuilder/Program.cs $(ROOT)/src/HomeHarbor.Tooling/BuildToolCommands.cs
	@mkdir -p "$(dir $@)"
	@set -eu; \
		tmp="$@.tmp.$$$$"; \
		trap 'rm -f "$$tmp"' EXIT; \
		dotnet run --project "$(ROOT)/src/HomeHarbor.ImageBuilder/HomeHarbor.ImageBuilder.csproj" -- generate-efi-avb-public-key "$$tmp" "$(ROOT)"; \
		if ! cmp -s "$$tmp" "$@"; then mv -f "$$tmp" "$@"; else rm -f "$$tmp"; fi; \
		trap - EXIT

FORCE:

clean:
	rm -rf "$(BUILD_DIR)"
