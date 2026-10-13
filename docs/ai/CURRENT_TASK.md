# Current Task

MISSION DESTINATION FALLBACK FOR UNCAPTURED TERMINALS (2026-10-13).

PROVEN: live rolls from terminal playfield 566 fail at unobserved QLs because
the existing optional nearest-QL lookup only searches the requested terminal's
recorded pools. The runtime catalog records originating playfields 655 and 800;
playfield 566 has none. Exact captured QLs already work through QL-wide fallback.

Mike approved extending the existing configurable fallback for uncaptured terminal
regions. Preserve exact terminal/QL, QL-wide exact and nearest same-origin ordering.
Only when the terminal has no recorded pools, use the nearest QL-wide observed
pool, choosing the lower QL on ties. Keep requested mission QL, rewards, types,
identities, coordinates, acceptance and DAO behavior unchanged. This is provisional
gameplay policy, not captured origin or cross-QL eligibility. Log it distinctly as
NEAREST_QL_FALLBACK. No new catalogs, configuration system or destination content.

Approved Windows core compilation passed. Original-client verification remains
Mike's responsibility. No automated suites or client launch were requested or run.
Linux consumes the accepted public commit; deployment is separate.
