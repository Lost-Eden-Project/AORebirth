namespace ZoneEngine_New.Core.Modules
{
    using AORebirth.Modules.Events;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Playfield;

    /// <summary>
    /// Module 1.2 lifecycle events, raised from the playfield tick. Without a loaded module the playfield has no
    /// <see cref="ZoneModuleEvents"/>; with no subscriber nothing is built.
    /// </summary>
    internal static class ModuleHooks
    {
        public static void PlayerEntered(Playfield playfield, Player player)
        {
            if (Events(playfield) is { PlayerEnteredPlayfield.HasHandlers: true } events)
                events.PlayerEnteredPlayfield.Raise(new PlayerPlayfieldEvent
                {
                    CharacterId = player.Identity.Instance, PlayfieldId = playfield.Identity.Instance
                });
        }

        public static void PlayerLeft(Playfield playfield, Player player)
        {
            if (Events(playfield) is { PlayerLeftPlayfield.HasHandlers: true } events)
                events.PlayerLeftPlayfield.Raise(new PlayerPlayfieldEvent
                {
                    CharacterId = player.Identity.Instance, PlayfieldId = playfield.Identity.Instance
                });
        }

        public static void PlayerLoggedIn(Playfield playfield, Player player)
        {
            if (Events(playfield) is { PlayerLoggedIn.HasHandlers: true } events)
                events.PlayerLoggedIn.Raise(new PlayerSessionEvent
                {
                    CharacterId = player.Identity.Instance, PlayfieldId = playfield.Identity.Instance
                });
        }

        public static void PlayerLoggedOut(Playfield playfield, int characterId)
        {
            if (Events(playfield) is { PlayerLoggedOut.HasHandlers: true } events)
                events.PlayerLoggedOut.Raise(new PlayerSessionEvent
                {
                    CharacterId = characterId, PlayfieldId = playfield.Identity.Instance
                });
        }

        public static void NpcSpawned(Playfield playfield, NpcCharacter npc)
        {
            if (Events(playfield) is { NpcSpawned.HasHandlers: true } events)
                events.NpcSpawned.Raise(Spawn(playfield, npc));
        }

        public static void NpcDespawned(Playfield playfield, NpcCharacter npc)
        {
            if (Events(playfield) is { NpcDespawned.HasHandlers: true } events)
                events.NpcDespawned.Raise(Spawn(playfield, npc));
        }

        public static void KnubotChatOpened(Playfield playfield, Player player, NpcCharacter npc, string scriptId)
        {
            if (Events(playfield) is { KnubotChatOpened.HasHandlers: true } events)
                events.KnubotChatOpened.Raise(new KnubotChatOpenedEvent
                {
                    PlayfieldId = playfield.Identity.Instance,
                    CharacterId = player.Identity.Instance,
                    NpcInstance = npc.Identity.Instance,
                    ScriptId = scriptId
                });
        }

        static ZoneModuleEvents? Events(Playfield playfield) => playfield.GetService<ZoneModuleEvents>();

        static NpcSpawnEvent Spawn(Playfield playfield, NpcCharacter npc) => new()
        {
            PlayfieldId = playfield.Identity.Instance,
            NpcInstance = npc.Identity.Instance,
            NpcHash = npc.MobTemplate?.Hash ?? string.Empty
        };
    }
}
