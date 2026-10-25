CREATE TABLE `login` (
	`Id` INT(32) NOT NULL AUTO_INCREMENT,
	`CreationDate` DATETIME NOT NULL,
	`Email` VARCHAR(64) NOT NULL,
	`FirstName` VARCHAR(32) NOT NULL,
	`LastName` VARCHAR(32) NOT NULL,
	`Username` VARCHAR(32) NOT NULL,
	`Password` VARCHAR(100) NOT NULL,
	`AllowedCharacters` INT(32) NOT NULL DEFAULT '14' COMMENT 'Persisted character slot allowance',
	`Flags` INT(32) NOT NULL DEFAULT '0',
	`AccountFlags` INT(32) NOT NULL DEFAULT '0',
	`Expansions` INT(32) NOT NULL DEFAULT '2047',
	`GM` INT(32) NOT NULL DEFAULT '0',
	PRIMARY KEY (`Id`),
	UNIQUE INDEX `Username` (`Username`)
)
ENGINE=InnoDB DEFAULT CHARSET=latin1;
