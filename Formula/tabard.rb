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
  version "0.1.1"
  license "MIT"

  on_macos do
    on_arm do
      url "https://github.com/wivuu/wivuu.tabard/releases/download/v0.1.1/tabard-0.1.1-osx-arm64.tar.gz"
      sha256 "7cf68f67b91ce81a5adeb5c75bb623db98179a3def90d82c904f573c940e3f84" # osx-arm64
    end

    on_intel do
      url "https://github.com/wivuu/wivuu.tabard/releases/download/v0.1.1/tabard-0.1.1-osx-x64.tar.gz"
      sha256 "17cd66d85b757e409c3cb427e11e673213a559aa3bf2ed794f82d7aebe3b2698" # osx-x64
    end
  end

  on_linux do
    on_arm do
      url "https://github.com/wivuu/wivuu.tabard/releases/download/v0.1.1/tabard-0.1.1-linux-arm64.tar.gz"
      sha256 "de14e93bae29d1dd0a9a885a4d1b1751b05e40fa871d9e24a030a6dc3f214e7f" # linux-arm64
    end

    on_intel do
      url "https://github.com/wivuu/wivuu.tabard/releases/download/v0.1.1/tabard-0.1.1-linux-x64.tar.gz"
      sha256 "121b48e9a626af3dd4131504d913c0852f67290a4b7e975d405309efcd5f459a" # linux-x64
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
