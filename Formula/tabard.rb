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
  version "0.5.0"
  license "MIT"

  on_macos do
    on_arm do
      url "https://github.com/wivuu/wivuu.tabard/releases/download/v0.5.0/tabard-0.5.0-osx-arm64.tar.gz"
      sha256 "831d83b6934fe44344e336d350fb3ad6f850e6dd689e323067eab60f797f19bd" # osx-arm64
    end

    on_intel do
      url "https://github.com/wivuu/wivuu.tabard/releases/download/v0.5.0/tabard-0.5.0-osx-x64.tar.gz"
      sha256 "07b9d32c12bc1e0f4a01cc4464ae7f8a4b29bd61b46bd91e8668d1a24ee0d643" # osx-x64
    end
  end

  on_linux do
    on_arm do
      url "https://github.com/wivuu/wivuu.tabard/releases/download/v0.5.0/tabard-0.5.0-linux-arm64.tar.gz"
      sha256 "01f05bf50a55018ce371b4f14cc84000b2ef278297da5abe839108ace2b5ec2a" # linux-arm64
    end

    on_intel do
      url "https://github.com/wivuu/wivuu.tabard/releases/download/v0.5.0/tabard-0.5.0-linux-x64.tar.gz"
      sha256 "9c88665c3c1e5a75406a74c7a8d9900782c0052b2d8665c11a69bd4cf3b4c266" # linux-x64
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
