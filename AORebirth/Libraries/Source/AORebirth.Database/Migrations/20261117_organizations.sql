-- Organizations: one authoritative store for organizations, their members and their votes.
--
-- Membership used to live only in the Clan (5) and ClanLevel (48) character stats, written back from whichever zone
-- held the character in memory, so a change made on another zone instance could be overwritten. org_members is now the
-- authority; the two stats are a projection the zone sets from it.
--
-- Additive and safe to re-run. The legacy `organizations` table and the stats are left untouched. Organizations and
-- members are copied once, only while org_organizations is empty, keeping each organization id (the client and the chat
-- channels know organizations by it).

CREATE TABLE IF NOT EXISTS `org_organizations` (
    `OrganizationId` INT NOT NULL AUTO_INCREMENT,
    `Name` VARCHAR(40) NOT NULL,
    `GoverningForm` INT NOT NULL DEFAULT 0,
    `LeaderId` INT NOT NULL,
    `Description` VARCHAR(255) NOT NULL DEFAULT '',
    `Objective` VARCHAR(255) NOT NULL DEFAULT '',
    `History` VARCHAR(255) NOT NULL DEFAULT '',
    `Bank` BIGINT NOT NULL DEFAULT 0,
    `Version` BIGINT NOT NULL DEFAULT 1,
    `CreatedAtUtc` DATETIME(6) NOT NULL,
    PRIMARY KEY (`OrganizationId`),
    UNIQUE KEY `UX_org_organizations_name` (`Name`),
    CONSTRAINT `CK_org_organizations_bank` CHECK (`Bank` >= 0),
    CONSTRAINT `CK_org_organizations_version` CHECK (`Version` > 0)
)
COMMENT='Organizations; Name is unique ignoring case; Version grows with every change'
DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS `org_members` (
    `CharacterId` INT NOT NULL,
    `OrganizationId` INT NOT NULL,
    `OrgRank` INT NOT NULL,
    `JoinedAtUtc` DATETIME(6) NOT NULL,
    PRIMARY KEY (`CharacterId`),
    KEY `IX_org_members_organization` (`OrganizationId`, `OrgRank`),
    CONSTRAINT `FK_org_members_organization` FOREIGN KEY (`OrganizationId`)
        REFERENCES `org_organizations` (`OrganizationId`) ON DELETE CASCADE,
    CONSTRAINT `CK_org_members_rank` CHECK (`OrgRank` >= 0)
)
COMMENT='One row per character in an organization (a character belongs to at most one); OrgRank 0 is the top rank'
DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin
ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS `org_votes` (
    `VoteId` BIGINT NOT NULL AUTO_INCREMENT,
    `OrganizationId` INT NOT NULL,
    `Question` VARCHAR(255) NOT NULL,
    `Choices` VARCHAR(1024) NOT NULL,
    `StartedBy` INT NOT NULL,
    `StartedAtUtc` DATETIME(6) NOT NULL,
    `OpensAtUtc` DATETIME(6) NOT NULL,
    `EndsAtUtc` DATETIME(6) NOT NULL,
    `ClosedAtUtc` DATETIME(6) NULL,
    `StopReason` VARCHAR(255) NULL,
    PRIMARY KEY (`VoteId`),
    KEY `IX_org_votes_organization` (`OrganizationId`, `StartedAtUtc`),
    KEY `IX_org_votes_due` (`ClosedAtUtc`, `EndsAtUtc`),
    CONSTRAINT `FK_org_votes_organization` FOREIGN KEY (`OrganizationId`)
        REFERENCES `org_organizations` (`OrganizationId`) ON DELETE CASCADE,
    CONSTRAINT `CK_org_votes_window` CHECK (`OpensAtUtc` >= `StartedAtUtc` AND `EndsAtUtc` > `OpensAtUtc`)
)
COMMENT='Organization votes; Choices are one per line; open while ClosedAtUtc is NULL; StopReason set when the leader stopped it'
DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS `org_vote_ballots` (
    `VoteId` BIGINT NOT NULL,
    `CharacterId` INT NOT NULL,
    `Choice` INT NOT NULL,
    `CastAtUtc` DATETIME(6) NOT NULL,
    PRIMARY KEY (`VoteId`, `CharacterId`),
    CONSTRAINT `FK_org_vote_ballots_vote` FOREIGN KEY (`VoteId`)
        REFERENCES `org_votes` (`VoteId`) ON DELETE CASCADE,
    CONSTRAINT `CK_org_vote_ballots_choice` CHECK (`Choice` >= 1)
)
COMMENT='One ballot per member per vote; it cannot be changed'
DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin
ENGINE=InnoDB;

-- One-time copy from the legacy table, only into an empty org_organizations.
SET @org_copy := (SELECT COUNT(*) = 0 FROM `org_organizations`);

-- Legacy text columns defaulted to the literal '" "'; those become empty. A name used by an older organization (ignoring
-- case and surrounding spaces) gets the organization id appended, and an empty name becomes "Organization <id>".
INSERT INTO `org_organizations`
    (`OrganizationId`, `Name`, `GoverningForm`, `LeaderId`, `Description`, `Objective`, `History`, `Bank`, `Version`, `CreatedAtUtc`)
SELECT
    o.`Id`,
    CASE
        WHEN TRIM(o.`Name`) = '' THEN CONCAT('Organization ', o.`Id`)
        WHEN EXISTS (SELECT 1 FROM `organizations` older
                     WHERE LOWER(TRIM(older.`Name`)) = LOWER(TRIM(o.`Name`)) AND older.`Id` < o.`Id`)
            THEN CONCAT(LEFT(TRIM(o.`Name`), 27), ' (', o.`Id`, ')')
        ELSE LEFT(TRIM(o.`Name`), 40)
    END,
    GREATEST(o.`GovernmentForm`, 0),
    o.`LeaderId`,
    IF(TRIM(o.`Description`) IN ('', '" "', '""'), '', LEFT(o.`Description`, 255)),
    IF(TRIM(o.`Objective`) IN ('', '" "', '""'), '', LEFT(o.`Objective`, 255)),
    IF(TRIM(o.`History`) IN ('', '" "', '""'), '', LEFT(o.`History`, 255)),
    LEAST(o.`Bank`, 4000000000),
    1,
    COALESCE(o.`Creation`, UTC_TIMESTAMP(6))
FROM `organizations` o
WHERE @org_copy = 1;

-- Members: every existing character whose Clan stat names a copied organization, at its ClanLevel rank.
INSERT INTO `org_members` (`CharacterId`, `OrganizationId`, `OrgRank`, `JoinedAtUtc`)
SELECT s.`Instance`, s.`StatValue`, GREATEST(COALESCE(r.`StatValue`, 0), 0), UTC_TIMESTAMP(6)
FROM `stats` s
INNER JOIN `characters` c ON c.`Id` = s.`Instance`
INNER JOIN `org_organizations` o ON o.`OrganizationId` = s.`StatValue`
LEFT JOIN `stats` r ON r.`Type` = 50000 AND r.`Instance` = s.`Instance` AND r.`StatId` = 48
WHERE @org_copy = 1 AND s.`Type` = 50000 AND s.`StatId` = 5 AND s.`StatValue` > 0;

-- A copied organization nobody belongs to any more is not carried over (the legacy row stays where it was).
DELETE o FROM `org_organizations` o
WHERE @org_copy = 1
  AND NOT EXISTS (SELECT 1 FROM `org_members` m WHERE m.`OrganizationId` = o.`OrganizationId`);

-- A leader who is not a member is replaced by the member of the highest rank (lowest number), then the lowest id.
UPDATE `org_organizations` o
SET o.`LeaderId` = (SELECT m.`CharacterId` FROM `org_members` m
                    WHERE m.`OrganizationId` = o.`OrganizationId`
                    ORDER BY m.`OrgRank`, m.`CharacterId` LIMIT 1)
WHERE @org_copy = 1
  AND NOT EXISTS (SELECT 1 FROM `org_members` m
                  WHERE m.`OrganizationId` = o.`OrganizationId` AND m.`CharacterId` = o.`LeaderId`);

-- The leader holds the top rank.
UPDATE `org_members` m
INNER JOIN `org_organizations` o ON o.`OrganizationId` = m.`OrganizationId` AND o.`LeaderId` = m.`CharacterId`
SET m.`OrgRank` = 0
WHERE @org_copy = 1;
