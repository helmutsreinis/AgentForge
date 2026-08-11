# ADR 0019: Credential-Free OpenAI-Compatible Adapter

## Status

Accepted on 2026-08-11.

## Context

AgentForge needs to exercise remote local OpenAI-compatible servers through the same
provider-neutral streaming boundary used by deterministic fixtures. A generic HTTP adapter
introduces hostile request/response parsing, endpoint redirection, unbounded streams,
provider-specific fields, error-body leakage, tool substitution, and plaintext transport
risks. Hosted credentials and arbitrary model context cannot cross this boundary before
their separate security gates.

## Decision

Implement the first compatible adapter inside `AgentForge.Models` without a vendor SDK. It
accepts only a normalized `openai-compatible` descriptor, an exact chat-completions URI,
explicit byte/time bounds and the harness clock. Public construction owns a transport with
redirects, cookies, proxies, and automatic decompression disabled; caller-owned clients and
arbitrary headers are not accepted. Only the unit-test assembly can inject a fake message
handler. HTTPS is the default; local/LAN HTTP requires an explicit option. URI user info,
query, fragment, redirected successful responses, and available media claims fail closed.

The adapter is credential-free. It translates typed messages, prior tool calls/results,
tool schemas, structured-output formats, sampling, and request limits to bounded JSON. Tool
result content includes an `is_error` flag and parsed result value so vendor transport does
not erase harness semantics. It requests SSE and validates content type, declared/observed
size, line/event bounds, strict UTF-8, JSON depth and unique keys, exact choice/tool identity,
tool arguments, structured output, usage, finish reason, event budget, and cancellation.
Remote bodies and exception text are never exposed as provider messages.

The adapter is not registered in production DI and has no control-plane route. A checked-in
integration test is skipped unless exact endpoint/model environment variables are present;
it constructs the public adapter with one fixed non-secret prompt. The initial live gate
targets the operator-authorized `qwen3.6` LAN endpoint with plaintext transport and thinking
disabled.

## Consequences

- AC-08 has real adapter evidence without making model invocation generally available.
- Local compatible-server behavior can be tested without provider credentials or SDK types.
- Media cannot be dropped because compatible adapter creation rejects media capability
  evidence until an artifact resolver and media transport gate exist.
- Redirect, proxy/DNS policy, credential materialization, context redaction, routing,
  failover, audit, durable snapshots, and cost accounting remain closed gates.
- Hosted OpenAI, Anthropic, and DeepSeek adapters may reuse harness records but cannot add
  arbitrary headers or bypass the later context/credential invocation service.
