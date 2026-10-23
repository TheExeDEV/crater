param(
	[string]$Version = "latest",
	[string]$InstallDir = (Join-Path $env:LOCALAPPDATA "crater\bin"),
	[switch]$NoPath
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$repository = "TheExeDEV/crater"
$isArm = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq [System.Runtime.InteropServices.Architecture]::Arm64
$architecture = if ($isArm) { "arm64" } else { "x64" }
$asset = "crater-win-$architecture.exe"

if ($Version -eq "latest") {
	$url = "https://github.com/$repository/releases/latest/download/$asset"
} else {
	$url = "https://github.com/$repository/releases/download/v$($Version.TrimStart('v'))/$asset"
}

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null

$target = Join-Path $InstallDir "crater.exe"
$download = "$target.download"

Write-Host "Downloading $url"
Invoke-WebRequest -Uri $url -OutFile $download -UseBasicParsing
Move-Item -Path $download -Destination $target -Force

if (-not $NoPath) {
	$userPath = [Environment]::GetEnvironmentVariable("Path", "User")
	$entries = @()

	if ($userPath) {
		$entries = $userPath.Split(";") | Where-Object { $_ -ne "" }
	}

	if ($entries -notcontains $InstallDir) {
		[Environment]::SetEnvironmentVariable("Path", (@($entries) + $InstallDir) -join ";", "User")
		Write-Host "Added $InstallDir to your PATH. Open a new terminal to use crater."
	}

	if (($env:Path.Split(";")) -notcontains $InstallDir) {
		$env:Path = "$env:Path;$InstallDir"
	}
}

& $target --version
