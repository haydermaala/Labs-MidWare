# Operational runbooks

- Status: Draft (Phase 11)
- Date: 2026-07-18

Concise response procedures. All assume the **edge continues safely during any
cloud/LIS outage** and that nothing releases results without a validated decision.
Owners are **OPEN** until Phase 0 governance assigns them.

| Condition | Detect | First response | Escalate if |
|-----------|--------|----------------|-------------|
| **Analyzer offline** | no bytes / connection-state metric down | verify cable/port/power; check `TransportStats`; capture-only keeps waiting (no transmit) | offline > SLA or during a run |
| **Port locked / permission** | serial open error; capture stats show read errors | release the other process holding the port; check OS permissions | repeated lock, or unknown holder |
| **Checksum storm** (ASTM) | rising NAK / checksum-failure counters | inspect cabling/baud; the link-layer NAKs and the sender retransmits | storm persists after cabling check |
| **Mapping unresolved** | results held `PendingReview` | route to mapping reviewer; **do not** force-release | volume of held results grows |
| **LIS unavailable** | delivery errors; outbox `pending` grows | edge keeps queuing (store-and-forward); verify LIS/MLLP endpoint | queue age exceeds threshold |
| **Queue growth** | `outbox pending` / age climbing | confirm LIS reachable; check delivery worker; watch disk | growth unbounded |
| **Disk pressure** | low free space | run retention pruning; archive/rotate; **never** drop un-delivered outbox items | space critical |
| **Expired certificate** | cert-expiry metric; auth failures | rotate device/gateway credential; re-enroll if needed | rotation blocked |
| **Failed update** | update client error; version drift | halt the channel; roll back to prior version; verify checksum/provenance | rollback fails |
| **Suspected compromise** | unexpected `SecurityEvent`; tampered driver rejected | isolate the gateway; revoke keys/digests/driver; preserve evidence | confirmed breach → incident response |
| **Lost gateway** | heartbeat gone | mark offline in fleet; investigate site; rotate its credentials | data at risk |
| **Driver rollback** | bad upgrade / discrepancies | `driver-runtime` rollback to prior version; audit records it | discrepancies persist post-rollback |

## Cross-cutting

- **Redaction**: logs, metrics, crash reports, and support bundles never contain
  patient identifiers or result values; metric labels are numeric only.
- **Reconciliation**: acknowledgements are matched to deliveries; unmatched or
  dead-lettered items are reviewed, never silently dropped.
- **Support access** is least-privilege, time-bounded, and audited.
