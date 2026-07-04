ROOT := $(abspath ../..)
OUTPUT ?= $(ROOT)/artifacts/homeharbor-verity
BUILD_DIR ?= $(ROOT)/.work/init-helper

CC ?= cc
CFLAGS ?= -O2 -Wall -Wextra

ifeq ($(HOMEHARBOR_DEBUG_INITRAMFS_SHELL),1)
ifneq ($(or $(HOMEHARBOR_CHANNEL),dev),dev)
$(error HOMEHARBOR_DEBUG_INITRAMFS_SHELL=1 is only allowed with HOMEHARBOR_CHANNEL=dev)
endif
CFLAGS += -DHOMEHARBOR_ENABLE_INITRAMFS_EMERGENCY_SHELL=1
endif

SOURCES := homeharbor-verity.c

.PHONY: all clean

all: $(OUTPUT)

$(OUTPUT): $(SOURCES)
	@mkdir -p "$(dir $@)" "$(BUILD_DIR)"
	$(CC) $(CFLAGS) -o "$@" $(SOURCES)
	@echo "Built $@"

clean:
	rm -rf "$(BUILD_DIR)"
	rm -f "$(OUTPUT)"
