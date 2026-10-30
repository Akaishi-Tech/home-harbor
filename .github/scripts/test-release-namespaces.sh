#!/usr/bin/env bash
set -euo pipefail

.github/scripts/configure-release-namespaces.sh

docker run --rm -i \
  --security-opt seccomp=unconfined \
  --security-opt apparmor=homeharbor-builder \
  -v "${PWD}:/workspace" -w /workspace \
  archlinux:base-devel bash <<'CONTAINER'
set -euo pipefail
pacman -Syu --noconfirm --needed arch-install-scripts dotnet-sdk sudo
chmod u-s /usr/bin/newuidmap /usr/bin/newgidmap
setcap cap_setuid=ep /usr/bin/newuidmap
setcap cap_setgid=ep /usr/bin/newgidmap
useradd -m -G wheel builder
printf 'builder ALL=(ALL) NOPASSWD: ALL\n' > /etc/sudoers.d/homeharbor-builder
chmod 0440 /etc/sudoers.d/homeharbor-builder
printf 'builder:100000:65536\n' >> /etc/subuid
printf 'builder:100000:65536\n' >> /etc/subgid
chown -R builder:builder /workspace
sudo -E -H -u builder bash <<'BUILDER'
set -euo pipefail
namespace_probe_dir=$(mktemp -d)
trap 'rm -rf "${namespace_probe_dir}"' EXIT
cat > "${namespace_probe_dir}/Probe.csproj" <<'PROJECT'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="/workspace/tools/system-build/src/HomeHarbor.SystemBuild/HomeHarbor.SystemBuild.csproj" />
  </ItemGroup>
</Project>
PROJECT
cat > "${namespace_probe_dir}/Program.cs" <<'CS'
using HomeHarbor.Tooling;
await new RootlessBuildExecutor(new ProcessCommandRunner()).RequireReadyAsync();
System.Console.WriteLine("Pinned rootless builder namespace probe passed.");
CS
dotnet run --project "${namespace_probe_dir}/Probe.csproj" --configuration Release
BUILDER
CONTAINER
