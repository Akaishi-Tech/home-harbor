#!/usr/bin/env bash
set -euo pipefail

# Ubuntu restricts user namespaces even for an AppArmor-unconfined process.
sudo apparmor_parser -r -W <<'PROFILE'
abi <abi/4.0>,
include <tunables/global>
profile homeharbor-builder flags=(unconfined) {
  userns,
}
PROFILE
