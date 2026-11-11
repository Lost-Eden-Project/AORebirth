namespace ChatEngine.Modules
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Security.Cryptography;
    using System.Text.Json;
    using System.Threading;

    using AORebirth.ChatModules;
    using AORebirth.ChatModules.Channels;
    using AORebirth.Core.GameData;

    using ChatEngine.Channels;
    using ChatEngine.CoreClient;
    using ChatEngine.CoreServer;
    using ChatEngine.Packets;

    using NLog;

    /// <summary>
    /// Loads the external chat modules named in Config/ChatModules.json once at startup, each into its own load context,
    /// and connects them to the channels: modules see what is said in channels (to relay it out of the game) and post into
    /// channels (to relay outside text in), and may claim channel commands such as !level that are hidden from the channel
    /// and answered privately. With no ChatModules.json nothing is loaded and every hook is a no-op.
    /// Modules are trusted code running in the chat process; there is no unloading or hot reload.
    /// </summary>
    internal sealed class ChatModuleHost
    {
        internal const int MaxSenderNameLength = 64;

        internal const int MaxTextLength = 1024;

        /// <summary>Distinct module sender names kept for the life of the process.</summary>
        internal const int MaxSenderIdentities = 65536;

        /// <summary>Longest command name after its prefix character.</summary>
        internal const int MaxCommandNameLength = 32;

        /// <summary>
        /// Module senders get chat wire ids from here up; BotService bots start at 0xE0000000, so the two ranges cannot meet
        /// within <see cref="MaxSenderIdentities"/>.
        /// </summary>
        private const uint FirstSenderWireId = 0xD0000000;

        private static readonly int ApiMajorVersion = typeof(IChatModule).Assembly.GetName().Version?.Major ?? 0;

        private static readonly Logger Log = LogManager.GetLogger("ChatModules");

        private static readonly TimeSpan DeliveryStopTimeout = TimeSpan.FromSeconds(2);

        private readonly ChatServer server;

        private readonly List<LoadedModule> modules = new List<LoadedModule>();

        private readonly object senderSync = new object();

        private readonly Dictionary<string, Sender> senders = new Dictionary<string, Sender>(StringComparer.OrdinalIgnoreCase);

        private Dictionary<string, List<string>> capabilities =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        private ChatModuleDelivery[] deliveries = Array.Empty<ChatModuleDelivery>();

        private uint nextSenderWireId = FirstSenderWireId;

        /// <summary>Built during load, before the listener opens, then only read; swapped whole.</summary>
        private CommandTable commandTable = CommandTable.Empty;

        private ChatModuleHost(ChatServer server)
        {
            this.server = server;
        }

        public int Count => this.modules.Count;

        public static ChatModuleHost Load(ChatServer server)
        {
            if (server == null)
            {
                throw new ArgumentNullException("server");
            }

            var host = new ChatModuleHost(server);
            string settingsPath = ChatModulesSettings.ResolvePath(GameDataPaths.ResolveRuntimeRoot());
            if (settingsPath == null)
            {
                Log.Info("Chat modules: no " + ChatModulesSettings.ConfigFileName + "; no modules loaded.");
                return host;
            }

            ChatModulesSettings settings = ChatModulesSettings.Load(settingsPath);
            string modulesDirectory = settings.ResolveModulesDirectory(settingsPath);
            var required = new HashSet<string>(settings.Required ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            host.capabilities = LoadCapabilities(settings);
            Log.Info("Chat modules: config=" + settingsPath + " directory=" + modulesDirectory + " enabled="
                + (settings.Enabled?.Count ?? 0));

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string id in settings.Enabled ?? new List<string>())
            {
                if (string.IsNullOrWhiteSpace(id) || !seen.Add(id))
                {
                    continue;
                }

                try
                {
                    host.LoadModule(id, modulesDirectory);
                }
                catch (Exception exception)
                {
                    Log.Error(exception, "Chat module " + id + " failed to load.");
                    if (required.Contains(id))
                    {
                        host.Shutdown();
                        throw new InvalidOperationException("Required chat module " + id + " failed to load.", exception);
                    }
                }
            }

            return host;
        }

        /// <summary>A character said <paramref name="text"/> in <paramref name="channel"/>. Called on the client's network thread.</summary>
        public void PublishPlayerMessage(ChannelBase channel, uint characterId, string characterName, string text)
        {
            this.Publish(channel, ChatMessageSource.Player, characterId, characterName, text, null);
        }

        /// <summary>
        /// When <paramref name="text"/> starts with a module's registered command, queues it to that module and returns true:
        /// the caller then shows the line to nobody. Called on the client's network thread; runs no module code.
        /// </summary>
        public bool TryConsumeCommand(ChannelBase channel, uint characterId, string characterName, int gmLevel, string text)
        {
            CommandTable table = Volatile.Read(ref this.commandTable);
            if (table.Commands.Count == 0 || channel == null || string.IsNullOrEmpty(text)
                || table.Prefixes.IndexOf(text[0]) < 0)
            {
                return false;
            }

            int end = 1;
            while (end < text.Length && !char.IsWhiteSpace(text[end]))
            {
                end++;
            }

            CommandEntry entry;
            if (end - 1 > MaxCommandNameLength || !table.Commands.TryGetValue(text.Substring(0, end), out entry))
            {
                return false;
            }

            var command = new ModuleCommand(this, Describe(channel), characterId, characterName, gmLevel, entry.Name,
                text.Substring(end).Trim(), text, DateTime.UtcNow);
            if (!entry.Delivery.EnqueueCommand(
                    entry.Name,
                    () => entry.Handler(command),
                    () => this.SendSystem(characterId, entry.Name + " failed.")))
            {
                this.SendSystem(characterId, entry.Name + " is busy; try again in a moment.");
            }

            return true;
        }

        /// <summary>A BotService bot said <paramref name="text"/> in <paramref name="channel"/>.</summary>
        public void PublishBotMessage(ChannelBase channel, uint botWireId, string botName, string text)
        {
            this.Publish(channel, ChatMessageSource.Bot, botWireId, botName, text, null);
        }

        public void Shutdown()
        {
            ChatModuleDelivery[] stopping = this.deliveries;
            this.deliveries = Array.Empty<ChatModuleDelivery>();
            foreach (ChatModuleDelivery delivery in stopping)
            {
                delivery.Stop(DeliveryStopTimeout);
            }

            for (int i = this.modules.Count - 1; i >= 0; i--)
            {
                ShutdownEntries(this.modules[i].Id, this.modules[i].Entries);
            }

            this.modules.Clear();
        }

        private static void ShutdownEntries(string moduleId, IEnumerable<IChatModule> entries)
        {
            foreach (IChatModule entry in entries)
            {
                try
                {
                    entry.Shutdown();
                }
                catch (Exception exception)
                {
                    Log.Error(exception, "Chat module " + moduleId + " failed to shut down.");
                }
            }
        }

        private static Dictionary<string, List<string>> LoadCapabilities(ChatModulesSettings settings)
        {
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ChatModuleCapabilities.ReadChannels,
                ChatModuleCapabilities.PostChannels,
                ChatModuleCapabilities.ConsumeCommands,
            };
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (settings.Capabilities == null)
            {
                return result;
            }

            foreach (KeyValuePair<string, List<string>> entry in settings.Capabilities)
            {
                var granted = new List<string>();
                foreach (string name in entry.Value ?? new List<string>())
                {
                    if (known.Contains(name))
                    {
                        granted.Add(name);
                    }
                    else
                    {
                        Log.Warn("Chat modules: unknown capability '" + name + "' for module " + entry.Key + " ignored.");
                    }
                }

                result[entry.Key] = granted;
                Log.Info("Chat modules: " + entry.Key + " capabilities=" + (granted.Count == 0 ? "none" : string.Join(",", granted)));
            }

            return result;
        }

        private static void CheckApiVersion(Assembly assembly)
        {
            AssemblyName api = assembly.GetReferencedAssemblies().FirstOrDefault(reference =>
                string.Equals(reference.Name, ChatModuleLoadContext.SharedAssemblyName, StringComparison.OrdinalIgnoreCase));
            if (api == null)
            {
                throw new InvalidDataException("Chat module does not reference " + ChatModuleLoadContext.SharedAssemblyName + ".");
            }

            if ((api.Version?.Major ?? -1) != ApiMajorVersion)
            {
                throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                    "Chat module was built against chat module API {0}; this engine provides {1}.", api.Version, ApiMajorVersion));
            }
        }

        private static ChatChannelInfo Describe(ChannelBase channel)
        {
            return new ChatChannelInfo(
                (byte)channel.channelType,
                channel.channelType.ToString(),
                channel.ChannelId,
                channel.ChannelName);
        }

        private static bool IsValidSenderName(string name)
        {
            if (name.Length == 0 || name.Length > MaxSenderNameLength)
            {
                return false;
            }

            foreach (char c in name)
            {
                if (char.IsControl(c))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>One prefix character (punctuation or symbol, not '.' or '/'), then 1 to 32 letters, digits, '-' or '_'.</summary>
        private static bool IsValidCommandName(string name)
        {
            if (name == null || name.Length < 2 || name.Length > MaxCommandNameLength + 1)
            {
                return false;
            }

            char prefix = name[0];
            if (prefix == '.' || prefix == '/' || !(char.IsPunctuation(prefix) || char.IsSymbol(prefix)))
            {
                return false;
            }

            for (int i = 1; i < name.Length; i++)
            {
                char c = name[i];
                if (!(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_'))
                {
                    return false;
                }
            }

            return true;
        }

        private void LoadModule(string id, string modulesDirectory)
        {
            string moduleDirectory = Path.GetFullPath(Path.Combine(modulesDirectory, id));
            string manifestPath = Path.Combine(moduleDirectory, ChatModuleManifest.FileName);
            if (!File.Exists(manifestPath))
            {
                throw new FileNotFoundException("Chat module manifest not found.", manifestPath);
            }

            ChatModuleManifest manifest;
            using (FileStream stream = File.OpenRead(manifestPath))
            {
                manifest = JsonSerializer.Deserialize<ChatModuleManifest>(stream, ChatModulesSettings.JsonOptions)
                    ?? throw new InvalidDataException("Empty chat module manifest: " + manifestPath);
            }

            if (!string.Equals(manifest.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Chat module manifest id '" + manifest.Id + "' does not match enabled id '" + id + "'.");
            }

            if (string.IsNullOrWhiteSpace(manifest.Assembly))
            {
                throw new InvalidDataException("Chat module manifest names no assembly: " + manifestPath);
            }

            string assemblyPath = Path.GetFullPath(Path.Combine(moduleDirectory, manifest.Assembly));
            if (!assemblyPath.StartsWith(moduleDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Chat module assembly must be inside the module folder: " + manifest.Assembly);
            }

            if (!File.Exists(assemblyPath))
            {
                throw new FileNotFoundException("Chat module assembly not found.", assemblyPath);
            }

            var loadContext = new ChatModuleLoadContext(manifest.Id, assemblyPath);
            Assembly assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
            CheckApiVersion(assembly);

            Type[] entryTypes = assembly.GetExportedTypes()
                .Where(type => type.IsClass && !type.IsAbstract && typeof(IChatModule).IsAssignableFrom(type))
                .ToArray();
            if (entryTypes.Length == 0)
            {
                throw new InvalidDataException("Chat module assembly has no public IChatModule: " + assemblyPath);
            }

            List<string> granted;
            this.capabilities.TryGetValue(manifest.Id, out granted);
            var delivery = new ChatModuleDelivery(manifest.Id, Log);
            var context = new ModuleContext(this, manifest.Id, moduleDirectory, new Gate(manifest.Id, granted), delivery);
            var entries = new List<IChatModule>(entryTypes.Length);
            try
            {
                foreach (Type type in entryTypes)
                {
                    var entry = (IChatModule)(Activator.CreateInstance(type)
                        ?? throw new InvalidOperationException("Could not create " + type.FullName));
                    entry.Initialize(context);
                    entries.Add(entry);
                }
            }
            catch
            {
                context.View.EndRegistration();
                delivery.Stop(DeliveryStopTimeout);
                ShutdownEntries(manifest.Id, entries);
                throw;
            }

            List<CommandEntry> registered = context.View.EndRegistration();
            if (registered.Count > 0)
            {
                CommandTable table = this.commandTable;
                var commands = new Dictionary<string, CommandEntry>(table.Commands, StringComparer.OrdinalIgnoreCase);
                foreach (CommandEntry command in registered)
                {
                    commands.Add(command.Name, command);
                }

                Volatile.Write(ref this.commandTable, new CommandTable(commands));
                delivery.EnsureStarted();
                Log.Info("Chat module " + manifest.Id + " commands: " + string.Join(", ", registered.Select(x => x.Name)));
            }

            this.modules.Add(new LoadedModule(manifest.Id, entries));
            ChatModuleDelivery[] current = this.deliveries;
            var next = new ChatModuleDelivery[current.Length + 1];
            Array.Copy(current, next, current.Length);
            next[current.Length] = delivery;
            this.deliveries = next;

            Log.Info(string.Format(CultureInfo.InvariantCulture,
                "Chat module loaded id={0} version={1} assembly={2} sha256={3} entries={4}",
                manifest.Id,
                string.IsNullOrWhiteSpace(manifest.Version) ? "?" : manifest.Version,
                Path.GetFileName(assemblyPath),
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assemblyPath))),
                entries.Count));
        }

        private void Publish(ChannelBase channel, ChatMessageSource source, uint senderId, string senderName, string text,
            string sourceModuleId)
        {
            if (channel == null)
            {
                return;
            }

            ChatModuleDelivery[] targets = this.deliveries;
            ChatChannelMessage message = null;
            foreach (ChatModuleDelivery delivery in targets)
            {
                if (!delivery.HasSubscribers
                    || string.Equals(delivery.ModuleId, sourceModuleId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Built only when some other module listens.
                message = message ?? new ChatChannelMessage(
                    Describe(channel), source, senderId, senderName, text, DateTime.UtcNow, sourceModuleId);
                delivery.Enqueue(message);
            }
        }

        private ChannelBase FindChannel(byte type, uint id)
        {
            lock (this.server.Channels)
            {
                foreach (ChannelBase channel in this.server.Channels)
                {
                    if ((byte)channel.channelType == type && channel.ChannelId == id)
                    {
                        return channel;
                    }
                }
            }

            return null;
        }

        private IReadOnlyList<ChatChannelInfo> ListChannels()
        {
            ChannelBase[] snapshot;
            lock (this.server.Channels)
            {
                snapshot = this.server.Channels.ToArray();
            }

            var result = new ChatChannelInfo[snapshot.Length];
            for (int i = 0; i < snapshot.Length; i++)
            {
                result[i] = Describe(snapshot[i]);
            }

            return result;
        }

        private ChatPostResult Post(string moduleId, byte type, uint id, string senderName, string text)
        {
            string name = (senderName ?? string.Empty).Trim();
            if (!IsValidSenderName(name))
            {
                return ChatPostResult.InvalidSenderName;
            }

            if (string.IsNullOrEmpty(text) || text.Length > MaxTextLength)
            {
                return ChatPostResult.InvalidText;
            }

            ChannelBase channel = this.FindChannel(type, id);
            if (channel == null)
            {
                return ChatPostResult.ChannelNotFound;
            }

            Sender sender;
            if (!this.TryGetSender(name, out sender))
            {
                return ChatPostResult.SenderLimitReached;
            }

            channel.BotMessage(sender.WireId, sender.Name, text);
            ChatLogger.WriteString(channel.ChannelName, text, sender.Name + " [module " + moduleId + "]");
            this.Publish(channel, ChatMessageSource.Module, sender.WireId, sender.Name, text, moduleId);
            return ChatPostResult.Posted;
        }

        /// <summary>A tell from a module sender to one online character, for <see cref="IChatCommand.ReplyPrivate"/>.</summary>
        private ChatPostResult Tell(uint characterId, string senderName, string text)
        {
            string name = (senderName ?? string.Empty).Trim();
            if (!IsValidSenderName(name))
            {
                return ChatPostResult.InvalidSenderName;
            }

            if (string.IsNullOrEmpty(text) || text.Length > MaxTextLength)
            {
                return ChatPostResult.InvalidText;
            }

            Client client;
            if (!this.server.ConnectedClients.TryGetValue(characterId, out client) || client == null)
            {
                return ChatPostResult.RecipientOffline;
            }

            Sender sender;
            if (!this.TryGetSender(name, out sender))
            {
                return ChatPostResult.SenderLimitReached;
            }

            if (!client.KnownClients.Contains(sender.WireId))
            {
                client.Send(NameLookupResult.Create(sender.WireId, sender.Name));
                client.KnownClients.Add(sender.WireId);
            }

            client.Send(MsgPrivate.Create(sender.WireId, text));
            return ChatPostResult.Posted;
        }

        private void SendSystem(uint characterId, string text)
        {
            Client client;
            if (this.server.ConnectedClients.TryGetValue(characterId, out client) && client != null)
            {
                client.Send(MsgSystem.Create(text));
            }
        }

        /// <summary>
        /// One stable wire id per sender name: clients cache the name per id for the session, so an id must never change
        /// name. The first spelling seen is the one shown.
        /// </summary>
        private bool TryGetSender(string name, out Sender sender)
        {
            lock (this.senderSync)
            {
                if (this.senders.TryGetValue(name, out sender))
                {
                    return true;
                }

                if (this.senders.Count >= MaxSenderIdentities)
                {
                    return false;
                }

                uint wireId;
                do
                {
                    wireId = this.nextSenderWireId++;
                }
                while (this.server.ConnectedClients.ContainsKey(wireId));

                sender = new Sender(wireId, name);
                this.senders[name] = sender;
                return true;
            }
        }

        private sealed class Sender
        {
            public Sender(uint wireId, string name)
            {
                this.WireId = wireId;
                this.Name = name;
            }

            public uint WireId { get; }

            public string Name { get; }
        }

        private sealed class CommandTable
        {
            public static readonly CommandTable Empty =
                new CommandTable(new Dictionary<string, CommandEntry>(StringComparer.OrdinalIgnoreCase));

            public CommandTable(Dictionary<string, CommandEntry> commands)
            {
                this.Commands = commands;
                this.Prefixes = new string(commands.Keys.Select(x => x[0]).Distinct().ToArray());
            }

            public Dictionary<string, CommandEntry> Commands { get; }

            /// <summary>The distinct first characters of the names: most lines are rejected on this alone.</summary>
            public string Prefixes { get; }
        }

        private sealed class CommandEntry
        {
            public CommandEntry(string name, Action<IChatCommand> handler, ChatModuleDelivery delivery)
            {
                this.Name = name;
                this.Handler = handler;
                this.Delivery = delivery;
            }

            public string Name { get; }

            public Action<IChatCommand> Handler { get; }

            public ChatModuleDelivery Delivery { get; }
        }

        private sealed class ModuleCommand : IChatCommand
        {
            private readonly ChatModuleHost host;

            public ModuleCommand(ChatModuleHost host, ChatChannelInfo channel, uint senderId, string senderName,
                int senderGmLevel, string name, string args, string text, DateTime timestamp)
            {
                this.host = host;
                this.Channel = channel;
                this.SenderId = senderId;
                this.SenderName = senderName ?? string.Empty;
                this.SenderGmLevel = senderGmLevel;
                this.Name = name;
                this.Args = args;
                this.Text = text;
                this.Timestamp = timestamp;
            }

            public ChatChannelInfo Channel { get; }

            public uint SenderId { get; }

            public string SenderName { get; }

            public int SenderGmLevel { get; }

            public string Name { get; }

            public string Args { get; }

            public string Text { get; }

            public DateTime Timestamp { get; }

            public ChatPostResult ReplyPrivate(string senderName, string text)
            {
                return this.host.Tell(this.SenderId, senderName, text);
            }
        }

        private sealed class LoadedModule
        {
            public LoadedModule(string id, List<IChatModule> entries)
            {
                this.Id = id;
                this.Entries = entries;
            }

            public string Id { get; }

            public List<IChatModule> Entries { get; }
        }

        /// <summary>A module with a Capabilities entry may use only what it lists; one without may use everything.</summary>
        private sealed class Gate
        {
            private readonly string moduleId;

            private readonly HashSet<string> granted;

            private readonly HashSet<string> warned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public Gate(string moduleId, List<string> granted)
            {
                this.moduleId = moduleId;
                this.granted = granted == null ? null : new HashSet<string>(granted, StringComparer.OrdinalIgnoreCase);
            }

            public void Demand(string capability)
            {
                if (this.granted == null || this.granted.Contains(capability))
                {
                    return;
                }

                lock (this.warned)
                {
                    if (this.warned.Add(capability))
                    {
                        Log.Warn("Chat module " + this.moduleId + " used '" + capability
                            + "', which Config/ChatModules.json Capabilities does not grant it.");
                    }
                }

                throw new UnauthorizedAccessException(
                    "Chat module " + this.moduleId + " is not granted the '" + capability + "' capability.");
            }
        }

        private sealed class ModuleLogger : IChatModuleLogger
        {
            private readonly string prefix;

            public ModuleLogger(string moduleId)
            {
                this.prefix = "[ChatModule " + moduleId + "] ";
            }

            public void Debug(string message) => Log.Debug(this.prefix + message);

            public void Info(string message) => Log.Info(this.prefix + message);

            public void Warn(string message) => Log.Warn(this.prefix + message);

            public void Error(string message) => Log.Error(this.prefix + message);

            public void Error(Exception exception, string message) => Log.Error(exception, this.prefix + message);
        }

        private sealed class ModuleContext : IChatModuleContext
        {
            public ModuleContext(ChatModuleHost host, string moduleId, string moduleDirectory, Gate gate,
                ChatModuleDelivery delivery)
            {
                this.ModuleId = moduleId;
                this.ModuleDirectory = moduleDirectory;
                this.Log = new ModuleLogger(moduleId);
                this.View = new ChannelsView(host, moduleId, gate, delivery);
            }

            public ChannelsView View { get; }

            public string ModuleId { get; }

            public string ModuleDirectory { get; }

            public IChatModuleLogger Log { get; }

            public IChatChannels Channels => this.View;

            public T GetConfig<T>()
                where T : class
            {
                string path = Path.Combine(this.ModuleDirectory, "config.json");
                if (!File.Exists(path))
                {
                    return null;
                }

                using (FileStream stream = File.OpenRead(path))
                {
                    return JsonSerializer.Deserialize<T>(stream, ChatModulesSettings.JsonOptions);
                }
            }
        }

        private sealed class ChannelsView : IChatChannels
        {
            private readonly ChatModuleHost host;

            private readonly string moduleId;

            private readonly Gate gate;

            private readonly ChatModuleDelivery delivery;

            private readonly object registrationSync = new object();

            /// <summary>Commands claimed during Initialize; null once registration has ended.</summary>
            private List<CommandEntry> pendingCommands = new List<CommandEntry>();

            public ChannelsView(ChatModuleHost host, string moduleId, Gate gate, ChatModuleDelivery delivery)
            {
                this.host = host;
                this.moduleId = moduleId;
                this.gate = gate;
                this.delivery = delivery;
            }

            public int MaxSenderNameLength => ChatModuleHost.MaxSenderNameLength;

            public int MaxTextLength => ChatModuleHost.MaxTextLength;

            public event Action<ChatChannelMessage> MessagePosted
            {
                add
                {
                    this.gate.Demand(ChatModuleCapabilities.ReadChannels);
                    this.delivery.Subscribe(value);
                }

                remove
                {
                    this.delivery.Unsubscribe(value);
                }
            }

            public IReadOnlyList<ChatChannelInfo> List()
            {
                return this.host.ListChannels();
            }

            public ChatChannelInfo Find(byte type, uint id)
            {
                ChannelBase channel = this.host.FindChannel(type, id);
                return channel == null ? null : Describe(channel);
            }

            public ChatPostResult Post(byte type, uint id, string senderName, string text)
            {
                this.gate.Demand(ChatModuleCapabilities.PostChannels);
                return this.host.Post(this.moduleId, type, id, senderName, text);
            }

            public void RegisterCommand(string name, Action<IChatCommand> handler)
            {
                this.gate.Demand(ChatModuleCapabilities.ConsumeCommands);
                if (handler == null)
                {
                    throw new ArgumentNullException(nameof(handler));
                }

                if (!IsValidCommandName(name))
                {
                    throw new ArgumentException("Chat command '" + name + "' must be one prefix character other than '.' "
                        + "or '/' followed by 1 to " + MaxCommandNameLength + " letters, digits, '-' or '_'.", nameof(name));
                }

                lock (this.registrationSync)
                {
                    if (this.pendingCommands == null)
                    {
                        throw new InvalidOperationException("Chat commands can be registered only during Initialize.");
                    }

                    if (this.host.commandTable.Commands.ContainsKey(name)
                        || this.pendingCommands.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new InvalidOperationException("Chat command '" + name + "' is already registered.");
                    }

                    this.pendingCommands.Add(new CommandEntry(name, handler, this.delivery));
                }
            }

            /// <summary>Ends registration and returns what was claimed; later RegisterCommand calls throw.</summary>
            public List<CommandEntry> EndRegistration()
            {
                lock (this.registrationSync)
                {
                    List<CommandEntry> result = this.pendingCommands ?? new List<CommandEntry>();
                    this.pendingCommands = null;
                    return result;
                }
            }
        }
    }
}
