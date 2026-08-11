# ADR 0030: Named compatible providers retain harness identities

Status: Accepted

## Context

OpenAI, DeepSeek, vLLM, and many self-hosted servers use an OpenAI-compatible chat-completions
protocol, but treating every profile as the generic type loses provider identity and prevents
provider-specific validation, routing evidence, health, and future quirks. Setup also previously
registered only the deterministic validator, so named profiles could not be created through the
shared CLI services.

## Decision

The hardened compatible adapter accepts exactly `openai`, `deepseek`, `vllm`, and
`openai-compatible` descriptor/profile types and preserves that type in every started event and
catalog entry. It does not rewrite identities to the generic type.

`AgentForge.Models` supplies the composed provider-profile validator. Deterministic profiles keep
their original validation behavior. Named compatible profiles require an exact OS-secret-store
reference, a bounded header-safe materialized credential, a safe endpoint, and a known adapter.
OpenAI and DeepSeek require HTTPS. vLLM and generic-compatible may use plaintext HTTP only for
literal/localhost Loopback or PrivateNetwork destinations; the invocation connector independently
revalidates the address at socket connect.

Configuration validation records text/streaming as configured but unprobed and leaves tool/image
capabilities unavailable. Live capability probes must later replace this evidence; setup does not
guess model-specific tool or media support. CLI composition includes Models so setup and recovery
use the same validator as the host.

## Consequences

Four named provider families now share one audited wire implementation without losing routing or
health identity. Credential buffers are cleared on success and failure. Unsupported provider types
fail typed before persistence.

Anthropic uses a different Messages protocol and remains a separate adapter. No compatible profile
is automatically added to the production catalog, and configuration alone performs no model egress.
