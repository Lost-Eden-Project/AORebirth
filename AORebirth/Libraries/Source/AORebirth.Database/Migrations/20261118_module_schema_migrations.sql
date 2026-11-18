-- Additive ledger of zone module migrations (module API 1.14). A module ships
-- Modules/<Id>/Migrations/NNNN_name.sql for its own mod_<id>_* tables;
-- DatabaseMigrationTool records each one it applies here with the script's SHA-256,
-- so an edited or missing migration stops the module loading. Kept apart from
-- schema_migrations, whose ledger names only the server's own migrations.
CREATE TABLE IF NOT EXISTS `module_schema_migrations` (
    `ModuleId` VARCHAR(32) NOT NULL,
    `MigrationName` VARCHAR(96) NOT NULL,
    `Sha256` CHAR(64) NOT NULL,
    `AppliedAtUtc` DATETIME(6) NOT NULL,
    PRIMARY KEY (`ModuleId`, `MigrationName`)
)
COMMENT='Zone module migrations applied (module API 1.14): one row per Modules/<Id>/Migrations script'
DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin
ENGINE=InnoDB;
