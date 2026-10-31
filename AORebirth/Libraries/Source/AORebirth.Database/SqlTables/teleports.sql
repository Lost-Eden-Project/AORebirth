CREATE TABLE IF NOT EXISTS `teleports` (
  `Id` int(10) NOT NULL AUTO_INCREMENT,
  `playfield` int(10) NOT NULL DEFAULT '0',
  `statelType` int(15) NOT NULL DEFAULT '0',
  `statelInstance` int(15) unsigned NOT NULL DEFAULT '0',
  `destinationPlayfield` int(15) NOT NULL DEFAULT '0',
  `destinationType` int(15) NOT NULL DEFAULT '0',
  `destinationInstance` int(15) unsigned NOT NULL DEFAULT '0',
  PRIMARY KEY (`Id`)
) ENGINE=InnoDB AUTO_INCREMENT=1256 DEFAULT CHARSET=latin1 COLLATE=latin1_general_ci;
