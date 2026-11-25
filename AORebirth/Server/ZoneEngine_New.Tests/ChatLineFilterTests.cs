using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using AORebirth.ChatModules.Moderation;
using AORebirth.World.Contracts.V1;
using Cell.Core;
using ChatEngine.Channels;
using ChatEngine.CoreServer;
using ChatEngine.Packets;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ChatClient = ChatEngine.CoreClient.Client;

namespace ZoneEngine_New.Tests;

/// <summary>
/// Chat module line filters (chat module API 1.2): a player's channel line or say/whisper/shout goes through the modules'
/// filters before anyone receives it, the way ChatEngine delivers it (a real channel frame through the client parser, a
/// real World vicinity event), and a broken filter never costs the player their line.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory("ChatNetwork")]
public sealed class ChatLineFilterTests
{
    const uint SpeakerId = 92001;
    const uint ListenerId = 92002;
    const uint ChannelId = 0x0000BEEF;
    static readonly byte[] Block = { 0x00, 0x00 };

    [TestMethod]
    public void OffensiveWordInAChannelIsReplacedForEveryMemberAndModule()
    {
        using var world = new FilterWorld(new WordFilter("bitch", "sweety"));

        world.Speaker.Feed(ChannelFrame(ChannelType.General, ChannelId, "you bitch", Block));

        Assert.AreEqual("you sweety", ChannelText(world.Listener.SingleOfType(65)));
        Assert.AreEqual("you sweety", ChannelText(world.Speaker.SingleOfType(65)), "The speaker sees what was delivered.");
        byte[] relayed = world.Listener.SingleOfType(65);
        CollectionAssert.AreEqual(Block, relayed.Skip(relayed.Length - Block.Length).ToArray(), "The block is relayed as sent.");
        Assert.AreEqual(relayed.Length - 4, (int)BinaryPrimitives.ReadUInt16BigEndian(relayed.AsSpan(2, 2)));
    }

    [TestMethod]
    public void EveryFilteredChannelTypeIsCovered()
    {
        foreach (ChannelType type in new[] { ChannelType.General, ChannelType.Shopping, ChannelType.Team, ChannelType.Raid, ChannelType.Organization })
        {
            var filter = new WordFilter("bitch", "sweety");
            using var world = new FilterWorld(filter, type);

            world.Speaker.Feed(ChannelFrame(type, ChannelId, "bitch", Block));

            Assert.AreEqual("sweety", ChannelText(world.Listener.SingleOfType(65)), type.ToString());
            Assert.AreEqual(ChatLineScope.Channel, filter.Seen.Single().Scope);
            Assert.AreEqual((byte)type, filter.Seen.Single().Channel!.Type);
        }
    }

    [TestMethod]
    public void BlockedChannelLineReachesNobodyAndTellsTheSpeakerWhy()
    {
        using var world = new FilterWorld(new BlockFilter("You are gagged for 9 more minutes."));

        world.Speaker.Feed(ChannelFrame(ChannelType.General, ChannelId, "let me talk", Block));

        Assert.AreEqual(0, world.Listener.Sent.Count, "A gagged line reached a channel member.");
        Assert.AreEqual(1, world.Speaker.Sent.Count);
        CollectionAssert.AreEqual(MsgSystem.Create("You are gagged for 9 more minutes."), world.Speaker.Sent[0]);
    }

    [TestMethod]
    public void AFilterThatThrowsNeverCostsThePlayerTheLine()
    {
        using var world = new FilterWorld(new ThrowingFilter(), new WordFilter("bitch", "sweety"));

        world.Speaker.Feed(ChannelFrame(ChannelType.General, ChannelId, "bitch please", Block));
        world.Speaker.Feed(ChannelFrame(ChannelType.General, ChannelId, "still here", Block));

        CollectionAssert.AreEqual(new[] { "sweety please", "still here" },
            world.Listener.OfType(65).Select(ChannelText).ToArray());
    }

    [TestMethod]
    public void AnInvalidReplacementIsIgnored()
    {
        using var world = new FilterWorld(new ReplaceWith(string.Empty), new ReplaceWith(new string('x', 1025)));

        world.Speaker.Feed(ChannelFrame(ChannelType.General, ChannelId, "hello", Block));

        Assert.AreEqual("hello", ChannelText(world.Listener.SingleOfType(65)));
    }

    [TestMethod]
    public void FiltersRunInLoadOrderAndTheFirstBlockEndsTheChain()
    {
        var first = new WordFilter("bitch", "sweety");
        var second = new BlockFilter(null);
        var third = new WordFilter("sweety", "darling");
        using var world = new FilterWorld(first, second, third);

        world.Speaker.Feed(ChannelFrame(ChannelType.General, ChannelId, "bitch", Block));

        Assert.AreEqual("sweety", second.Seen.Single().Text, "The second filter sees the first one's text.");
        Assert.AreEqual("bitch", second.Seen.Single().OriginalText);
        Assert.AreEqual(0, third.Seen.Count, "A filter after the block still ran.");
        Assert.AreEqual(0, world.Listener.Sent.Count);
        Assert.AreEqual(0, world.Speaker.Sent.Count, "A block without a notice sent the speaker something.");
    }

    [TestMethod]
    public void CommandLinesAreNeverFiltered()
    {
        var filter = new BlockFilter("gagged");
        using var world = new FilterWorld(filter);

        world.Speaker.Feed(ChannelFrame(ChannelType.General, ChannelId, ".gm bitch", Block));

        Assert.AreEqual(0, filter.Seen.Count, "A '.' command went through the line filter.");
        Assert.AreEqual(0, world.Speaker.Sent.Count);
    }

    [TestMethod]
    public void VicinitySayWhisperAndShoutAreFiltered()
    {
        foreach (VicinityChatType type in new[] { VicinityChatType.Say, VicinityChatType.Whisper, VicinityChatType.Shout })
        {
            var filter = new WordFilter("bitch", "sweety");
            using var world = new FilterWorld(filter);

            world.Vicinity(SpeakerId, null, (byte)type, "bitch");

            CollectionAssert.Contains(world.Listener.Sent.Select(Hex).ToList(),
                Hex(MsgVicinity.Create(SpeakerId, "sweety", (byte)type)), type.ToString());
            CollectionAssert.DoesNotContain(world.Listener.Sent.Select(Hex).ToList(),
                Hex(MsgVicinity.Create(SpeakerId, "bitch", (byte)type)));
            ChatLine seen = filter.Seen.Single();
            Assert.AreEqual(ChatLineScope.Vicinity, seen.Scope);
            Assert.AreEqual(type, seen.VicinityType);
            Assert.IsNull(seen.Channel);
            Assert.AreEqual("Speaker", seen.SenderName);
        }
    }

    [TestMethod]
    public void BlockedVicinityLineReachesNobodyAndTellsTheSpeakerWhy()
    {
        using var world = new FilterWorld(new BlockFilter("You are gagged."));

        world.Vicinity(SpeakerId, null, (byte)VicinityChatType.Shout, "hear me");

        Assert.AreEqual(0, world.Listener.Sent.Count, "A gagged shout reached a listener.");
        Assert.AreEqual(1, world.Speaker.Sent.Count);
        CollectionAssert.AreEqual(MsgSystem.Create("You are gagged."), world.Speaker.Sent[0]);
    }

    [TestMethod]
    public void NpcSpeechIsNeverFiltered()
    {
        var filter = new BlockFilter("blocked");
        using var world = new FilterWorld(filter);

        world.Vicinity(123456, "Wounded Dockworker", (byte)VicinityChatType.Say, "Help me");

        Assert.AreEqual(0, filter.Seen.Count);
        CollectionAssert.Contains(world.Listener.Sent.Select(Hex).ToList(),
            Hex(MsgVicinity.Create(123456, "Help me", (byte)VicinityChatType.Say)));
    }

    [TestMethod]
    public void WithoutFiltersLinesAreUntouched()
    {
        using var world = new FilterWorld();

        world.Speaker.Feed(ChannelFrame(ChannelType.General, ChannelId, "bitch", Block));
        world.Vicinity(SpeakerId, null, (byte)VicinityChatType.Say, "bitch");

        Assert.AreEqual("bitch", ChannelText(world.Listener.SingleOfType(65)));
        CollectionAssert.Contains(world.Listener.Sent.Select(Hex).ToList(),
            Hex(MsgVicinity.Create(SpeakerId, "bitch", (byte)VicinityChatType.Say)));
    }

    static string Hex(byte[] bytes) => Convert.ToHexString(bytes);

    /// <summary>The client's channel line: type, channel id, text, block.</summary>
    static byte[] ChannelFrame(ChannelType type, uint channelId, string text, byte[] block)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(text);
        byte[] body = new byte[5 + 2 + bytes.Length + 2 + block.Length];
        body[0] = (byte)type;
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(1, 4), channelId);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(5, 2), (ushort)bytes.Length);
        bytes.CopyTo(body, 7);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(7 + bytes.Length, 2), (ushort)block.Length);
        block.CopyTo(body, 9 + bytes.Length);
        byte[] frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0, 2), 65);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2, 2), (ushort)body.Length);
        body.CopyTo(frame, 4);
        return frame;
    }

    /// <summary>The text of a relayed channel line: header, channel type and id, sender id, then the text string.</summary>
    static string ChannelText(byte[] packet)
    {
        int length = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(13, 2));
        return Encoding.Latin1.GetString(packet, 15, length);
    }

    sealed class FilterWorld : IDisposable
    {
        static readonly Assembly Chat = typeof(ChatServer).Assembly;
        readonly ChatServer server = new();
        public readonly CapturingClient Speaker;
        public readonly CapturingClient Listener;

        public FilterWorld(params IChatLineFilter[] filters) : this(ChannelType.General, filters)
        {
        }

        public FilterWorld(IChatLineFilter filter, ChannelType type) : this(type, new[] { filter })
        {
        }

        FilterWorld(ChannelType type, IChatLineFilter[] filters)
        {
            Speaker = Client(SpeakerId, "Speaker");
            Listener = Client(ListenerId, "Listener");
            var channel = new ChannelBase(default, type, ChannelId, "Fixture");
            server.Channels.Add(channel);
            Speaker.Channels.Add(channel);
            Listener.Channels.Add(channel);
            Speaker.KnownClients.Add(ListenerId);
            Listener.KnownClients.Add(SpeakerId);

            Type hostType = Chat.GetType("ChatEngine.Modules.ChatModuleHost", throwOnError: true)!;
            object host = hostType.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, new[] { typeof(ChatServer) })!
                .Invoke(new object[] { server });
            MethodInfo add = hostType.GetMethod("AddLineFilter", BindingFlags.Instance | BindingFlags.NonPublic)!;
            for (int index = 0; index < filters.Length; index++)
            {
                add.Invoke(host, new object[] { "Fixture" + index, filters[index] });
            }

            typeof(ChatServer).GetField("Modules", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(server, host);
        }

        /// <summary>A vicinity line from the zone, through World, as the chat server receives it.</summary>
        public void Vicinity(uint senderId, string? npcName, byte type, string text)
        {
            var vicinity = new VicinityMessage { Sender = (int)senderId, SenderName = npcName ?? string.Empty, ChannelType = type, Text = text };
            vicinity.Recipients.Add((int)SpeakerId);
            vicinity.Recipients.Add((int)ListenerId);
            typeof(ChatServer).GetMethod("OnWorldMessage", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(server, new object[] { new WorldToService { Chat = new ChatEvent { Vicinity = vicinity } } });
        }

        CapturingClient Client(uint id, string name)
        {
            var client = new CapturingClient(server);
            client.Character.CharacterId = id;
            client.Character.characterName = name;
            // A player, set here so no database is asked for the GM level.
            client.Character.GetType().GetField("characterGMLevel", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(client.Character, 0);
            bool registered = (bool)typeof(ChatServer).GetMethod("RegisterClient", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(server, new object[] { client })!;
            Assert.IsTrue(registered);
            // Bound to its character, as after SelectCharacter: the parser drops channel lines before this.
            typeof(ChatClient).GetMethod("MarkChatReady", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(client, null);
            return client;
        }

        public void Dispose()
        {
            Speaker.Dispose();
            Listener.Dispose();
        }
    }

    sealed class CapturingClient : ChatClient
    {
        public readonly List<byte[]> Sent = new();

        public CapturingClient(ChatServer server) : base(server)
        {
        }

        public IEnumerable<byte[]> OfType(ushort opcode) =>
            Sent.Where(packet => BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(0, 2)) == opcode);

        public byte[] SingleOfType(ushort opcode) => OfType(opcode).Single();

        public override void Send(byte[] packet, int offset, int length) => Sent.Add(packet.AsSpan(offset, length).ToArray());

        public void Feed(byte[] frame)
        {
            _offset = 0;
            _remainingLength = frame.Length;
            Assert.IsTrue(OnReceive(BufferSegment.CreateSegment((byte[])frame.Clone(), 0, frame.Length)));
        }
    }

    abstract class RecordingFilter : IChatLineFilter
    {
        public readonly List<ChatLine> Seen = new();

        public ChatFilterResult Filter(ChatLine line)
        {
            Seen.Add(line);
            return Decide(line);
        }

        protected abstract ChatFilterResult Decide(ChatLine line);
    }

    sealed class WordFilter(string word, string replacement) : RecordingFilter
    {
        protected override ChatFilterResult Decide(ChatLine line) =>
            line.Text.Contains(word, StringComparison.OrdinalIgnoreCase)
                ? ChatFilterResult.Replace(line.Text.Replace(word, replacement, StringComparison.OrdinalIgnoreCase))
                : ChatFilterResult.Pass;
    }

    sealed class BlockFilter(string? notice) : RecordingFilter
    {
        protected override ChatFilterResult Decide(ChatLine line) => ChatFilterResult.Block(notice);
    }

    sealed class ReplaceWith(string text) : RecordingFilter
    {
        protected override ChatFilterResult Decide(ChatLine line) => ChatFilterResult.Replace(text);
    }

    sealed class ThrowingFilter : IChatLineFilter
    {
        public ChatFilterResult Filter(ChatLine line) => throw new InvalidOperationException("broken module");
    }
}
