# Homebrew formula for tabard. This repo doubles as its own tap:
#
#   brew tap wivuu/tabard https://github.com/wivuu/wivuu.tabard
#   brew install tabard
#
# The bottles are the native AOT binaries release.yml attaches to each GitHub release,
# so there is nothing to compile and no .NET runtime to install. The `packaging` job in
# release.yml rewrites the version and the four sha256 lines on every non-prerelease tag;
# the trailing RID comments are what it keys off, so leave them in place.
class Tabard < Formula
  desc "Claude Code profile switcher - one login per profile, picked at launch"
  homepage "https://github.com/wivuu/wivuu.tabard"
  version "0.3.0"
  license "MIT"

  on_macos do
    on_arm do
      url "https://github.com/wivuu/wivuu.tabard/releases/download/v0.3.0/tabard-0.3.0-osx-arm64.tar.gz"
      sha256 "3724045632e104fb9a8797f684e43b94b2c1ea7ba810dc1a9ecc6d45b44cde88" # osx-arm64
    end

    on_intel do
      url "https://github.com/wivuu/wivuu.tabard/releases/download/v0.3.0/tabard-0.3.0-osx-x64.tar.gz"
      sha256 "d9ae86265f8fd3ad600eb52fa38aae84a1a11ce66408ec5ac13fcd9a0a3bc2d8" # osx-x64
    end
  end

  on_linux do
    on_arm do
      url "https://github.com/wivuu/wivuu.tabard/releases/download/v0.3.0/tabard-0.3.0-linux-arm64.tar.gz"
      sha256 "7dd490ed555de394b7b1522a602c368616c6358c2f23c733f3f0e49bcc97c79b" # linux-arm64
    end

    on_intel do
      url "https://github.com/wivuu/wivuu.tabard/releases/download/v0.3.0/tabard-0.3.0-linux-x64.tar.gz"
      sha256 "2291aaab52201a34731d693d0919520ddd9c1f133b12233281630b1eed93ae76" # linux-x64
    end
  end

  def install
    bin.install "tabard"

    # Asks the binary that was just installed for its own script, so brew users get completion
    # without running `tabard completion install`. The zsh script doubles as an fpath function
    # file - it opens with `#compdef tabard` - which is what lands here as `_tabard`. No :fish,
    # because `tabard completion` has nothing for it and would fail the install.
    generate_completions_from_executable(bin/"tabard", "completion", shells: [:bash, :zsh])
  end

  test do
    # tabard has no --version flag; --help is tabard's own help and touches nothing on disk.
    assert_match "Claude Code profile switcher", shell_output("#{bin}/tabard --help")
  end
end
