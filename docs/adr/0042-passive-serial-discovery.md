# ADR 0042: Passive serial discovery and capability separation

Status: Accepted

## Decision

Place device support in an independent `AgentForge.Devices` module. Discover Windows serial endpoints through the
read-only serial registry map and Linux endpoints through sysfs metadata and device-node existence. Discovery must not
reference a serial-port open API, file stream, write primitive, shell, or process API. Hash stable hardware evidence into
a physical device ID independently from the current endpoint and emit typed attach, detach, re-enumeration, and readiness
changes. Use an explicit typed profile whose conservative default leaves DTR and RTS disabled.

Represent inventory, capture, read, write, command, calibration, firmware, and privileged operations as separate exact,
expiring capabilities. Missing capability is denial; no capability implies another.

## Consequences

Inventory can report unknown or permission-required readiness without probing the device. Some platforms may provide only
weaker stable evidence; that weakness remains visible in the descriptor. A later explicit session adapter may open a port
only after the exact operation gate and must fail typed when isolation or permission is unavailable.
