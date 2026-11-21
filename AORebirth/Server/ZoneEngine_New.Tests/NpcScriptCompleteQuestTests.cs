namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.NpcScripts;
using ZoneEngine_New.Core.Quests;

/// <summary>The CompleteQuest script verb: what compiles and what is refused.</summary>
[TestClass]
public sealed class NpcScriptCompleteQuestTests
{
    const string QuestsJson = """
        [
          { "Hash": "QBCA", "Name": "Escort Cultist Loke to Spirit Guide Hinnerichsen", "Action": "ReturnToQuestGiver", "NextStep": "09LE" },
          { "Hash": "09LE", "Name": "Talk to Spirit Guide Hinnerichsen", "Action": "ReturnToQuestGiver" }
        ]
        """;

    static string _root = string.Empty;
    static QuestCatalog _quests = null!;
    static IGameData _gameData = null!;

    [ClassInitialize]
    public static void Load(TestContext context)
    {
        _root = Path.Combine(Path.GetTempPath(), "NpcScriptCompleteQuestTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, QuestCatalog.FileName), QuestsJson);
        _gameData = RootOnlyGameData.Create(_root);
        _quests = new QuestCatalog(_gameData, new QuietLogger());
        Assert.IsTrue(_quests.TryGet("QBCA", out _), "The fixture Quests.json must load.");
    }

    [ClassCleanup]
    public static void Cleanup()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public void KnownHashCompilesToOneNonBlockingOp()
    {
        NpcScriptProgram? program = Compile("""{ "Hash": "QBCA", "Radius": 30 }""", out List<string> problems);

        Assert.IsNotNull(program, string.Join("; ", problems));
        ScriptOp op = Array.Find(program.Ops, o => o.Code == ScriptOpCode.CompleteQuest);
        Assert.AreEqual(ScriptOpCode.CompleteQuest, op.Code);
        Assert.AreEqual("QBCA", program.Strings[op.A]);
        Assert.AreEqual(30, op.B);
    }

    [TestMethod]
    [DataRow("""{ "Hash": "ZZZZ", "Radius": 30 }""", DisplayName = "unknown hash")]
    [DataRow("""{ "Hash": "QBCA", "Radius": 0 }""", DisplayName = "radius 0")]
    [DataRow("""{ "Hash": "QBCA", "Radius": 101 }""", DisplayName = "radius above 100")]
    [DataRow("""{ "Hash": "QBCA" }""", DisplayName = "no radius")]
    [DataRow("""{ "Radius": 30 }""", DisplayName = "no hash")]
    [DataRow("\"QBCA\"", DisplayName = "not an object")]
    public void BadOperandsFailToCompileNamingTheVerb(string operand)
    {
        NpcScriptProgram? program = Compile(operand, out List<string> problems);

        Assert.IsNull(program);
        Assert.AreEqual(1, problems.Count);
        StringAssert.Contains(problems[0], "CompleteQuest");
    }

    [TestMethod]
    public void WithoutAQuestCatalogEveryCompleteQuestIsRefused()
    {
        NpcScriptProgram? program = Compile("""{ "Hash": "QBCA", "Radius": 30 }""", out List<string> problems, withQuests: false);

        Assert.IsNull(program);
        StringAssert.Contains(problems[0], "CompleteQuest");
    }

    static NpcScriptProgram? Compile(string operand, out List<string> problems, bool withQuests = true)
    {
        string json = """{ "Id": "Test_CompleteQuest", "Handlers": [ { "On": "Spawned", "Do": [ { "CompleteQuest": """
            + operand + """ } ] } ] }""";
        using JsonDocument document = JsonDocument.Parse(json);
        problems = new List<string>();
        var compiler = new NpcScriptCompiler(_gameData, withQuests ? _quests : null);
        return compiler.CompileProgram("Test_CompleteQuest.json", document.RootElement, problems);
    }

    /// <summary>An <see cref="IGameData"/> with only <see cref="IGameData.RootPath"/>; anything else the verb does not touch fails.</summary>
    public class RootOnlyGameData : DispatchProxy
    {
        string _root = string.Empty;

        public static IGameData Create(string root)
        {
            IGameData proxy = Create<IGameData, RootOnlyGameData>();
            ((RootOnlyGameData)(object)proxy)._root = root;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name == "get_" + nameof(IGameData.RootPath)
                ? _root
                : throw new NotSupportedException("CompleteQuest compiling touched IGameData." + targetMethod?.Name);
    }

    sealed class QuietLogger : IZoneLogger
    {
        public void Debug(string message) { }

        public void Info(string message) { }

        public void Warn(string message) { }

        public void Error(string message) { }

        public void Error(Exception exception, string message) { }

        public IZoneLogger CreateForPlayfield(int playfieldId) => this;
    }
}
