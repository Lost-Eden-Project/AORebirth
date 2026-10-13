# Current Task

CHATENGINE CONFIGURATION CLEANUP (2026-10-12).

Mike authorized applying the reviewed ChatEngine XML cleanup to public master,
the derived private Linux repository, his local installation and live configuration.
Keep the eight active ChatEngine settings in a dedicated editable XML template.
Reuse the existing AO_REBIRTH_CONFIG_PATH override and project content-copy rules.
Preserve shared configuration required by LoginEngine, ZoneEngine_New and WebEngine,
explicit caller overrides, local preferences, credentials and unrelated work.

No runtime C#, gameplay, database, authentication or FTP-account changes are in scope.
Validate XML, retained values and configuration wiring. No automated test suites,
binary rebuild, service restart or AO client launch is requested.
