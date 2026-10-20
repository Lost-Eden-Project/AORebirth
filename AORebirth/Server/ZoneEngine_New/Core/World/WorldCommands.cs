namespace ZoneEngine_New.Core.World
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Threading.Tasks;

    using AORebirth.World.Contracts.V1;

    using Grpc.Core;

    using ZoneEngine_New.Core.Logging;

    /// <summary>
    /// Commands between zone instances, routed by World to whichever instance owns a character, runs a playfield,
    /// or to all of them. Services register a handler by name; handlers run off any tick and dispatch onto the
    /// playfields they touch themselves.
    /// </summary>
    public sealed class WorldCommands
    {
        readonly WorldLink _world;
        readonly IZoneLogger _logger;
        readonly ConcurrentDictionary<string, Action<InstanceCommand>> _handlers = new(StringComparer.Ordinal);

        public WorldCommands(WorldLink world, IZoneLogger logger)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _world.WorldMessageReceived += OnMessage;
        }

        public void Register(string name, Action<InstanceCommand> handler)
        {
            if (!_handlers.TryAdd(name, handler ?? throw new ArgumentNullException(nameof(handler))))
                throw new InvalidOperationException("A handler for " + name + " is already registered.");
        }

        public Task<bool> ToCharacterAsync(int characterId, string name, params string[] arguments)
            => SendAsync(new InstanceCommand { Name = name, CharacterId = characterId }, arguments);

        public Task<bool> ToPlayfieldAsync(int playfieldId, string name, params string[] arguments)
            => SendAsync(new InstanceCommand { Name = name, PlayfieldId = playfieldId }, arguments);

        /// <summary>Every other zone instance.</summary>
        public Task<bool> BroadcastAsync(string name, params string[] arguments)
            => SendAsync(new InstanceCommand { Name = name, Broadcast = true }, arguments);

        async Task<bool> SendAsync(InstanceCommand command, IEnumerable<string> arguments)
        {
            command.Arguments.AddRange(arguments);
            if (!_world.IsServing) return false;
            try
            {
                Ack ack = await _world.Channel.Directory.RouteAsync(command, _world.Channel.Call(TimeSpan.FromSeconds(3)))
                    .ResponseAsync.ConfigureAwait(false);
                return ack.Ok;
            }
            catch (RpcException exception)
            {
                _logger.Warn("World command " + command.Name + " not delivered: " + exception.StatusCode);
                return false;
            }
        }

        void OnMessage(WorldToService message)
        {
            if (message.BodyCase != WorldToService.BodyOneofCase.Command) return;
            InstanceCommand command = message.Command;
            if (!_handlers.TryGetValue(command.Name, out Action<InstanceCommand>? handler))
            {
                _logger.Warn("World command " + command.Name + " from " + command.Origin + " has no handler here");
                return;
            }

            _ = Task.Run(() =>
            {
                try { handler(command); }
                catch (Exception exception) { _logger.Error(exception, "World command " + command.Name + " failed"); }
            });
        }
    }
}
