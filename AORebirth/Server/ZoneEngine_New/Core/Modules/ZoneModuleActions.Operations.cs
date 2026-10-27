namespace ZoneEngine_New.Core.Modules
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Threading.Tasks;

    using AORebirth.Core.GameData;
    using AORebirth.Modules.World;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Playfield;

    using CharacterStat = SmokeLounge.AOtomation.Messaging.GameData.CharacterStat;
    using Vector3 = AORebirth.Core.Vector.Vector3;

    /// <summary>
    /// The named operations (module API 1.5). Each registers its name, target kind, parameters, the capability it needs
    /// (its own name) and where it runs: player and NPC operations on the owning playfield tick, account operations on a
    /// worker thread. Every operation goes through the engine's normal paths; none writes packets of its own.
    /// </summary>
    internal sealed partial class ZoneModuleActions
    {
        /// <summary>
        /// The login.Flags value an account ban writes. LoginEngine (CheckLogin) and the account broker admit an account
        /// only while Flags is 0; nothing else writes Flags after an account is created.
        /// </summary>
        internal const int BannedLoginFlags = 1;

        sealed record Operation(OperationDescriptor Descriptor, Func<OperationTarget, OperationArgs, Task<OperationResult>> Run);

        static OperationParameter Required(string name, OperationValueType type, string summary) => new(name, type, true, summary);

        static OperationParameter Optional(string name, OperationValueType type, string summary) => new(name, type, false, summary);

        public IReadOnlyList<OperationDescriptor> Operations => _operations.Values.Select(x => x.Descriptor).ToArray();

        public Task<OperationResult> Invoke(string operation, OperationTarget target, OperationArgs args)
        {
            args ??= OperationArgs.Empty;
            if (string.IsNullOrEmpty(operation) || !_operations.TryGetValue(operation, out Operation? registered))
                return Task.FromResult(OperationResult.Fail(OperationCodes.UnknownOperation, "No operation named '" + operation + "'."));

            OperationDescriptor descriptor = registered.Descriptor;
            if (target.Kind != descriptor.Target)
                return Task.FromResult(OperationResult.Fail(OperationCodes.BadTarget,
                    descriptor.Name + " takes a " + descriptor.Target + " target, not " + target.Kind + "."));

            foreach (OperationParameter parameter in descriptor.Parameters)
            {
                bool present = parameter.Type switch
                {
                    OperationValueType.Int => args.TryGetInt(parameter.Name, out _),
                    OperationValueType.Float => args.TryGetFloat(parameter.Name, out _),
                    OperationValueType.Bool => args.TryGetBool(parameter.Name, out _),
                    _ => args.TryGetString(parameter.Name, out _)
                };
                if (!present && (parameter.Required || args.Has(parameter.Name)))
                    return Task.FromResult(OperationResult.Fail(OperationCodes.BadArgs,
                        descriptor.Name + " needs '" + parameter.Name + "' as " + parameter.Type + "."));
            }

            return registered.Run(target, args);
        }

        Dictionary<string, Operation> BuildOperations()
        {
            var operations = new Dictionary<string, Operation>(StringComparer.OrdinalIgnoreCase);
            void Add(string name, TargetKind target, string summary, OperationParameter[] parameters,
                Func<OperationTarget, OperationArgs, Task<OperationResult>> run)
                => operations.Add(name, new Operation(new OperationDescriptor(name, target, parameters, name, summary), run));

            Add(OperationNames.NpcRefresh, TargetKind.Npc,
                "Sends the NPC's spawn extras again to everyone who sees it.", [],
                async (target, _) => Done(await RefreshNpc(target.PlayfieldId, target.Id).ConfigureAwait(false),
                    "The NPC is not spawned on that playfield."));
            Add(OperationNames.NpcRespawn, TargetKind.Npc,
                "Removes the NPC as if it died (no corpse, loot or XP) and respawns its spawn point now.", [],
                async (target, _) => Done(await RespawnNpc(target.PlayfieldId, target.Id).ConfigureAwait(false),
                    "The NPC is not spawned there, or has no spawn point that respawns."));
            Add(OperationNames.PlayerMessage, TargetKind.Character,
                "A line of system text in the player's chat window.",
                [Required("text", OperationValueType.String, "The text.")],
                async (target, args) => Done(await SendSystemMessage(target.Id, args.GetString("text")).ConfigureAwait(false),
                    "The player is not in play here."));
            Add(OperationNames.PlayerFeedback, TargetKind.Character,
                "A client feedback message by id.",
                [Required("id", OperationValueType.Int, "The feedback id.")],
                async (target, args) => Done(await SendFeedback(target.Id, args.TryGetInt("id", out int id) ? id : 0)
                    .ConfigureAwait(false), "The player is not in play here."));
            Add(OperationNames.PlayerTeleport, TargetKind.Character,
                "Moves the player: within its playfield, or to another one (on this instance or another).",
                [
                    Required("playfield", OperationValueType.Int, "The destination playfield id."),
                    Required("x", OperationValueType.Float, "East-west."),
                    Required("y", OperationValueType.Float, "Height."),
                    Required("z", OperationValueType.Float, "North-south.")
                ],
                Teleport);
            Add(OperationNames.PlayerKick, TargetKind.Character,
                "Logs the player out as a client logout does, with the normal logout save.",
                [Optional("reason", OperationValueType.String, "For the log.")],
                Kick);
            if (_accounts != null)
            {
                Add(OperationNames.AccountBan, TargetKind.Account,
                    "Blocks the account from logging in (login.Flags). Characters in play stay until they log out or are kicked.",
                    [Optional("reason", OperationValueType.String, "For the log.")],
                    Ban);
            }

            return operations;
        }

        static OperationResult Done(bool done, string notHere)
            => done ? OperationResult.Success() : OperationResult.Fail(OperationCodes.NotHere, notHere);

        Task<OperationResult> Teleport(OperationTarget target, OperationArgs args)
        {
            args.TryGetInt("playfield", out int playfieldId);
            args.TryGetFloat("x", out float x);
            args.TryGetFloat("y", out float y);
            args.TryGetFloat("z", out float z);
            if (playfieldId <= 0 || !float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
                return Task.FromResult(OperationResult.Fail(OperationCodes.BadArgs, "A playfield id and finite coordinates are needed."));
            if (_gameDataRoot != null
                && !Directory.Exists(Path.Combine(_gameDataRoot, GameDataPaths.PlayfieldRelativeDirectory(playfieldId))))
                return Task.FromResult(OperationResult.Fail(OperationCodes.BadArgs, "Unknown playfield " + playfieldId + "."));

            var landing = new Vector3(x, y, z);
            return OnPlayer(target.Id, player =>
            {
                if (player.Playfield is not Playfield current)
                    return OperationResult.Fail(OperationCodes.NotHere, "The player is not on a playfield.");

                // Same playfield: a soft teleport, as .tp does.
                if (current.Identity.Instance == playfieldId)
                {
                    player.TeleportWithinPlayfield(landing);
                    return OperationResult.Success();
                }

                if (player.Session == null)
                    return OperationResult.Fail(OperationCodes.NotHere, "The player has no connection.");

                // Another playfield: no exit-proxy return to come back through, as a GM summon from another instance.
                player.Stats.Set(CharacterStat.ExternalPlayfieldInstance, 0, StatDetail.Base, dirty: true);
                player.Stats.Set(CharacterStat.ExternalDoorInstance, 0, StatDetail.Base, dirty: true);
                bool moving = _manager.Value.WithPlayfield(playfieldId, player,
                    destination => player.Session?.TransferToPlayfield(destination, landing),
                    new ZoneEngine_New.Core.World.RemoteArrival(landing, Kind: AORebirth.World.Contracts.V1.ArrivalKind.ArrivalTeleport));
                return moving
                    ? OperationResult.Success()
                    : OperationResult.Fail(OperationCodes.Failed, "The player is already changing playfield.");
            }, OperationResult.Fail(OperationCodes.NotHere, "The player is not in play here."));
        }

        Task<OperationResult> Kick(OperationTarget target, OperationArgs args)
        {
            string reason = args.GetString("reason");
            return OnPlayer(target.Id, player =>
            {
                if (player.Playfield is not Playfield playfield)
                    return OperationResult.Fail(OperationCodes.NotHere, "The player is not on a playfield.");

                _logger?.Info(string.Format(CultureInfo.InvariantCulture,
                    "Module operation player.kick character={0} reason={1}", player.Identity.Instance, reason));
                playfield.GetRequiredService<SpawnService>().LogoutPlayer(player);
                return OperationResult.Success();
            }, OperationResult.Fail(OperationCodes.NotHere, "The player is not in play here."));
        }

        Task<OperationResult> Ban(OperationTarget target, OperationArgs args)
        {
            string account = target.Account?.Trim() ?? string.Empty;
            if (account.Length == 0)
                return Task.FromResult(OperationResult.Fail(OperationCodes.BadArgs, "An account name is needed."));

            string reason = args.GetString("reason");
            Func<ZoneEngine_New.Core.Data.ILoginFlagsRepository> accounts = _accounts!;
            return Task.Run(() =>
            {
                int rows;
                try
                {
                    rows = accounts().SetLoginFlags(account, BannedLoginFlags);
                }
                catch (Exception exception)
                {
                    _logger?.Error(exception, "Module operation account.ban failed for account " + account + ".");
                    return OperationResult.Fail(OperationCodes.Failed, "The account could not be updated.");
                }

                if (rows == 0)
                    return OperationResult.Fail(OperationCodes.Failed, "No account named '" + account + "'.");

                _logger?.Warn(string.Format(CultureInfo.InvariantCulture,
                    "Module operation account.ban account={0} reason={1}", account, reason));
                return OperationResult.Success();
            });
        }
    }
}
