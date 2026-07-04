ROOT := $(abspath ../..)
OUTPUT ?= $(ROOT)/artifacts/homeharbor-avb
BUILD_DIR ?= $(ROOT)/.work/avb-helper

CC ?= cc
CFLAGS ?= -O2 -Wall -Wextra
LDLIBS ?= -lcrypto

SOURCES := homeharbor-avb.c

.PHONY: all clean

all: $(OUTPUT)

$(OUTPUT): $(SOURCES)
	@mkdir -p "$(dir $@)" "$(BUILD_DIR)"
	$(CC) $(CFLAGS) -o "$@" $(SOURCES) $(LDLIBS)
	@echo "Built $@"

clean:
	rm -rf "$(BUILD_DIR)"
	rm -f "$(OUTPUT)"
