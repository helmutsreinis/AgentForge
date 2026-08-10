# ADR 0005: Risk-based plugin process boundary

Status: Accepted

Signed, trusted, low-risk adapters may load in-process behind Plugin SDK contracts.
Untrusted, high-risk, or independently scalable plugins run out-of-process through a
versioned constrained protocol. Plugin discovery is not authorization, and no plugin
may increase its declared capability or access raw host secrets.
