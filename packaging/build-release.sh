#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C

if [[ $# != 2 || ! $1 =~ ^[0-9]+\.[0-9]+\.[0-9]+$ || ! $2 =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    echo "Usage: $0 <release-version> <published-dotnet-runtime-version>" >&2
    exit 2
fi
version=$1
runtime_version=$2
cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.."

stem="gamepad-filter-v$version-linux-x64"
binary="$PWD/out/gamepad-filter"
destination="$PWD/out/release"
packages="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
runtime_pack="$packages/microsoft.netcore.app.runtime.linux-x64/$runtime_version"

# Read the highest glibc symbol version from the exact binary being packaged.
glibc_floor=$(readelf -W --version-info "$binary" |
    sed -n 's/.*Name: GLIBC_\([0-9][0-9.]*\) .*/\1/p' | sort -V | tail -1)
if [[ -z $glibc_floor ]] || ! readelf -h "$binary" | grep -q 'Machine:.*X86-64'; then
    echo "Publish a glibc Linux x86-64 executable to out/gamepad-filter first." >&2
    exit 1
fi

scratch=$(mktemp -d)
trap 'rm -rf -- "$scratch"' EXIT
bundle="$scratch/$stem"
mkdir -p "$bundle"
install -m 755 "$binary" "$bundle/gamepad-filter"
install -m 644 README.md afterglow.conf "$bundle/"
install -m 644 "$runtime_pack/LICENSE.TXT" "$bundle/DOTNET-LICENSE.txt"
install -m 644 "$runtime_pack/THIRD-PARTY-NOTICES.TXT" "$bundle/DOTNET-THIRD-PARTY-NOTICES.txt"
(cd "$scratch" && zip -q -r "$stem.zip" "$stem")

deb_root="$scratch/deb"
deb_name="gamepad-filter_${version}-1_amd64.deb"
doc_dir="$deb_root/usr/share/doc/gamepad-filter"
mkdir -p "$deb_root/DEBIAN" "$doc_dir"
install -D -m 755 "$binary" "$deb_root/usr/bin/gamepad-filter"
install -m 644 "$bundle/"*.txt "$bundle/README.md" "$bundle/afterglow.conf" "$doc_dir/"
cat > "$deb_root/DEBIAN/control" <<CONTROL
Package: gamepad-filter
Version: $version-1
Section: utils
Priority: optional
Architecture: amd64
Maintainer: Mashrien <29497715+Mashrien@users.noreply.github.com>
Depends: libc6 (>= $glibc_floor), libudev1, libayatana-appindicator3-1, libgtk-3-0t64 | libgtk-3-0
Homepage: https://github.com/Mashrien/linux-gamepad-transform-driver
Description: INI-configurable Linux gamepad axis filter
 Filter analog gamepad axes in userspace with evdev and uinput.
 Use a single INI file across multiple controllers.
CONTROL
dpkg-deb --build --root-owner-group "$deb_root" "$scratch/$deb_name"

rpm_tree="$scratch/rpm"
rpm_name="gamepad-filter-$version-1.x86_64.rpm"
mkdir -p "$rpm_tree/SOURCES"
cp "$scratch/$stem.zip" "$rpm_tree/SOURCES/"
rpmbuild -bb --target x86_64 \
    --define "_topdir $rpm_tree" \
    --define "source_date_epoch_from_changelog 0" \
    --define "app_version $version" \
    --define "glibc_floor $glibc_floor" \
    packaging/gamepad-filter.spec

mkdir -p "$destination"
install -m 644 "$scratch/$deb_name" "$rpm_tree/RPMS/x86_64/$rpm_name" "$scratch/$stem.zip" "$destination/"
(
    cd "$destination"
    sha256sum "$deb_name" "$rpm_name" "$stem.zip" > SHA256SUMS
    sha256sum -c SHA256SUMS
)
