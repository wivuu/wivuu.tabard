param(
	# Install this version instead of the latest.
	[string] $Version = $env:TABARD_VERSION,
	# Install here instead of %LOCALAPPDATA%\Programs\tabard.
	[string] $InstallDir = $env:TABARD_INSTALL_DIR,
	# Leave the user PATH alone.
	[switch] $NoPath,
	# Install even if the .NET tool owns tabard.
	[switch] $Force
)

# tabard installer for Windows.
#
#   irm https://raw.githubusercontent.com/wivuu/wivuu.tabard/master/install.ps1 | iex
#
# Downloads the native AOT binary from the latest GitHub release, verifies it against that
# release's own SHA256SUMS.txt, and puts it on your PATH. No .NET runtime needed.
#
# Re-running it upgrades in place: with no -InstallDir it installs over whatever `tabard` is
# already on your PATH, and does nothing at all if that copy is already the release being
# installed. An install owned by `dotnet tool` is left alone with the right upgrade command
# printed instead.
#
# `iex` cannot pass arguments, so to use the switches, build the script block yourself:
#
#   & ([scriptblock]::Create((irm https://.../install.ps1))) -Version 0.1.1

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue' # a progress bar makes Invoke-WebRequest much slower

$repo = 'wivuu/wivuu.tabard'

# Windows PowerShell 5.1 still defaults to protocols github.com hung up on years ago.
try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch {}

# PROCESSOR_ARCHITECTURE describes this process; a 32-bit shell on 64-bit Windows reports
# x86 and sets the W6432 variable to what the OS actually is.
$arch = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
switch ($arch) {
	'AMD64' { $rid = 'win-x64' }
	'ARM64' {
		# Only win-x64 is published; ARM64 Windows runs it under emulation.
		$rid = 'win-x64'
		Write-Host 'No native ARM64 build is published yet - installing win-x64, which runs emulated.'
	}
	default { throw "unsupported architecture: $arch" }
}

# /releases/latest never points at a prerelease, and tag_name is the whole of what we need.
if (-not $Version) {
	try {
		$Version = (Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest").tag_name
	}
	catch {
		throw "could not reach github.com to resolve the latest release: $($_.Exception.Message)"
	}
}
$Version = $Version -replace '^v', ''

$asset = "tabard-$Version-$rid.zip"
$base = "https://github.com/$repo/releases/download/v$Version"

# An existing install decides where this one goes, so a re-run upgrades rather than leaving
# a second copy earlier or later on the PATH.
$existing = (Get-Command tabard -CommandType Application -ErrorAction SilentlyContinue |
	Select-Object -First 1).Source

if ($existing) {
	$managed = $existing -like '*\.dotnet\tools\*'

	# An explicit -InstallDir is a request for a standalone copy somewhere specific, so it
	# says its piece and gets out of the way.
	if ($managed -and -not $Force -and -not $InstallDir) {
		Write-Host "tabard is already installed as a .NET tool at $existing."
		Write-Host 'Upgrade it with that instead:'
		Write-Host ''
		Write-Host '    dotnet tool update -g Wivuu.Tabard'
		Write-Host ''
		Write-Host 'Re-run with -Force to install a standalone copy alongside it.'
		return
	}

	# Inherit the existing location so an upgrade lands where the last one did - but never
	# dotnet's own tools directory, since -Force is meant to install alongside that, not
	# overwrite files dotnet will later disagree about.
	if (-not $InstallDir -and -not $managed) { $InstallDir = Split-Path -Parent $existing }
}
if (-not $InstallDir) { $InstallDir = Join-Path $env:LOCALAPPDATA 'Programs\tabard' }

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("tabard-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp -Force | Out-Null

try {
	Write-Host "Downloading tabard $Version ($rid)..."
	$zip = Join-Path $tmp $asset
	$sums = Join-Path $tmp 'SHA256SUMS.txt'
	try {
		Invoke-WebRequest "$base/$asset" -OutFile $zip -UseBasicParsing
		Invoke-WebRequest "$base/SHA256SUMS.txt" -OutFile $sums -UseBasicParsing
	}
	catch {
		throw "could not download $base/$asset : $($_.Exception.Message)"
	}

	# SHA256SUMS.txt is "<hash>  <file>", written by sha256sum on the release runner.
	$expected = $null
	foreach ($line in Get-Content $sums) {
		$parts = $line -split '\s+'
		if ($parts.Count -ge 2 -and ($parts[1] -replace '^\*', '') -eq $asset) { $expected = $parts[0] }
	}
	if (-not $expected) { throw "$asset is not listed in the release's SHA256SUMS.txt" }

	$actual = (Get-FileHash $zip -Algorithm SHA256).Hash
	if ($actual -ne $expected.ToUpperInvariant()) {
		throw "checksum mismatch for $asset (expected $expected, got $actual) - not installing"
	}

	Expand-Archive -Path $zip -DestinationPath $tmp -Force
	$fresh = Join-Path $tmp 'tabard.exe'
	if (-not (Test-Path $fresh)) { throw "$asset did not contain tabard.exe" }

	$target = Join-Path $InstallDir 'tabard.exe'

	# tabard has no --version flag, so "is this already the version being installed?" is a
	# byte comparison against the binary that is already there - the extracted exe, not the
	# zip that $actual hashes.
	$replacing = Test-Path $target
	$current = $replacing -and
		(Get-FileHash $target -Algorithm SHA256).Hash -eq (Get-FileHash $fresh -Algorithm SHA256).Hash

	# Nothing to copy when it is already this exact binary, but the PATH block below still
	# runs - a re-run is a reasonable way to fix a PATH that was never set up.
	if ($current) {
		Write-Host "tabard $Version is already installed at $target - nothing to do."
	}
	else {
		New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null

		# Windows will not let a running executable be overwritten, but it will let it be
		# renamed out of the way. Sweep up anything a previous upgrade parked here first.
		Get-ChildItem -Path $InstallDir -Filter 'tabard.exe.old*' -ErrorAction SilentlyContinue |
			ForEach-Object { Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue }

		if ($replacing) {
			try {
				Remove-Item $target -Force
			}
			catch {
				Rename-Item -Path $target -NewName 'tabard.exe.old' -Force
				Write-Host "tabard was running, so the old binary was left as $target.old - it gets cleaned up on the next run."
			}
		}
		Move-Item -Path $fresh -Destination $target -Force

		if ($replacing) { Write-Host "Updated tabard to $Version at $target" }
		else { Write-Host "Installed tabard $Version to $target" }
	}
}
finally {
	Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

# The user PATH, not the machine one: no elevation, and it only affects this account.
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
$onPath = ($userPath -split ';' | Where-Object { $_.TrimEnd('\') -eq $InstallDir.TrimEnd('\') })

if ($onPath) {
	# Already registered - just make sure this session can see it too.
	if (($env:Path -split ';' | Where-Object { $_.TrimEnd('\') -eq $InstallDir.TrimEnd('\') }).Count -eq 0) {
		$env:Path = "$InstallDir;$env:Path"
	}
}
elseif ($NoPath) {
	Write-Host ''
	Write-Host "$InstallDir is not on your PATH. Add it yourself, or re-run without -NoPath."
}
else {
	$updated = if ($userPath) { "$userPath;$InstallDir" } else { $InstallDir }
	[Environment]::SetEnvironmentVariable('Path', $updated, 'User')
	$env:Path = "$InstallDir;$env:Path"
	Write-Host ''
	Write-Host "Added $InstallDir to your user PATH. Open a new terminal for other shells to see it."
}

Write-Host ''
Write-Host "Run 'tabard --help' to get started, or 'tabard completion install' for tab completion."
