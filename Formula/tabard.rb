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
  version "0.4.0"
  license "MIT"

  on_macos do
    on_arm do
      url "https://github.com/wivuu/wivuu.tabard/releases/download/v0.4.0/tabard-0.4.0-osx-arm64.tar.gz"
      sha256 "0d17743710363e39d5b9d93ea9706f6dc082795d86eee1e02c28c08d31bdc8e9" # osx-arm64
    end

    on_intel do
      url "https://github.com/wivuu/wivuu.tabard/releases/download/v0.4.0/tabard-0.4.0-osx-x64.tar.gz"
      sha256 "ec6980896e83950b467ff94b70376f3a3deac7264841debca4fbcb4197fefa8b" # osx-x64
    end
  end

  on_linux do
    on_arm do
      url "https://github.com/wivuu/wivuu.tabard/releases/download/v0.4.0/tabard-0.4.0-linux-arm64.tar.gz"
      sha256 "555e774b57e22611b579228ec0d288bf5c766683105e66f89cd9c8616316694b" # linux-arm64
    end

    on_intel do
      url "https://github.com/wivuu/wivuu.tabard/releases/download/v0.4.0/tabard-0.4.0-linux-x64.tar.gz"
      sha256 "b739abe2f4ea90957a0b9064a46b661d47fdfac5e8a7c647a5e5f7d704ede609" # linux-x64
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
