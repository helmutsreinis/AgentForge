# ADR 0008: Portable skill content plus immutable harness sidecars

Status: Accepted

`SKILL.md` remains portable. `skill.harness.json` carries lifecycle, permissions,
compatibility, hashes, provenance, tests, and promotion policy. Versions are
content-addressed and immutable; active pointers change transactionally; every run
uses a snapshot. Seed and created skills use the same evaluation and rollout path.
