namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using AORebirth.Modules.Network;
using AORebirth.Modules.World;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using LostEden.Protocol.GameData;
using LostEden.Protocol.Messages;
using LostEden.Protocol.Messages.N3Messages;

using ZoneEngine_New.Core.Modules;

using MsgVector3 = LostEden.Protocol.GameData.Vector3;

/// <summary>
/// Module API 1.5: the message observer's guaranteed field names (ZONE_MODULE_API.md lists the same set) and the
/// named-operation checks that run before anything is queued.
/// </summary>
[TestClass]
public sealed class ModuleMessageApiTests
{
    static readonly Identity Self = new() { Type = IdentityType.CanbeAffected, Instance = 901 };
    static readonly Identity Other = new() { Type = IdentityType.CanbeAffected, Instance = 1_200_001 };

    [TestMethod]
    public void MessageTypeConstantsMatchTheEngineEnum()
    {
        Assert.AreEqual((int)N3MessageType.Attack, MessageTypes.Attack);
        Assert.AreEqual((int)N3MessageType.CastNanoSpell, MessageTypes.CastNanoSpell);
        Assert.AreEqual((int)N3MessageType.CharacterAction, MessageTypes.CharacterAction);
        Assert.AreEqual((int)N3MessageType.CharDCMove, MessageTypes.CharDCMove);
        Assert.AreEqual((int)N3MessageType.FollowTarget, MessageTypes.FollowTarget);
        Assert.AreEqual((int)N3MessageType.GenericCmd, MessageTypes.GenericCmd);
        Assert.AreEqual((int)N3MessageType.LookAt, MessageTypes.LookAt);
        Assert.AreEqual((int)N3MessageType.StopFight, MessageTypes.StopFight);
    }

    [TestMethod]
    public void CharDCMoveGuaranteedFieldsRead()
    {
        var reader = Reader(new CharDCMoveMessage
        {
            Identity = Self,
            MoveType = (MovementAction)7,
            Position = new MsgVector3 { X = 1.5f, Y = 2.5f, Z = 3.5f },
            Heading = new Quaternion { X = 0.1f, Y = 0.2f, Z = 0.3f, W = 0.9f },
            DeltaTime = 42,
            Unknown2 = 4,
            Unknown3 = 5
        });

        AssertIdentity(reader, "Identity", Self);
        AssertInt(reader, "MoveType", 7);
        AssertFloat(reader, "Position.X", 1.5f);
        AssertFloat(reader, "Position.Y", 2.5f);
        AssertFloat(reader, "Position.Z", 3.5f);
        AssertFloat(reader, "Heading.X", 0.1f);
        AssertFloat(reader, "Heading.Y", 0.2f);
        AssertFloat(reader, "Heading.Z", 0.3f);
        AssertFloat(reader, "Heading.W", 0.9f);
        AssertInt(reader, "DeltaTime", 42);
        AssertInt(reader, "Unknown2", 4);
        AssertInt(reader, "Unknown3", 5);

        // The names guaranteed before 1.12 still read.
        AssertFloat(reader, "Coordinates.X", 1.5f);
        AssertFloat(reader, "Coordinates.Y", 2.5f);
        AssertFloat(reader, "Coordinates.Z", 3.5f);
        AssertInt(reader, "Unknown1", 42);
        AssertFloat(reader, "AuxA", 4f);
        AssertFloat(reader, "AuxB", 5f);
    }

    [TestMethod]
    public void ActionMessageGuaranteedFieldsRead()
    {
        var attack = Reader(new AttackMessage { Identity = Self, Target = Other, Action = 3 });
        AssertIdentity(attack, "Identity", Self);
        AssertIdentity(attack, "Target", Other);
        AssertInt(attack, "Action", 3);

        var stop = Reader(new StopFightMessage { Identity = Self, Unknown1 = 1 });
        AssertIdentity(stop, "Identity", Self);
        AssertInt(stop, "Unknown1", 1);

        var cast = Reader(new CastNanoSpellMessage { Identity = Self, NanoId = 123456, Target = Other, Caster = Self });
        AssertIdentity(cast, "Identity", Self);
        AssertInt(cast, "NanoId", 123456);
        AssertIdentity(cast, "Target", Other);
        AssertIdentity(cast, "Caster", Self);

        var action = Reader(new CharacterActionMessage
        {
            Identity = Self, Action = (CharacterActionType)5, Target = Other, Parameter1 = 11, Parameter2 = 12
        });
        AssertIdentity(action, "Identity", Self);
        AssertInt(action, "Action", 5);
        AssertIdentity(action, "Target", Other);
        AssertInt(action, "Parameter1", 11);
        AssertInt(action, "Parameter2", 12);

        var use = Reader(new GenericCmdMessage
        {
            Identity = Self, Action = (GenericCmdAction)3, Count = 1, User = Self, Target = Other
        });
        AssertIdentity(use, "Identity", Self);
        AssertInt(use, "Action", 3);
        AssertInt(use, "Count", 1);
        AssertIdentity(use, "User", Self);
        AssertIdentity(use, "Target", Other);
        AssertIdentity(use, "Target[0]", Other); // The name guaranteed before 1.12.
        Assert.IsFalse(use.TryGetIdentity("Target[1]", out _), "An index past the end reads nothing.");

        var look = Reader(new LookAtMessage { Identity = Self, Target = Other });
        AssertIdentity(look, "Identity", Self);
        AssertIdentity(look, "Target", Other);
    }

    [TestMethod]
    public void Api16GuaranteedFieldsRead()
    {
        var special = Reader(new CharSecSpecAttackMessage { Identity = Self, Target = Other, Stat = 142 });
        AssertIdentity(special, "Identity", Self);
        AssertIdentity(special, "Target", Other);
        AssertInt(special, "Stat", 142);

        var use = Reader(new GenericCmdMessage { Identity = Self, Temp1 = 7 });
        AssertInt(use, "Temp1", 7);

        var feedback = Reader(new FeedbackMessage { Identity = Self, MessageId = 0x2A });
        AssertIdentity(feedback, "Identity", Self);
        AssertInt(feedback, "MessageId", 0x2A);

        var chat = Reader(new ChatTextMessage { Identity = Self, Text = "hello" });
        AssertIdentity(chat, "Identity", Self);
        Assert.IsTrue(chat.TryGetString("Text", out string text), "Text");
        Assert.AreEqual("hello", text, "Text");
    }

    [TestMethod]
    public void FollowTargetReadsThroughTheRuntimeInfoType()
    {
        var follow = Reader(new FollowTargetMessage
        {
            Identity = Self,
            Unknown1 = 2,
            Info = new FollowTargetMessage.TargetInfo { Target = Other, Unknown1 = 9 }
        });
        AssertIdentity(follow, "Identity", Self);
        AssertInt(follow, "Type", (int)FollowTargetType.Target);
        AssertInt(follow, "Unknown1", 2);
        AssertInt(follow, "Info.MoveType", 2); // The name guaranteed before 1.12: the move type byte.
        AssertIdentity(follow, "Info.Target", Other);
        AssertInt(follow, "Info.Unknown1", 9);
        Assert.IsFalse(follow.TryGetInt("Info.Flag", out _), "A stop is a target follow with move type 21; there is no Info.Flag.");

        var path = Reader(new FollowTargetMessage { Identity = Self, Info = new FollowTargetMessage.PathInfo() });
        AssertInt(path, "Type", (int)FollowTargetType.NpcPath);
        Assert.IsFalse(path.TryGetIdentity("Info.Target", out _), "An NPC path has no Info.Target.");
    }

    [TestMethod]
    public void MissingNamesAndWrongKindsReadNothing()
    {
        var reader = Reader(new LookAtMessage { Identity = Self, Target = Other });
        Assert.IsFalse(reader.TryGetInt("NoSuchField", out _));
        Assert.IsFalse(reader.TryGetFloat("Target", out _));
        Assert.IsFalse(reader.TryGetString("Target", out _));
        Assert.IsFalse(reader.TryGetIdentity("", out _));

        var empty = new MessageFieldReader();
        Assert.IsFalse(empty.TryGetInt("MoveType", out _), "A reader with no message reads nothing.");
    }

    [TestMethod]
    public async Task InvokeChecksNameTargetAndArgumentsBeforeQueueing()
    {
        var actions = new ZoneModuleActions(() => null!);

        OperationResult unknown = await actions.Invoke("player.fly", OperationTarget.Character(901), OperationArgs.Empty);
        Assert.AreEqual(OperationCodes.UnknownOperation, unknown.Code);

        OperationResult wrongTarget = await actions.Invoke(OperationNames.PlayerKick, OperationTarget.ForAccount("someone"), OperationArgs.Empty);
        Assert.AreEqual(OperationCodes.BadTarget, wrongTarget.Code);

        OperationResult missing = await actions.Invoke(OperationNames.PlayerTeleport, OperationTarget.Character(901),
            OperationArgs.Of(("playfield", 800), ("x", 637.0f), ("z", 722.4f)));
        Assert.AreEqual(OperationCodes.BadArgs, missing.Code);

        OperationResult wrongType = await actions.Invoke(OperationNames.PlayerMessage, OperationTarget.Character(901),
            OperationArgs.Of(("text", 5)));
        Assert.AreEqual(OperationCodes.BadArgs, wrongType.Code);
    }

    [TestMethod]
    public void AccountOperationsNeedAnAccountStore()
    {
        Assert.IsFalse(Names(new ZoneModuleActions(() => null!)).Contains(OperationNames.AccountBan));
        // The store is only resolved when an account operation runs.
        HashSet<string> names = Names(new ZoneModuleActions(() => null!, () => null!));
        Assert.IsTrue(names.Contains(OperationNames.AccountBan));
        Assert.IsTrue(names.Contains(OperationNames.AccountSuspend));
        Assert.IsTrue(names.Contains(OperationNames.AccountUnban));
    }

    [TestMethod]
    public void OperationCapabilitiesAreTheirNamesOrTheOldCapability()
    {
        var gate = new CapabilityGate("Example", [ModuleCapabilities.RefreshNpc, OperationNames.PlayerTeleport], new StubLogger());
        Assert.IsTrue(gate.AllowsOperation(OperationNames.NpcRefresh), "refresh-npc still grants npc.refresh");
        Assert.IsTrue(gate.AllowsOperation(OperationNames.PlayerTeleport));
        Assert.IsFalse(gate.AllowsOperation(OperationNames.PlayerKick));

        var gated = new GatedActions(new ZoneModuleActions(() => null!), gate);
        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            gated.Invoke(OperationNames.PlayerKick, OperationTarget.Character(901), OperationArgs.Empty).GetAwaiter().GetResult());
        foreach (OperationDescriptor descriptor in gated.Operations)
            Assert.AreEqual(gate.AllowsOperation(descriptor.Name), descriptor.Granted, descriptor.Name);
    }

    static HashSet<string> Names(ZoneModuleActions actions)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (OperationDescriptor descriptor in actions.Operations)
            names.Add(descriptor.Name);
        return names;
    }

    static MessageFieldReader Reader(MessageBody body)
    {
        var reader = new MessageFieldReader();
        reader.Reset(body);
        return reader;
    }

    static void AssertInt(IMessageFields fields, string name, int expected)
    {
        Assert.IsTrue(fields.TryGetInt(name, out int value), name);
        Assert.AreEqual(expected, value, name);
    }

    static void AssertFloat(IMessageFields fields, string name, float expected)
    {
        Assert.IsTrue(fields.TryGetFloat(name, out float value), name);
        Assert.AreEqual(expected, value, 0.0001f, name);
    }

    static void AssertIdentity(IMessageFields fields, string name, Identity expected)
    {
        Assert.IsTrue(fields.TryGetIdentity(name, out EntityRef value), name);
        Assert.AreEqual(new EntityRef((int)expected.Type, expected.Instance), value, name);
    }
}
