# ADR 0004: Harness-owned model contracts; Agent Framework as optional adapter

Status: Accepted after spike

AgentForge owns provider-neutral requests, streaming events, capability evidence,
routing, policy, usage, and snapshots. Microsoft.Extensions.AI and vendor SDKs may be
used inside adapters. Microsoft Agent Framework 1.17 passed the M0 typed workflow,
streaming, and cancellation-token spike but will not own durable task, lease, audit,
approval, skill, or promotion state.
