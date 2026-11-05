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
  version "0.6.0"
  license "MIT"

  on_macos do
    on_arm do
      url "https://github.com/wivuu/wivuu.tabard/releases/download/v0.6.0/tabard-0.6.0-osx-arm64.tar.gz"
      sha256 "ceab9414572d2aec8480dac7f33eba2c5a1b33c85a106b115ee4d60f18f3db29" # osx-arm64
    end

    on_intel do
      url "https://github.com/wivuu/wivuu.tabard/releases/download/v0.6.0/tabard-0.6.0-osx-x64.tar.gz"
      sha256 "e4452e5d56a26f4f62ee8ce74ef6c237555a66e3daa965df9d612f35131be65e" # osx-x64
    end
  end

  on_linux do
    on_arm do
      url "https://github.com/wivuu/wivuu.tabard/releases/download/v0.6.0/tabard-0.6.0-linux-arm64.tar.gz"
      sha256 "d756f18cab5c3dd6af535e899caf4b1cf6aeb2a1bc57ab5b3651d3d268447bf5" # linux-arm64
    end

    on_intel do
      url "https://github.com/wivuu/wivuu.tabard/releases/download/v0.6.0/tabard-0.6.0-linux-x64.tar.gz"
      sha256 "32ad47cd058f0c1a0cf3562c77336705347dc2dc25c487308465dfb684b88065" # linux-x64
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
