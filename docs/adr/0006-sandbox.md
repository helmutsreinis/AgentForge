# ADR 0006: Explicit sandbox capability with fail-closed fallback

Status: Accepted

Untrusted work prefers Linux containers/namespaces. Windows uses Job Objects and
available restricted process controls. Every invocation declares mounts, working
directory, environment, network, resources, secrets, timeout, and output bounds.
When the requested isolation cannot be provided, execution returns a typed error; it
does not silently fall back to unrestricted host execution.
