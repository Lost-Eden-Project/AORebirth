namespace ZoneEngine_New.Tests
{
    using System;
    using System.IO;
    using AORebirth.Core.Text;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    /// <summary>
    /// Config/NameFilter.json edits take effect on the next name checked (character creation, pet naming) without a
    /// restart, and a broken edit never opens the filter.
    /// </summary>
    [TestClass]
    public sealed class NameFilterHotReloadTests
    {
        string _dir = string.Empty;
        string _gameData = string.Empty;
        string _config = string.Empty;
        DateTime _stamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        [TestInitialize]
        public void Setup()
        {
            _dir = Path.Combine(Path.GetTempPath(), "NameFilterHotReload_" + Guid.NewGuid().ToString("N"));
            _gameData = Path.Combine(_dir, "GameData");
            Directory.CreateDirectory(_gameData);
            Directory.CreateDirectory(Path.Combine(_dir, "Config"));
            _config = Path.Combine(_dir, "Config", NameFilter.ConfigFileName);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        void WriteConfig(string json)
        {
            File.WriteAllText(_config, json);
            _stamp = _stamp.AddMinutes(1);
            File.SetLastWriteTimeUtc(_config, _stamp);
        }

        [TestMethod]
        public void AddedRootRefusesNextNameWithoutRestart()
        {
            WriteConfig("{ \"Roots\": [\"zorgle\"] }");
            Assert.IsFalse(NameFilter.For(_gameData).IsAllowed("Zorgleman"));
            Assert.IsTrue(NameFilter.For(_gameData).IsAllowed("Blarghking"));

            WriteConfig("{ \"Roots\": [\"zorgle\", \"blargh\"] }");
            Assert.IsFalse(NameFilter.For(_gameData).IsAllowed("Blarghking"), "a root added while running must refuse the next name");
        }

        [TestMethod]
        public void RemovedRootAllowsNextName()
        {
            WriteConfig("{ \"Roots\": [\"zorgle\"] }");
            Assert.IsFalse(NameFilter.For(_gameData).IsAllowed("Zorgleman"));

            WriteConfig("{ \"Roots\": [] }");
            Assert.IsTrue(NameFilter.For(_gameData).IsAllowed("Zorgleman"));
        }

        [TestMethod]
        public void MalformedEditKeepsLastGoodFilterAndRecovers()
        {
            WriteConfig("{ \"Roots\": [\"zorgle\"] }");
            Assert.IsFalse(NameFilter.For(_gameData).IsAllowed("Zorgleman"));

            WriteConfig("{ \"Roots\": [\"zorgle\", ");
            Assert.IsFalse(NameFilter.For(_gameData).IsAllowed("Zorgleman"), "a broken edit must not open the filter");
            Assert.IsFalse(NameFilter.For(_gameData).IsAllowed("Zorgleman"), "nor throw on the following check");

            WriteConfig("{ \"Roots\": [\"zorgle\", \"blargh\"] }");
            Assert.IsFalse(NameFilter.For(_gameData).IsAllowed("Blarghking"), "the fixed edit takes effect");
        }

        [TestMethod]
        public void MalformedFirstReadThrows()
        {
            WriteConfig("{ \"Roots\": [");
            Assert.ThrowsExactly<System.Text.Json.JsonException>(() => NameFilter.For(_gameData));
        }

        [TestMethod]
        public void UnchangedFileIsNotReread()
        {
            WriteConfig("{ \"Roots\": [\"zorgle\"] }");
            NameFilter first = NameFilter.For(_gameData);
            Assert.AreSame(first, NameFilter.For(_gameData));
        }

        [TestMethod]
        public void DisabledByEditAllowsEveryName()
        {
            WriteConfig("{ \"Roots\": [\"zorgle\"] }");
            Assert.IsFalse(NameFilter.For(_gameData).IsAllowed("Zorgleman"));

            WriteConfig("{ \"Enabled\": false, \"Roots\": [\"zorgle\"] }");
            Assert.IsTrue(NameFilter.For(_gameData).IsAllowed("Zorgleman"));
        }
    }
}
