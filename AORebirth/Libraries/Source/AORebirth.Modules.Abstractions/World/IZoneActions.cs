namespace AORebirth.Modules.World
{
    using System.Threading.Tasks;

    /// <summary>
    /// Things a module may ask this zone instance to do (module API 1.2). Each action is queued onto the playfield tick
    /// that owns its target and runs there; the returned task completes after it ran (or with false when it could not).
    /// No call blocks the caller or takes an engine lock, so these are safe from any thread. Awaiting one inside an event
    /// handler is pointless: the handler already runs on a tick, and the action runs on a later one.
    /// Only this instance's players and NPCs can be reached.
    /// </summary>
    public interface IZoneActions
    {
        /// <summary>
        /// Sends the NPC's spawn extras again to everyone who sees it: its shop pane and whether it talks through a Knubot
        /// script (HasKnubotData 1, or 0 when it no longer does). False when the NPC is not spawned on that playfield.
        /// Needs <c>refresh-npc</c>.
        /// </summary>
        Task<bool> RefreshNpc(int playfieldId, int instance);

        /// <summary>A line of system text in the player's chat window. False when the player is not in play here. Needs <c>send-messages</c>.</summary>
        Task<bool> SendSystemMessage(int characterId, string text);

        /// <summary>A client feedback message by id (the client's text table). False when the player is not in play here. Needs <c>send-messages</c>.</summary>
        Task<bool> SendFeedback(int characterId, int feedbackId);
    }

    /// <summary>Capability names for the "Capabilities" map in Config/Modules.json.</summary>
    public static class ModuleCapabilities
    {
        /// <summary><see cref="IZoneWorld"/>.</summary>
        public const string ReadWorld = "read-world";

        /// <summary><see cref="IZoneActions.RefreshNpc"/>.</summary>
        public const string RefreshNpc = "refresh-npc";

        /// <summary><see cref="IZoneActions.SendSystemMessage"/> and <see cref="IZoneActions.SendFeedback"/>.</summary>
        public const string SendMessages = "send-messages";
    }
}
