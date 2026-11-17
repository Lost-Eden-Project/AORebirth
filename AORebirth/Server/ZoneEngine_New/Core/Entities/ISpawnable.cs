namespace ZoneEngine_New.Core.Entities
{
    using LostEden.Protocol.Messages;

    public interface ISpawnable
    {
        MessageBody BuildSpawnMessage();
    }
}
