#!/bin/sh
# tabard installer for macOS and Linux.
#
#   curl -fsSL https://raw.githubusercontent.com/wivuu/wivuu.tabard/master/install.sh | sh
#
# Downloads the native AOT binary from the latest GitHub release, verifies it against that
# release's own SHA256SUMS.txt, and drops it on your PATH. No .NET runtime needed - this is
# the same binary the Homebrew formula serves.
#
# Re-running it upgrades in place: with no --dir it installs over whatever `tabard` is already
# on your PATH, and does nothing at all if that copy is already the release being installed.
# An install owned by Homebrew or `dotnet tool` is left alone with the right upgrade command
# printed instead, since fighting a package manager over its own files only ends one way.
#
# Flags need `sh -s --`, e.g. `curl -fsSL <url> | sh -s -- --version 0.1.1`:
#
#   --version <v>  TABARD_VERSION       install this version instead of the latest
#   --dir <path>   TABARD_INSTALL_DIR   install here (default: ~/.local/bin)
#   --force        TABARD_FORCE=1       install even if a package manager owns tabard

set -eu

REPO=wivuu/wivuu.tabard
VERSION=${TABARD_VERSION:-}
INSTALL_DIR=${TABARD_INSTALL_DIR:-}
FORCE=${TABARD_FORCE:-}

die() {
	echo "install.sh: $1" >&2
	exit 1
}

while [ $# -gt 0 ]; do
	case $1 in
	-v | --version)
		[ $# -ge 2 ] || die "--version needs a value"
		VERSION=$2
		shift 2
		;;
	-d | --dir)
		[ $# -ge 2 ] || die "--dir needs a value"
		INSTALL_DIR=$2
		shift 2
		;;
	-f | --force)
		FORCE=1
		shift
		;;
	-h | --help)
		## Piped into sh there is no script file to read the header comment back out of.
		echo "Install tabard from its latest GitHub release, or upgrade an existing install."
		echo
		echo "  --version <v>  install this version instead of the latest (TABARD_VERSION)"
		echo "  --dir <path>   install here, default ~/.local/bin (TABARD_INSTALL_DIR)"
		echo "  --force        install even if a package manager owns tabard (TABARD_FORCE=1)"
		exit 0
		;;
	*) die "unknown option: $1" ;;
	esac
done

command -v curl >/dev/null 2>&1 || die "curl is required"
command -v tar >/dev/null 2>&1 || die "tar is required"

## sha256sum on Linux, shasum on macOS. Both print "<hash>  <file>". Resolved up front so a
## machine with neither says so here, rather than as an empty hash failing to match later.
if command -v sha256sum >/dev/null 2>&1; then
	sha256=sha256sum
elif command -v shasum >/dev/null 2>&1; then
	sha256="shasum -a 256"
else
	die "need sha256sum or shasum to verify the download"
fi

## Unquoted on purpose: shasum carries its -a 256 along with it.
hash_file() {
	# shellcheck disable=SC2086
	$sha256 "$1" | cut -d' ' -f1
}

## Only the RIDs release.yml actually publishes are on offer here.
os=$(uname -s)
arch=$(uname -m)
case $os in
Darwin)
	case $arch in
	arm64 | aarch64) rid=osx-arm64 ;;
	x86_64) rid=osx-x64 ;;
	*) die "unsupported macOS architecture: $arch" ;;
	esac
	;;
Linux)
	case $arch in
	aarch64 | arm64) rid=linux-arm64 ;;
	x86_64 | amd64) rid=linux-x64 ;;
	*) die "unsupported Linux architecture: $arch" ;;
	esac
	;;
*) die "unsupported OS: $os (on Windows run install.ps1 from PowerShell)" ;;
esac

## /releases/latest redirects to the newest stable tag, so this needs no API token and
## never resolves to a prerelease.
if [ -z "$VERSION" ]; then
	tag_url=$(curl -fsSLI -o /dev/null -w '%{url_effective}' "https://github.com/$REPO/releases/latest") ||
		die "could not reach github.com to resolve the latest release"
	VERSION=${tag_url##*/tag/v}
	case $VERSION in
	"" | *[!0-9.a-zA-Z-]*) die "could not parse a version out of $tag_url" ;;
	esac
fi
VERSION=${VERSION#v}

asset="tabard-$VERSION-$rid.tar.gz"
base="https://github.com/$REPO/releases/download/v$VERSION"

## An existing install decides where this one goes, so a re-run upgrades rather than
## leaving a second copy earlier or later on the PATH.
existing=$(command -v tabard 2>/dev/null || true)
if [ -n "$existing" ]; then
	## Resolve symlinks by hand: readlink -f is not portable to older macOS.
	resolved=$existing
	while [ -L "$resolved" ]; do
		target=$(readlink "$resolved")
		case $target in
		/*) resolved=$target ;;
		*) resolved=$(dirname "$resolved")/$target ;;
		esac
	done

	manager=
	case $resolved in
	*/Cellar/* | */homebrew/* | */linuxbrew/*)
		manager=Homebrew
		hint="brew upgrade tabard"
		;;
	*/.dotnet/tools/* | */dotnet/tools/*)
		manager="the .NET tool"
		hint="dotnet tool update -g Wivuu.Tabard"
		;;
	esac

	## An explicit --dir is a request for a standalone copy somewhere specific, so it says
	## its piece and gets out of the way.
	if [ -n "$manager" ] && [ -z "$FORCE" ] && [ -z "$INSTALL_DIR" ]; then
		echo "tabard is already installed by $manager at $existing."
		echo "Upgrade it with that instead:"
		echo
		echo "    $hint"
		echo
		echo "Re-run with --force to install a standalone copy alongside it."
		exit 0
	fi

	## Inherit the existing location so an upgrade lands where the last one did - but never a
	## package manager's own directory, since --force is meant to install alongside it, not
	## overwrite files brew or dotnet will later disagree about.
	if [ -z "$INSTALL_DIR" ] && [ -z "$manager" ]; then INSTALL_DIR=$(dirname "$resolved"); fi
fi
[ -n "$INSTALL_DIR" ] || INSTALL_DIR=$HOME/.local/bin

tmp=$(mktemp -d) || die "could not create a temp directory"
trap 'rm -rf "$tmp"' EXIT INT TERM

echo "Downloading tabard $VERSION ($rid)..."
curl -fsSL "$base/$asset" -o "$tmp/$asset" || die "could not download $base/$asset"
curl -fsSL "$base/SHA256SUMS.txt" -o "$tmp/sums.txt" || die "could not download $base/SHA256SUMS.txt"

expected=$(awk -v f="$asset" '$2 == f || $2 == "*" f { print $1 }' "$tmp/sums.txt")
[ -n "$expected" ] || die "$asset is not listed in the release's SHA256SUMS.txt"
actual=$(hash_file "$tmp/$asset")
[ "$actual" = "$expected" ] ||
	die "checksum mismatch for $asset (expected $expected, got $actual) - not installing"

tar -xzf "$tmp/$asset" -C "$tmp" tabard || die "could not extract $asset"
chmod +x "$tmp/tabard"

## tabard has no --version flag, so "is this already the version being installed?" is a
## byte comparison against the binary that is already there.
replacing=
current=
if [ -f "$INSTALL_DIR/tabard" ]; then
	replacing=1
	if [ "$(hash_file "$INSTALL_DIR/tabard")" = "$(hash_file "$tmp/tabard")" ]; then current=1; fi
fi

## Nothing to copy when it is already this exact binary, but the PATH check below still runs -
## a re-run is a reasonable way to fix a PATH that was never set up.
if [ -n "$current" ]; then
	echo "tabard $VERSION is already installed at $INSTALL_DIR/tabard - nothing to do."
else
	mkdir -p "$INSTALL_DIR" || die "could not create $INSTALL_DIR"
	[ -w "$INSTALL_DIR" ] ||
		die "$INSTALL_DIR is not writable - re-run with sudo, or --dir to install somewhere else"

	## Stage inside the target directory so the last step is a same-filesystem rename: atomic,
	## and safe to do while the old binary is running.
	staged="$INSTALL_DIR/.tabard.$$"
	cp "$tmp/tabard" "$staged" || die "could not write to $INSTALL_DIR"
	chmod +x "$staged"
	mv -f "$staged" "$INSTALL_DIR/tabard" || {
		rm -f "$staged"
		die "could not replace $INSTALL_DIR/tabard"
	}

	if [ -n "$replacing" ]; then
		echo "Updated tabard to $VERSION at $INSTALL_DIR/tabard"
	else
		echo "Installed tabard $VERSION to $INSTALL_DIR/tabard"
	fi
fi

case ":$PATH:" in
*":$INSTALL_DIR:"*) ;;
*)
	echo
	echo "$INSTALL_DIR is not on your PATH. Add it:"
	echo
	case ${SHELL##*/} in
	fish) echo "    fish_add_path $INSTALL_DIR" ;;
	zsh) echo "    echo 'export PATH=\"$INSTALL_DIR:\$PATH\"' >> ~/.zshrc" ;;
	*) echo "    echo 'export PATH=\"$INSTALL_DIR:\$PATH\"' >> ~/.bashrc" ;;
	esac
	;;
esac

echo
echo "Run 'tabard --help' to get started."
