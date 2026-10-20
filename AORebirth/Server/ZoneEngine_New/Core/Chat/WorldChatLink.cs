namespace ZoneEngine_New.Core.Chat
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Threading.Tasks;

    using AORebirth.World.Contracts.V1;

    using Grpc.Core;

    using ZoneEngine_New.Core.Logging;
    using ZoneEngine_New.Core.Teams;
    using ZoneEngine_New.Core.World;

    public interface IChatEngineLink : IDisposable
    {
        void Start();

        /// <summary>A command string for ChatEngine about one character (team channels, LFT location).</summary>
        bool SendChatCommand(int characterId, string command);

        /// <summary>Vicinity chat for ChatEngine to deliver to the recipients it names.</summary>
        bool SendVicinity(int senderId, string? senderName, byte messageType, string text, IReadOnlyList<int> recipients);
    }

    /// <summary>
    /// Zone and ChatEngine talk through World: this instance publishes to ChatEngine, and ChatEngine's commands for
    /// a character arrive here when this instance owns it.
    /// </summary>
    public sealed class WorldChatLink : IChatEngineLink
    {
        static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(3);

        readonly WorldLink _world;
        readonly IZoneLogger _logger;
        readonly Lazy<TeamService> _teams;
        bool _started;
        bool _disposed;
        long _lastFailureLogMs;

        public WorldChatLink(WorldLink world, IZoneLogger logger, Lazy<TeamService> teams)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _teams = teams ?? throw new ArgumentNullException(nameof(teams));
        }

        public void Start()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) return;
            _world.WorldMessageReceived += OnWorldMessage;
            _started = true;
        }

        public bool SendChatCommand(int characterId, string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return false;
            return Publish(new ChatEvent { ToCharacter = new CharacterChatCommand { CharacterId = characterId, Command = command } });
        }

        public bool SendVicinity(int senderId, string? senderName, byte messageType, string text, IReadOnlyList<int> recipients)
        {
            var vicinity = new VicinityMessage
            {
                Sender = senderId,
                SenderName = senderName ?? string.Empty,
                ChannelType = messageType,
                Text = text ?? string.Empty
            };
            vicinity.Recipients.AddRange(recipients);
            return Publish(new ChatEvent { Vicinity = vicinity });
        }

        /// <summary>On the registry stream, so lines arrive in the order they were said.</summary>
        bool Publish(ChatEvent message)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_started) return false;
            if (_world.PublishChat(message)) return true;
            long now = Environment.TickCount64;
            if (now - _lastFailureLogMs > 10_000)
            {
                _lastFailureLogMs = now;
                _logger.Warn("Chat not sent: World is unreachable");
            }

            return false;
        }

        void OnWorldMessage(WorldToService message)
        {
            if (message.BodyCase != WorldToService.BodyOneofCase.Chat
                || message.Chat.BodyCase != ChatEvent.BodyOneofCase.ToCharacter) return;
            CharacterChatCommand command = message.Chat.ToCharacter;
            try
            {
                _teams.Value.TryHandleInboundChatCommand(command.CharacterId, command.Command);
            }
            catch (Exception exception)
            {
                _logger.Warn(string.Format(CultureInfo.InvariantCulture, "Inbound chat command failed: {0}", exception.Message));
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_started) _world.WorldMessageReceived -= OnWorldMessage;
        }
    }
}
