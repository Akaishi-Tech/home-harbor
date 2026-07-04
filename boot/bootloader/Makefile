ROOT := $(abspath ../..)
OUTPUT ?= $(ROOT)/artifacts/HomeHarborBoot.efi
BUILD_DIR ?= $(ROOT)/.work/efi-loader

CLANG ?= clang

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

.PHONY: all clean

all: $(OUTPUT)

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

$(AVB_PUBLIC_KEY_HEADER): $(ROOT)/src/HomeHarbor.ImageBuilder/Program.cs $(ROOT)/src/HomeHarbor.Tooling/BuildToolCommands.cs
	@mkdir -p "$(dir $@)"
	dotnet run --project "$(ROOT)/src/HomeHarbor.ImageBuilder/HomeHarbor.ImageBuilder.csproj" -- generate-efi-avb-public-key "$@" "$(ROOT)"

clean:
	rm -rf "$(BUILD_DIR)"
