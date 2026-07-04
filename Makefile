.DEFAULT_GOAL := help

DOTNET ?= dotnet
PNPM ?= pnpm
DOTNET_RESTORE_FLAGS ?=
DOTNET_BUILD_FLAGS ?=
DOTNET_TEST_FLAGS ?=
ARCH ?= x86_64
VERSION ?= 0.1.0-dev
MANIFEST ?= system/$(ARCH)/system/manifest.yml
EFI_OUTPUT ?= artifacts/HomeHarborBoot.efi
AVB_OUTPUT ?= artifacts/homeharbor-avb
INIT_OUTPUT ?= artifacts/homeharbor-verity
BOOT_HELPER_TARGETS ?= efi-loader avb-helper init-helper

ROOT := $(abspath .)
SOLUTION := HomeHarbor.slnx
API_PROJECT := src/HomeHarbor.Api/HomeHarbor.Api.csproj
IMAGE_BUILDER_PROJECT := src/HomeHarbor.ImageBuilder/HomeHarbor.ImageBuilder.csproj
UNIT_TEST_PROJECT := tests/HomeHarbor.Tests/HomeHarbor.Tests.csproj
IMAGE_BUILDER := $(DOTNET) run --project $(IMAGE_BUILDER_PROJECT) --

.PHONY: help setup restore dev api-dev frontend-dev docs-dev \
	backend-restore backend-build backend-ci-build backend-lint lint test-unit backend-ci-test test \
	frontend-typecheck frontend-build frontend-preview \
	docs-build docs-preview docs-deploy ui-build build check \
	image-plan system-plan image-plan-check system-plan-check \
	efi-loader avb-helper init-helper appliance-build system-build iso-build arch-package \
	clean

help:
	@printf '%s\n' 'HomeHarbor root Makefile'
	@printf '%s\n' ''
	@printf '%s\n' 'Developer targets:'
	@printf '%s\n' '  make setup              Install JS workspace deps and restore .NET packages'
	@printf '%s\n' '  make restore            Restore .NET and pnpm dependencies'
	@printf '%s\n' '  make dev                Run API and frontend dev servers'
	@printf '%s\n' '  make api-dev            Run the ASP.NET Core API'
	@printf '%s\n' '  make frontend-dev       Run the Vite frontend dev server'
	@printf '%s\n' '  make docs-dev           Run the VitePress docs dev server'
	@printf '%s\n' ''
	@printf '%s\n' 'Build and check targets:'
	@printf '%s\n' '  make build              Build backend, UI, docs, appliance plans, EFI, and AVB helper'
	@printf '%s\n' '  make check              Run build plus unit tests and frontend typecheck'
	@printf '%s\n' '  make backend-restore    Restore .NET packages'
	@printf '%s\n' '  make backend-build      Build the .NET solution'
	@printf '%s\n' '  make backend-lint       Verify C# whitespace, style, and analyzer rules'
	@printf '%s\n' '  make lint               Run C# lint checks'
	@printf '%s\n' '  make test-unit          Run local MSTest unit tests'
	@printf '%s\n' '  make frontend-build     Build frontend release assets'
	@printf '%s\n' '  make docs-build         Build the docs site'
	@printf '%s\n' '  make ui-build           Build frontend and docs via pnpm'
	@printf '%s\n' ''
	@printf '%s\n' 'Appliance targets:'
	@printf '%s\n' '  make image-plan         Print the image plan for ARCH/MANIFEST/VERSION'
	@printf '%s\n' '  make system-plan        Print the system plan for ARCH/MANIFEST/VERSION'
	@printf '%s\n' '  make efi-loader         Build EFI loader to EFI_OUTPUT'
	@printf '%s\n' '  make avb-helper         Build AVB helper to AVB_OUTPUT'
	@printf '%s\n' '  make init-helper        Build initramfs helper to INIT_OUTPUT'
	@printf '%s\n' '  make appliance-build    Run appliance plan checks and build boot helpers'
	@printf '%s\n' '  make system-build       Build a full system image explicitly'
	@printf '%s\n' '  make iso-build          Build OTA bundles, channel metadata, and full installer ISO'
	@printf '%s\n' '  make arch-package       Build Arch package artifacts explicitly'
	@printf '%s\n' ''
	@printf '%s\n' 'Useful overrides:'
	@printf '%s\n' '  ARCH=x86_64 VERSION=0.1.0-dev MANIFEST=system/$$(ARCH)/system/manifest.yml'
	@printf '%s\n' '  EFI_OUTPUT=artifacts/HomeHarborBoot.efi AVB_OUTPUT=artifacts/homeharbor-avb INIT_OUTPUT=artifacts/homeharbor-verity'
	@printf '%s\n' '  BOOT_HELPER_TARGETS="efi-loader avb-helper init-helper"'

setup: restore

restore: backend-restore
	$(PNPM) install

backend-restore:
	$(DOTNET) restore $(SOLUTION) $(DOTNET_RESTORE_FLAGS)

dev:
	$(MAKE) -j2 api-dev frontend-dev

api-dev:
	$(DOTNET) run --project $(API_PROJECT)

frontend-dev:
	$(PNPM) frontend:dev

docs-dev:
	$(PNPM) docs:dev

backend-build:
	$(DOTNET) build $(SOLUTION) $(DOTNET_BUILD_FLAGS)

backend-ci-build: DOTNET_BUILD_FLAGS += --no-restore -p:TreatWarningsAsErrors=true
backend-ci-build: backend-build

backend-lint:
	$(DOTNET) format $(SOLUTION) whitespace --verify-no-changes --verbosity minimal
	$(DOTNET) format $(SOLUTION) style --verify-no-changes --severity info --verbosity minimal
	$(DOTNET) format $(SOLUTION) analyzers --verify-no-changes --severity info --verbosity minimal

lint: backend-lint

test-unit:
	$(DOTNET) test $(UNIT_TEST_PROJECT) $(DOTNET_TEST_FLAGS)

backend-ci-test: DOTNET_TEST_FLAGS += --no-build
backend-ci-test: test-unit

test: test-unit

frontend-typecheck:
	$(PNPM) frontend:typecheck

frontend-build:
	$(PNPM) frontend:build

frontend-preview:
	$(PNPM) frontend:preview

docs-build:
	$(PNPM) docs:build

docs-preview:
	$(PNPM) docs:preview

docs-deploy:
	$(PNPM) docs:deploy

ui-build:
	$(PNPM) ui:build

build: backend-build ui-build appliance-build

check: backend-lint build test-unit frontend-typecheck

image-plan: system-plan

system-plan:
	$(IMAGE_BUILDER) system-plan $(MANIFEST) $(VERSION) "$(ROOT)"

image-plan-check: system-plan-check

system-plan-check:
	$(IMAGE_BUILDER) system-plan $(MANIFEST) $(VERSION) "$(ROOT)" > /dev/null

efi-loader:
	$(IMAGE_BUILDER) build-efi-loader $(EFI_OUTPUT) "$(ROOT)"

avb-helper:
	$(IMAGE_BUILDER) build-homeharbor-avb $(AVB_OUTPUT) "$(ROOT)"

init-helper:
	$(IMAGE_BUILDER) build-homeharbor-init $(INIT_OUTPUT) "$(ROOT)"

appliance-build: system-plan-check $(BOOT_HELPER_TARGETS)

system-build:
	$(IMAGE_BUILDER) system-build $(MANIFEST) $(VERSION) "$(ROOT)"

iso-build:
	$(IMAGE_BUILDER) release-build $(MANIFEST) $(VERSION) "$(ROOT)"

arch-package:
	$(IMAGE_BUILDER) arch-package $(VERSION) "$(ROOT)"

clean:
	$(DOTNET) clean $(SOLUTION)
	$(MAKE) -C boot/bootloader clean
	rm -f "$(EFI_OUTPUT)" "$(AVB_OUTPUT)" "$(INIT_OUTPUT)"
