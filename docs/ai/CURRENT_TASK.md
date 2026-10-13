# Current Task

VICINITY CHAT INTERNAL CONNECTION REPAIR (2026-10-12).

Mike authorized fixing the observed live vicinity-chat failure. The existing
ZoneEngine_New ISCom link used the public ChatIP even when ChatEngine's internal
listener used a different address. Prefer the existing ISCommLocalIP setting for
that internal connection, preserving ChatIP and loopback as the prior fallback.
Publish the same-host loopback setting in shared Config.xml and Config.example.xml.

Preserve client ChatServerInfo advertisement, the dedicated eight-setting ChatEngine
template, communication port, message routing and unrelated systems. Build public
Windows source first, then reconcile that exact public source into the private Linux
build and prepare its package. Deployment requires the documented package approval.
Keep private deployment details private.

Approved Windows core compilation, XML validation and the secret scan passed.
Linux package and live connection verification are pending. Mike performs original-client
acceptance. No automated test suites or AO client launch were requested.
