namespace ZoneEngine_New.Tests;

using System;

using AORebirth.Enums;

using LostEden.Protocol.GameData;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ZoneEngine_New.Core.Data;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.World;

/// <summary>
/// Lost Eden v0.15.1 swap (2026-11-26): character 451's logout save failed and it was despawned unsaved, yet the swap
/// reported it "logged_out_saved". World only saw the lease go. A quarantined player's release now says it was unsaved.
/// </summary>
[TestClass]
public sealed class UnsavedLeaseReleaseTests
{
    [TestMethod]
    public void AQuarantinedPlayerReleasesItsLeaseAsUnsaved()
    {
        Player player = NewPlayer();
        var lease = new Lease();
        player.AttachOnlineOwnership(lease);

        player.QuarantinePersistence();
        player.ReleaseOnlineOwnership();

        Assert.IsTrue(lease.Unsaved && lease.Disposed);
    }

    [TestMethod]
    public void ASavedPlayerReleasesItsLeaseNormally()
    {
        Player player = NewPlayer();
        var lease = new Lease();
        player.AttachOnlineOwnership(lease);

        player.ReleaseOnlineOwnership();
        player.ReleaseOnlineOwnership();

        Assert.IsTrue(lease.Disposed);
        Assert.IsFalse(lease.Unsaved);
    }

    static Player NewPlayer()
        => new(new Identity { Type = IdentityType.SimpleChar, Instance = 451 }, new Logger(), new NoItems());

    sealed class Lease : IUnsavedRelease
    {
        internal bool Unsaved;
        internal bool Disposed;
        public void MarkUnsaved() => Unsaved = true;
        public void Dispose() => Disposed = true;
    }

    sealed class Logger : IZoneLogger
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
        public void Error(Exception exception, string message) { }
        public IZoneLogger CreateForPlayfield(int playfieldId) => this;
    }

    sealed class NoItems : IItemBuilder
    {
        public Item Create(int lowId, int highId, int quality, ItemSource source, int stackCount = 1, int instanceId = 0,
            Identity? identity = null, byte[]? statsBlob = null) => throw new NotSupportedException();
        public Item CreateWithNewInstance(int lowId, int highId, int quality, ItemSource source, int stackCount = 1)
            => throw new NotSupportedException();
        public ItemTemplate CreateTemplate(int lowId, int highId, int quality) => new() { Id = lowId, Quality = quality };
        public bool TryFromInstanceRecord(ItemInstanceRecord row, out Item item) => throw new NotSupportedException();
    }
}
