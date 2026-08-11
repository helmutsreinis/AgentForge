# Coding harness fixture

This repository intentionally starts with an incorrect `Calculator.Add` implementation. The
Milestone 6 acceptance test creates an isolated worktree, applies an exact hash-bound patch, builds
the solution, runs the executable specification, simulates an interruption, resumes from the last
durable verifier checkpoint, reviews the exact diff, and proves the baseline checkout is untouched.
