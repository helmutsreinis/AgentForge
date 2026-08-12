#!/usr/bin/env bash
set -euo pipefail

version="${1:-1.0.0}"
rid="${2:-linux-x64}"
if [[ ! "$version" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?$ ]]; then
  echo "Version must be stable SemVer." >&2
  exit 1
fi
if [[ "$rid" != "linux-x64" && "$rid" != "win-x64" ]]; then
  echo "RID must be linux-x64 or win-x64." >&2
  exit 1
fi

repo_root="$(realpath -m "$(dirname "${BASH_SOURCE[0]}")/..")"
allowed_root="$(realpath -m "$repo_root/artifacts/release")"
output_root="$(realpath -m "$allowed_root/$rid-build")"
case "$output_root/" in
  "$allowed_root/"*) ;;
  *) echo "Release output escaped artifacts/release." >&2; exit 1 ;;
esac
if [[ -e "$output_root" ]]; then
  resolved="$(realpath "$output_root")"
  [[ "$resolved" == "$output_root" ]] || { echo "Release output resolved unexpectedly." >&2; exit 1; }
  rm -rf -- "$resolved"
fi
mkdir -p "$output_root/$rid"

commit="$(git -C "$repo_root" rev-parse HEAD)"
created="$(git -C "$repo_root" show -s --format=%cI "$commit")"
declare -A projects=(
  [host]="src/AgentForge.Host/AgentForge.Host.csproj"
  [cli]="src/AgentForge.Cli/AgentForge.Cli.csproj"
  [worker]="src/AgentForge.PluginWorker/AgentForge.PluginWorker.csproj"
)
cd "$repo_root"
for component in host cli worker; do
  project="${projects[$component]}"
  dotnet restore "$project" --locked-mode
  dotnet publish "$project" --configuration Release --runtime "$rid" --self-contained true \
    --no-restore --output "$output_root/$rid/$component" -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false \
    -p:Version="$version" -p:ContinuousIntegrationBuild=true
done
cp packaging/common/README.md "$output_root/$rid/README.md"
if [[ "$rid" == "linux-x64" ]]; then
  cp packaging/linux/appsettings.Production.json "$output_root/$rid/host/appsettings.Production.json"
  cp packaging/linux/agentforge.service packaging/linux/install-service.sh "$output_root/$rid/"
  format="tar.gz"
  archive="$output_root/AgentForge-$version-linux-x64.tar.gz"
else
  cp packaging/windows/appsettings.Production.json "$output_root/$rid/host/appsettings.Production.json"
  cp packaging/windows/install-service.ps1 packaging/windows/uninstall-service.ps1 "$output_root/$rid/"
  format="zip"
  archive="$output_root/AgentForge-$version-win-x64.zip"
fi
dotnet run --project tools/AgentForge.Release/AgentForge.Release.csproj --configuration Release --no-restore -- \
  archive --source-directory "$output_root/$rid" --output-path "$archive" --format "$format" --created "$created"
dotnet run --project tools/AgentForge.Release/AgentForge.Release.csproj --configuration Release --no-restore -- \
  manifest --release-directory "$output_root" --repository-root "$repo_root" --version "$version" \
  --commit "$commit" --created "$created"
dotnet run --project tools/AgentForge.Release/AgentForge.Release.csproj --configuration Release --no-restore -- \
  verify --release-directory "$output_root"
