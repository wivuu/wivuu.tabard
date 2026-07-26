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
  version "0.2.0"
  license "MIT"

  on_macos do
    on_arm do
      url "https://github.com/wivuu/wivuu.tabard/releases/download/v0.2.0/tabard-0.2.0-osx-arm64.tar.gz"
      sha256 "0bed012d1d435caf6a7c3bbb0bc1529e603f07e0893bb678bc9ca3eb5e64b2f4" # osx-arm64
    end

    on_intel do
      url "https://github.com/wivuu/wivuu.tabard/releases/download/v0.2.0/tabard-0.2.0-osx-x64.tar.gz"
      sha256 "a811000f1b89b2e16196dcbaddd23c5464267af046e9f2c6b55c8ec668122a8a" # osx-x64
    end
  end

  on_linux do
    on_arm do
      url "https://github.com/wivuu/wivuu.tabard/releases/download/v0.2.0/tabard-0.2.0-linux-arm64.tar.gz"
      sha256 "f1491cb73248d9a5c5fbe54d5355a8e0d98f92b4f0e4b64a82b2675a93dc799d" # linux-arm64
    end

    on_intel do
      url "https://github.com/wivuu/wivuu.tabard/releases/download/v0.2.0/tabard-0.2.0-linux-x64.tar.gz"
      sha256 "600cd5a9352da8ec768fbefb94e1403565a31b289ece53a6e23b6bbf9561fc5e" # linux-x64
    end
  end

  def install
    bin.install "tabard"
  end

  test do
    # tabard has no --version flag; --help is tabard's own help and touches nothing on disk.
    assert_match "Claude Code profile switcher", shell_output("#{bin}/tabard --help")
  end
end
