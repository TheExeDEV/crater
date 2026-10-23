param(
	[string]$InstallDir = (Join-Path $env:LOCALAPPDATA "crater\bin"),
	[string]$CacheDir = $(if ($env:CRATER_CACHE) { $env:CRATER_CACHE } else { Join-Path $env:LOCALAPPDATA "crater\cache" }),
	[string]$ConfigDir = (Join-Path $env:APPDATA "crater"),
	[switch]$Purge,
	[switch]$NoPath
)

$ErrorActionPreference = "Stop"

$removed = 0
$files = @("crater.exe", "crater.exe.old", "crater.exe.new", "crater.exe.download")

foreach ($file in $files) {
	$path = Join-Path $InstallDir $file

	if (Test-Path -LiteralPath $path) {
		try {
			Remove-Item -LiteralPath $path -Force
		} catch {
			Write-Error "Could not remove $path. Close every running crater and try again."
		}

		Write-Host "Removed $path"
		$removed++
	}
}

if ((Test-Path -LiteralPath $InstallDir) -and -not (Get-ChildItem -LiteralPath $InstallDir -Force | Select-Object -First 1)) {
	Remove-Item -LiteralPath $InstallDir -Force
	Write-Host "Removed $InstallDir"
}

if (-not $NoPath) {
	$userPath = [Environment]::GetEnvironmentVariable("Path", "User")

	if ($userPath) {
		$entries = $userPath.Split(";") | Where-Object { $_ -ne "" }
		$kept = @($entries | Where-Object { $_.TrimEnd("\") -ne $InstallDir.TrimEnd("\") })

		if ($kept.Count -ne @($entries).Count) {
			[Environment]::SetEnvironmentVariable("Path", $kept -join ";", "User")
			Write-Host "Removed $InstallDir from your PATH"
		}
	}
}

if ($Purge) {
	foreach ($directory in @($CacheDir, $ConfigDir)) {
		if (Test-Path -LiteralPath $directory) {
			Remove-Item -LiteralPath $directory -Recurse -Force
			Write-Host "Removed $directory"
		}
	}

	$root = Join-Path $env:LOCALAPPDATA "crater"

	if ((Test-Path -LiteralPath $root) -and -not (Get-ChildItem -LiteralPath $root -Force | Select-Object -First 1)) {
		Remove-Item -LiteralPath $root -Force
	}
}

if ($removed -eq 0) {
	Write-Host "crater was not found in $InstallDir"

	if (Get-Command dotnet -ErrorAction SilentlyContinue) {
		Write-Host "If you installed it as a .NET tool, run: dotnet tool uninstall -g crater"
	}

	exit 0
}

Write-Host "crater is uninstalled. Open a new terminal to refresh PATH."

if (-not $Purge) {
	Write-Host "The download cache and global config were kept. Run with -Purge to remove them too."
}
