#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
results_directory="${1:-$repository_root/TestResults/r1-linux}"
dotnet_command="${DOTNET_COMMAND:-dotnet}"
mkdir -p "$results_directory"
cd "$repository_root"
if [[ "$dotnet_command" == */* ]]; then
  dotnet_command="$(realpath "$dotnet_command")"
  export DOTNET_ROOT="$(dirname "$dotnet_command")"
  export PATH="$DOTNET_ROOT:$PATH"
fi

"$dotnet_command" restore AgentForge.slnx --locked-mode
"$dotnet_command" build AgentForge.slnx --configuration Release --no-restore

projects=(
  tests/AgentForge.UnitTests/AgentForge.UnitTests.csproj
  tests/AgentForge.IntegrationTests/AgentForge.IntegrationTests.csproj
  tests/AgentForge.ArchitectureTests/AgentForge.ArchitectureTests.csproj
  tests/AgentForge.SecurityTests/AgentForge.SecurityTests.csproj
  tests/AgentForge.CrossPlatformTests/AgentForge.CrossPlatformTests.csproj
  tests/AgentForge.EndToEndTests/AgentForge.EndToEndTests.csproj
  spikes/AgentFrameworkSpike/AgentFrameworkSpike.csproj
)

for project in "${projects[@]}"; do
  name="$(basename "$project" .csproj)"
  "$dotnet_command" test "$project" --configuration Release --no-build \
    --logger "trx;LogFileName=$name.trx" --results-directory "$results_directory"
done
