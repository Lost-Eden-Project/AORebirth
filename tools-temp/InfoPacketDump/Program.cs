using System;
using System.IO;

using SmokeLounge.AOtomation.Messaging.GameData;
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;
using SmokeLounge.AOtomation.Messaging.Serialization;

var capturePath = @"C:\Users\tyler\OneDrive\Documents\AORebirth\AORebirth\Libraries\Source\AOtomation\AOtomation.Messaging\test\TestData\InfoPacket #1 - 0x40";

byte[] capture = File.ReadAllBytes(capturePath);
Console.WriteLine("capture len=" + capture.Length);
Console.WriteLine("capture " + Convert.ToHexString(capture));

var serializer = new MessageSerializer();
using (var captureStream = new MemoryStream(capture))
{
    Message captured = serializer.Deserialize(captureStream);
    var capturedInfo = (InfoPacketMessage)captured.Body;
    var capturedPacket = (CharacterInfoPacket)capturedInfo.Info;
    Console.WriteLine(
        "capture parsed unknown={0} type={1:X} ident={2}:{3} first='{4}' last='{5}' level={6} prof={7} health={8}/{9} ai={10}",
        capturedInfo.Unknown,
        (int)capturedInfo.Type,
        capturedInfo.Identity.Type,
        capturedInfo.Identity.Instance,
        capturedPacket.FirstName,
        capturedPacket.LastName,
        capturedPacket.Level,
        capturedPacket.Profession,
        capturedPacket.Health,
        capturedPacket.MaxHealth,
        capturedPacket.AiLevel);
}

var built = new InfoPacketMessage
{
    Identity = new Identity { Type = IdentityType.CanbeAffected, Instance = 18 },
    Unknown = 1,
    Type = InfoPacketType.Character,
    Info = new CharacterInfoPacket
    {
        Unknown1 = 0x01,
        Profession = Profession.Soldier,
        Level = 1,
        TitleLevel = 1,
        VisualProfession = Profession.Soldier,
        SideXp = 0,
        Health = 34,
        MaxHealth = 34,
        BreedHostility = 0,
        OrganizationId = null,
        FirstName = "Delmus",
        LastName = string.Empty,
        LegacyTitle = string.Empty,
        Unknown2 = 0,
        OrganizationRank = null,
        TowerFields = null,
        CityPlayfieldId = 0,
        Towers = null,
        InvadersKilled = 0,
        KilledByInvaders = 0,
        AiLevel = 1,
        PvpDuelWins = 0,
        PvpDuelLoses = 0,
        PvpProfessionDuelLoses = 0,
        PvpSoloKills = 0,
        PvpTeamKills = 0,
        PvpSoloScore = 0,
        PvpTeamScore = 0,
        PvpDuelScore = 0
    }
};

var message = new Message
{
    Body = built,
    Header = new Header
    {
        MessageId = 0xDFDF,
        PacketType = built.PacketType,
        Unknown = 0x0001,
        Sender = 954,
        Receiver = 18
    }
};

using var builtStream = new MemoryStream();
serializer.Serialize(builtStream, message);
byte[] builtBytes = builtStream.ToArray();
Console.WriteLine("built len=" + builtBytes.Length);
Console.WriteLine("built " + Convert.ToHexString(builtBytes));
