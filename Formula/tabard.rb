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
  version "0.7.0"
  license "MIT"

  on_macos do
    on_arm do
      url "https://github.com/wivuu/wivuu.tabard/releases/download/v0.7.0/tabard-0.7.0-osx-arm64.tar.gz"
      sha256 "edbc07580753ab39e2c838c8212f39d7fbe4789c5f66dc2e89b063f4667f585b" # osx-arm64
    end

    on_intel do
      url "https://github.com/wivuu/wivuu.tabard/releases/download/v0.7.0/tabard-0.7.0-osx-x64.tar.gz"
      sha256 "ed650e33bb4f7450061475e26f0d1ee06ae4a3b5b5bf61b3e3509cc955c69522" # osx-x64
    end
  end

  on_linux do
    on_arm do
      url "https://github.com/wivuu/wivuu.tabard/releases/download/v0.7.0/tabard-0.7.0-linux-arm64.tar.gz"
      sha256 "600d0aa71e450809922356ea24d7ab0a1d047fd9c6b4aba4438d27501431256f" # linux-arm64
    end

    on_intel do
      url "https://github.com/wivuu/wivuu.tabard/releases/download/v0.7.0/tabard-0.7.0-linux-x64.tar.gz"
      sha256 "d989a9d4126a0e02430900a0034e5f40cf251a59b38751e23300a81fd11fb877" # linux-x64
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
