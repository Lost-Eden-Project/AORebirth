namespace ZoneEngine_New.Core.Commands
{
    using System;
    using System.Globalization;

    using Dapper;

    using MySqlConnector;

    using SmokeLounge.AOtomation.Messaging.GameData;

    using ZoneEngine_New.Core.Data;
    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Mobs;
    using ZoneEngine_New.Core.Playfield;

    /// <summary>
    /// TEMPORARY - REMOVE ONCE SHOPS ARE FIXED. Also remove the TEMP helpers it uses:
    /// <c>ShopStock.TryClearConfiguredStock</c>, <c>VendingMachine.ClearDatabaseDefinition</c>,
    /// <c>NpcContentActivationService.TryReleaseDatabaseShop</c> and its Program.cs registration.
    /// <para>
    /// <c>.clearshopstock</c>: deletes the selected static shop's <c>vendors</c> row (the SQL stock binding for that
    /// exact placement) and releases its runtime binding, so the shop goes back to rolling stock from its item
    /// template's own ShopInventory. Shared <c>vendortemplate</c>/<c>shopinventorytemplates</c> rows are left alone:
    /// other vendors may use the same ShopInvHash.
    /// </para>
    /// </summary>
    public sealed class ClearShopStockCommand : IGmCommand
    {
        const string DeleteVendorSql = "DELETE FROM vendors WHERE Id = @VendorId AND Playfield = @PlayfieldId";

        public string Name => "clearshopstock";

        public int RequiredGmLevel => 1;

        public string Usage => ".clearshopstock (TEMP: wipes the selected shop's SQL stock; it falls back to its template ShopInventory)";

        public GmCommandHelp Help { get; } = new(
            "TEMP: wipes your selected shop's SQL vendor stock so it uses its item template's ShopInventory.",
            [
                new(".clearshopstock", "Deletes the selected vending machine's vendors row and reloads its stock from the template."),
            ],
            [
                new(".clearshopstock", "Select a shop terminal first, then run it."),
            ]);

        public void Execute(GmCommandContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            Player player = context.Player;
            if (player.Playfield is not Playfield playfield)
            {
                GmCommandFeedback.Send(context.Session, player, "Not on a playfield.");
                return;
            }

            Identity target = player.Target;
            if (target.Instance == 0 || target == player.Identity
                || !playfield.GetRequiredService<DynelRegistry>().TryGet(target, out Dynel? dynel))
            {
                GmCommandFeedback.Send(context.Session, player, "Select a shop first.");
                return;
            }

            VendingMachine? machine = dynel switch
            {
                VendingMachine vending => vending,
                NpcCharacter npc => npc.Shop,
                _ => null
            };
            if (machine == null)
            {
                GmCommandFeedback.Send(context.Session, player, "Target is not a shop.");
                return;
            }

            int vendorId = machine.DatabaseVendorId;
            if (vendorId == 0)
            {
                GmCommandFeedback.Send(context.Session, player,
                    "This shop has no SQL stock binding; it already uses its template/JSON stock.");
                return;
            }

            // Release the runtime binding first: it is refused while someone is trading, and the row must not be
            // deleted while the live shop still serves it.
            if (!playfield.GetRequiredService<NpcContentActivationService>().TryReleaseDatabaseShop(machine, out string failure))
            {
                GmCommandFeedback.Send(context.Session, player, failure);
                return;
            }

            int deleted;
            try
            {
                using var connection = new MySqlConnection(MySqlConnectionSettings.GetRequiredConnectionString());
                deleted = connection.Execute(DeleteVendorSql, new { VendorId = vendorId, PlayfieldId = playfield.ResourceId });
            }
            catch (Exception error)
            {
                GmCommandFeedback.Send(context.Session, player,
                    "Runtime stock cleared, but deleting vendors row " + vendorId.ToString(CultureInfo.InvariantCulture)
                    + " failed (it returns on zone restart): " + error.Message);
                return;
            }

            string source = machine.Template.ShopInventory.Count > 0
                ? "its template ShopInventory (" + machine.Template.ShopInventory.Count.ToString(CultureInfo.InvariantCulture) + " lines)"
                : "VendingMachines.json (the template has no ShopInventory)";
            GmCommandFeedback.Send(context.Session, player,
                "Cleared SQL stock for " + machine.Template.Name + " (vendor " + vendorId.ToString(CultureInfo.InvariantCulture)
                + ", " + deleted.ToString(CultureInfo.InvariantCulture) + " row deleted). Next open uses " + source + ".");
        }
    }
}
