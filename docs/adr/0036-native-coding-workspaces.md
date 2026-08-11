# ADR 0036: Native coding workspaces and semantic evidence

Status: Accepted

## Context

Coding against an operator's current worktree can overwrite unrelated changes. Filename search alone
cannot provide reliable symbol, reference, project-graph, or compiler evidence, while an external
coding backend must not become a policy or verification authority.

## Decision

Discover repositories passively with bounded link-free metadata and normalized hashes. Load C#
projects with the installed MSBuild instance and Roslyn Workspaces for symbol, reference, and
diagnostic evidence. Create each coding session from an exact commit and tree in a dedicated Git
worktree and branch using literal argument arrays. Dirty sources deny by default; removal accepts
only a clean linked worktree marker.

Keep discovery, semantic navigation, workspace management, later patch application, backends,
verification, and durable session state behind AgentForge contracts. Backends may propose changes
but never commit, verify, or widen authority.

## Consequences

Operator changes remain outside the coding workspace and exact baselines make patches reproducible.
MSBuild loading is an explicit environmental capability and may fail typed for unsupported project
systems. Worktree cleanup intentionally refuses dirty targets for recoverability.
