#!/bin/sh
set -eu

repository="TheExeDEV/crater"
version="${CRATER_VERSION:-latest}"
install_dir="${CRATER_INSTALL_DIR:-$HOME/.crater/bin}"

case "$(uname -s)" in
	Linux) system="linux" ;;
	Darwin) system="osx" ;;
	*) echo "Unsupported system: $(uname -s). On Windows use install.ps1." >&2; exit 1 ;;
esac

case "$(uname -m)" in
	x86_64 | amd64) architecture="x64" ;;
	arm64 | aarch64) architecture="arm64" ;;
	*) echo "Unsupported architecture: $(uname -m)" >&2; exit 1 ;;
esac

asset="crater-$system-$architecture"

if [ "$version" = "latest" ]; then
	url="https://github.com/$repository/releases/latest/download/$asset"
else
	url="https://github.com/$repository/releases/download/v${version#v}/$asset"
fi

mkdir -p "$install_dir"

echo "Downloading $url"
curl --fail --silent --show-error --location "$url" --output "$install_dir/crater.download"
chmod +x "$install_dir/crater.download"
mv "$install_dir/crater.download" "$install_dir/crater"

case ":$PATH:" in
	*":$install_dir:"*) ;;
	*)
		echo ""
		echo "Add crater to your PATH, for example in ~/.bashrc or ~/.zshrc:"
		echo "  export PATH=\"$install_dir:\$PATH\""
		;;
esac

"$install_dir/crater" --version
