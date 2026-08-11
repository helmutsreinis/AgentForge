# ADR 0031: Translate Anthropic Messages behind harness contracts

Status: Accepted

## Context

Anthropic Messages uses a request shape, authentication headers, content blocks, stop reasons, and
SSE event sequence that differ materially from OpenAI-compatible chat completions. Treating it as a
compatible endpoint would weaken validation and could turn a malformed or truncated stream into a
false completion.

## Decision

Implement a distinct `anthropic` adapter while retaining AgentForge-owned requests and stream
events. Require an exact HTTPS/cloud profile, policy-bound socket resolution, prepared context,
invocation-scoped `x-api-key` materialization, an exact API version, bounded request/event/content
blocks, listed tool names, normalized object arguments, usage evidence, and `message_stop` before
completion. Reject media and structured output until an exact Anthropic translation is gated.

Production composition keeps the provider catalog empty. Live credential verification remains an
environment-gated integration test rather than a prerequisite for deterministic acceptance.

## Consequences

Vendor protocol behavior cannot leak into orchestration contracts and malformed, substituted,
oversized, or incomplete events fail typed. The implementation is SDK-independent and portable,
at the cost of maintaining an explicit bounded protocol translator.
