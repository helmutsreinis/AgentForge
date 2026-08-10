# ADR 0001: Modular monolith with enforced boundaries

Status: Accepted

AgentForge begins as one process to keep transactions, recovery, deployment, and
debugging tractable. Domain depends only on the BCL; Abstractions depends on Domain;
feature implementations depend only on both; Host and CLI compose implementations.
Architecture tests enforce the rule. Out-of-process workers are introduced only for
untrusted code, plugins, remote execution, or demonstrated scaling needs.
