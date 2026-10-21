namespace ZoneEngine_New.Core.Modules
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Threading.Tasks;

    using AORebirth.Modules.World;

    using ZoneEngine_New.Core.Logging;

    /// <summary>
    /// One module's grants from the Modules.json "Capabilities" map. Without an entry everything is allowed (module API
    /// 1.1 behaviour); with one, a member it does not list throws <see cref="UnauthorizedAccessException"/>, logged the
    /// first time per capability.
    /// </summary>
    internal sealed class CapabilityGate
    {
        private readonly string _moduleId;
        private readonly HashSet<string>? _granted;
        private readonly IZoneLogger _logger;
        private readonly ConcurrentDictionary<string, byte> _reported = new(StringComparer.OrdinalIgnoreCase);

        public CapabilityGate(string moduleId, IEnumerable<string>? granted, IZoneLogger logger)
        {
            _moduleId = moduleId;
            _granted = granted == null ? null : new HashSet<string>(granted, StringComparer.OrdinalIgnoreCase);
            _logger = logger;
        }

        public void Require(string capability)
        {
            if (_granted == null || _granted.Contains(capability))
                return;

            string message = "Module " + _moduleId + " is not granted the '" + capability + "' capability (Config/Modules.json Capabilities).";
            if (_reported.TryAdd(capability, 0))
                _logger.Warn(message);
            throw new UnauthorizedAccessException(message);
        }
    }

    internal sealed class GatedWorld(IZoneWorld world, CapabilityGate gate) : IZoneWorld
    {
        public IReadOnlyList<PlayerInfo> Players()
        {
            gate.Require(ModuleCapabilities.ReadWorld);
            return world.Players();
        }

        public PlayerInfo? Player(int characterId)
        {
            gate.Require(ModuleCapabilities.ReadWorld);
            return world.Player(characterId);
        }

        public IReadOnlyList<int> Playfields()
        {
            gate.Require(ModuleCapabilities.ReadWorld);
            return world.Playfields();
        }

        public IReadOnlyList<NpcInfo> Npcs(int playfieldId)
        {
            gate.Require(ModuleCapabilities.ReadWorld);
            return world.Npcs(playfieldId);
        }

        public NpcInfo? Npc(int playfieldId, int instance)
        {
            gate.Require(ModuleCapabilities.ReadWorld);
            return world.Npc(playfieldId, instance);
        }

        public int? Stat(EntityRef entity, int statId)
        {
            gate.Require(ModuleCapabilities.ReadWorld);
            return world.Stat(entity, statId);
        }
    }

    internal sealed class GatedActions(IZoneActions actions, CapabilityGate gate) : IZoneActions
    {
        public Task<bool> RefreshNpc(int playfieldId, int instance)
        {
            gate.Require(ModuleCapabilities.RefreshNpc);
            return actions.RefreshNpc(playfieldId, instance);
        }

        public Task<bool> SendSystemMessage(int characterId, string text)
        {
            gate.Require(ModuleCapabilities.SendMessages);
            return actions.SendSystemMessage(characterId, text);
        }

        public Task<bool> SendFeedback(int characterId, int feedbackId)
        {
            gate.Require(ModuleCapabilities.SendMessages);
            return actions.SendFeedback(characterId, feedbackId);
        }
    }
}
