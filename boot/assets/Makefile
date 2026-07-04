ROOT := $(abspath ../..)

ASSETS := \
	homeharbor-syslinux-splash.png \
	homeharbor-syslinux-splash.svg

.PHONY: all check clean

all: check

check:
	@for asset in $(ASSETS); do \
		test -f "$$asset" || { echo "missing boot asset: $$asset" >&2; exit 1; }; \
	done

clean:
	@:
