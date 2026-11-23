namespace ZoneEngine_New.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;

    using AODB.Common.RDBObjects;

    using Microsoft.VisualStudio.TestTools.UnitTesting;

    using ZoneEngine_New.Core.GameData;
    using ZoneEngine_New.Core.Logging;
    using ZoneEngine_New.Core.WorldSimulation;

    using Quaternion = AORebirth.Core.Vector.Quaternion;
    using Vector3 = AORebirth.Core.Vector.Vector3;

    /// <summary>
    /// Camelot (120) door C01F0078 put arrivals 2.5-5 m in front of its frame, inside the staircase there, and the
    /// player was stuck. PlayfieldContent\120\DoorLandings.json names a clear spot; every way onto that door lands on it.
    /// </summary>
    [TestClass]
    public sealed class DoorLandingOverrideTests
    {
        const int Camelot = 120;
        const int StairDoor = unchecked((int)0xC01F0078);
        static readonly (float X, float Y, float Z) ClearSpot = (218.0f, 23.4f, 187.5f);

        [TestMethod]
        [DataRow(PortalDoorLandingResolver.ProxyEntryDoorClearance)]
        [DataRow(PortalDoorLandingResolver.Proxy2EntryDoorClearance)] // also the exit clearance
        public void Arriving_at_the_camelot_stair_door_lands_on_the_clear_spot_facing_out_of_the_door(float clearance)
        {
            PlayfieldGeometryData camelot = CamelotGeometry();
            PlayfieldDynel door = Door(camelot, StairDoor);

            Assert.IsTrue(PortalDoorLandingResolver.TryResolveDoorLanding(camelot, StairDoor, clearance, out Vector3 walkIn, out Quaternion walkInHeading));
            AssertAt(ClearSpot, walkIn);
            AssertHeading(door, walkInHeading);

            // TeleportProxy2 (the one-way way in that clears the return, which is how the player arrived).
            Assert.IsTrue(PortalDoorLandingResolver.TryResolveProxyLanding(camelot, StairDoor, clearance, out Vector3 proxy2, out Quaternion proxy2Heading));
            AssertAt(ClearSpot, proxy2);
            AssertHeading(door, proxy2Heading);
        }

        [TestMethod]
        public void Other_camelot_doors_keep_landing_in_front_of_their_frame()
        {
            PlayfieldGeometryData camelot = CamelotGeometry();
            PlayfieldDynel other = camelot.Dynels!.Dynels.FirstOrDefault(d =>
                d.IdentityType == (int)LostEden.Protocol.GameData.IdentityType.Door
                && d.IdentityInstance != StairDoor)
                ?? throw new AssertFailedException("Camelot has no second door to compare against.");

            Assert.IsTrue(PortalDoorLandingResolver.TryResolveDoorLanding(
                camelot, other.IdentityInstance, PortalDoorLandingResolver.ExitDoorClearance, out Vector3 landing, out _));

            Vector3 expected = PortalDoorLandingResolver.LandingInFront(
                new Vector3(other.Position.X, other.Position.Y, other.Position.Z),
                new Quaternion(other.Heading.X, other.Heading.Y, other.Heading.Z, other.Heading.W),
                PortalDoorLandingResolver.ExitDoorClearance);
            AssertAt(((float)expected.x, (float)expected.y, (float)expected.z), landing);
        }

        [TestMethod]
        public void Each_landing_is_a_fresh_position_so_moving_one_arrival_never_moves_the_next()
        {
            PlayfieldGeometryData camelot = CamelotGeometry();
            Assert.IsTrue(PortalDoorLandingResolver.TryResolveDoorLanding(
                camelot, StairDoor, PortalDoorLandingResolver.ExitDoorClearance, out Vector3 first));
            first.x += 100;

            Assert.IsTrue(PortalDoorLandingResolver.TryResolveDoorLanding(
                camelot, StairDoor, PortalDoorLandingResolver.ExitDoorClearance, out Vector3 second));
            AssertAt(ClearSpot, second);
        }

        [TestMethod]
        public void A_playfield_without_door_landings_configures_none()
        {
            string root = TempRoot();
            try
            {
                Assert.IsNull(DoorLandingCatalog.Load(root, Camelot));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [TestMethod]
        [DataRow("{ \"SchemaVersion\": 1, \"PlayfieldId\": 121, \"Doors\": [] }", DisplayName = "another playfield's file")]
        [DataRow("{ \"SchemaVersion\": 2, \"PlayfieldId\": 120, \"Doors\": [] }", DisplayName = "unknown schema")]
        [DataRow("{ \"SchemaVersion\": 1, \"PlayfieldId\": 120, \"Doors\": [ { \"Instance\": \"door\", \"X\": 1, \"Y\": 2, \"Z\": 3 } ] }", DisplayName = "instance not hex")]
        [DataRow("{ \"SchemaVersion\": 1, \"PlayfieldId\": 120, \"Doors\": [ { \"Instance\": \"C01F0078\", \"X\": 1, \"Z\": 3 } ] }", DisplayName = "missing Y")]
        [DataRow("{ \"SchemaVersion\": 1, \"PlayfieldId\": 120, \"Doors\": [ { \"Instance\": \"C01F0078\", \"X\": 1, \"Y\": 2, \"Z\": 3 }, { \"Instance\": \"C01F0078\", \"X\": 4, \"Y\": 5, \"Z\": 6 } ] }", DisplayName = "door listed twice")]
        [DataRow("{ \"SchemaVersion\": 1, \"PlayfieldId\": 120, \"Doors\": [ ", DisplayName = "truncated")]
        public void A_malformed_file_fails_the_load_instead_of_putting_players_back_on_the_stairs(string json)
        {
            string root = TempRoot();
            try
            {
                string path = Path.Combine(root, DoorLandingCatalog.RelativePath(Camelot));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, json);

                Assert.ThrowsExactly<InvalidDataException>(() => DoorLandingCatalog.Load(root, Camelot));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        static PlayfieldGeometryData CamelotGeometry()
        {
            PlayfieldGeometryData camelot = new GameDataStore(new DoorLandingTestLogger()).GetPlayfieldGeometry(Camelot);
            RequireOverride(camelot);
            return camelot;
        }

        static void RequireOverride(PlayfieldGeometryData camelot)
        {
            if (camelot.Dynels?.Dynels == null)
                Assert.Inconclusive("GameData has no Camelot Dynels.dat.");
            Assert.IsNotNull(camelot.DoorLandings, "PlayfieldContent\\120\\DoorLandings.json was not loaded.");
            Assert.IsTrue(camelot.DoorLandings!.ContainsKey(StairDoor), "DoorLandings.json does not list door C01F0078.");
        }

        static PlayfieldDynel Door(PlayfieldGeometryData geometry, int instance)
            => geometry.Dynels!.Dynels.First(d =>
                d.IdentityInstance == instance
                && d.IdentityType == (int)LostEden.Protocol.GameData.IdentityType.Door);

        static void AssertAt((float X, float Y, float Z) expected, Vector3 actual)
        {
            Assert.AreEqual(expected.X, actual.x, 0.01, "landing X");
            Assert.AreEqual(expected.Y, actual.y, 0.01, "landing Y");
            Assert.AreEqual(expected.Z, actual.z, 0.01, "landing Z");
        }

        static void AssertHeading(PlayfieldDynel door, Quaternion heading)
        {
            Assert.AreEqual(door.Heading.X, heading.x, 1e-4, "heading x");
            Assert.AreEqual(door.Heading.Y, heading.y, 1e-4, "heading y");
            Assert.AreEqual(door.Heading.Z, heading.z, 1e-4, "heading z");
            Assert.AreEqual(door.Heading.W, heading.w, 1e-4, "heading w");
        }

        static string TempRoot()
        {
            string root = Path.Combine(Path.GetTempPath(), "DoorLandingOverrideTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }
    }

    internal sealed class DoorLandingTestLogger : IZoneLogger
    {
        public void Debug(string message) { }

        public void Info(string message) { }

        public void Warn(string message) { }

        public void Error(string message) { }

        public void Error(Exception exception, string message) { }

        public IZoneLogger CreateForPlayfield(int playfieldId) => this;
    }
}
