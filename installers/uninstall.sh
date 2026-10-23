#!/bin/sh
set -eu

install_dir="${CRATER_INSTALL_DIR:-$HOME/.crater/bin}"
cache_dir="${CRATER_CACHE:-${XDG_DATA_HOME:-$HOME/.local/share}/crater/cache}"
config_dir="${XDG_CONFIG_HOME:-$HOME/.config}/crater"
purge=0

for argument in "$@"; do
	case "$argument" in
		--purge) purge=1 ;;
		*) echo "Unknown option: $argument (use --purge)" >&2; exit 1 ;;
	esac
done

removed=0

for file in crater crater.old crater.new crater.download; do
	if [ -e "$install_dir/$file" ]; then
		rm -f "$install_dir/$file"
		echo "Removed $install_dir/$file"
		removed=$((removed + 1))
	fi
done

remove_if_empty() {
	if [ -d "$1" ] && [ -z "$(ls -A "$1")" ]; then
		rmdir "$1"
		echo "Removed $1"
	fi
}

remove_if_empty "$install_dir"
remove_if_empty "$(dirname "$install_dir")"

if [ "$purge" -eq 1 ]; then
	for directory in "$cache_dir" "$config_dir"; do
		if [ -d "$directory" ]; then
			rm -rf "$directory"
			echo "Removed $directory"
		fi
	done

	remove_if_empty "$(dirname "$cache_dir")"
fi

if [ "$removed" -eq 0 ]; then
	echo "crater was not found in $install_dir"
	echo "If you installed it as a .NET tool, run: dotnet tool uninstall -g crater"
	exit 0
fi

echo "crater is uninstalled."

case ":$PATH:" in
	*":$install_dir:"*)
		echo "Remove this line from ~/.bashrc or ~/.zshrc:"
		echo "  export PATH=\"$install_dir:\$PATH\""
		;;
esac

if [ "$purge" -eq 0 ]; then
	echo "The download cache and global config were kept. Run with --purge to remove them too."
fi
