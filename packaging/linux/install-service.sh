#!/usr/bin/env bash
set -euo pipefail
if [[ "$(id -u)" -eq 0 ]]; then
  echo "Run this user-service installer as the single AgentForge operator, not root." >&2
  exit 1
fi
package_root="$(realpath "$(dirname "${BASH_SOURCE[0]}")")"
host_executable="$(realpath "$package_root/host/AgentForge.Host")"
case "$host_executable" in
  "$package_root/host/AgentForge.Host") ;;
  *) echo "Package host path is invalid." >&2; exit 1 ;;
esac
install_root="$HOME/.local/opt/agentforge"
data_root="$HOME/.local/share/agentforge"
unit_root="$HOME/.config/systemd/user"
install -d -m 0700 "$install_root" "$data_root"
for component in host cli worker; do
  install -d -m 0700 "$install_root/$component"
  cp -a "$package_root/$component/." "$install_root/$component/"
done
install -d -m 0700 "$unit_root"
install -m 0600 "$package_root/agentforge.service" "$unit_root/agentforge.service"
systemctl --user daemon-reload
systemctl --user enable agentforge.service
echo "Installed AgentForge user service. Run: systemctl --user start agentforge"
