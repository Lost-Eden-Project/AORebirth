namespace ZoneEngine_New.Core.DailyLogin;

using System;
using System.IO;
using System.Linq;
using AORebirth.Interfaces.Persistence.Characters;
using ZoneEngine_New.Core.Commands;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.Logging;

/// <summary>Restores the captured native claim trigger; HTTP requests alone cannot award inventory.</summary>
public sealed class DailyLoginClaimBridge(DailyLoginRewardService rewards, IDailyLoginClaimDao dao,
    DailyLoginClaimFiles files, IZoneLogger logger)
{
    public void HandleClaim(Player player, string authenticatedAccount, DateTime utcNow)
    {
        string account = (authenticatedAccount ?? string.Empty).Trim().ToLowerInvariant();
        if (account.Length == 0 || player.Session is null)
            return;
        try
        {
            // Completed requests remain evidence, so projection repair must also work with an empty queue.
            PublishCurrentBoard(account, player.Identity.Instance, utcNow);
            var pending = files.Pending(account, player.Identity.Instance);
            if (pending.Count == 0)
            {
                Feedback("No Daily Login request is waiting for this character or account. Reopen Daily Login and try again.");
                return;
            }
            foreach (DailyLoginPendingClaim request in pending)
            {
                DailyLoginClaimSnapshotData? before = dao.LoadDailyLoginState(account);
                DailyLoginAccountState imported = before is null
                    ? files.ReadLegacyProgress(account, utcNow) : DailyLoginAccountState.Parse(before.StateJson);
                DailyLoginClaimResult result = rewards.Claim(player, account, request.ClaimToken, imported, utcNow);
                // Receipts are durable projections only. A file failure is recovered by replaying the DB token.
                // Publish the durable board before exposing a committed result to a browser that immediately syncs it.
                PublishCurrentBoard(account, player.Identity.Instance, utcNow);
                files.WriteResult(result);
                Feedback(result.Message);
                if (player.IsPersistenceQuarantined)
                    break;
            }
        }
        catch (Exception exception)
        {
            logger.Error(exception, "Daily Login claim failed for character " + player.Identity.Instance + ". Pending requests were retained.");
            if (!player.IsPersistenceQuarantined)
                Feedback("Daily reward confirmation is unavailable. Your request has been retained; reopen Daily Login to retry.");
        }

        void Feedback(string message)
        {
            if (player.Session is not null)
                GmCommandFeedback.Send(player.Session, player, message);
        }
    }

    public bool PublishCurrentBoard(string account, int characterId, DateTime utcNow)
    {
        DailyLoginClaimSnapshotData? committed = dao.LoadDailyLoginState(account);
        if (committed is null)
            return false;
        files.PublishBoard(account, characterId, DailyLoginAccountState.Parse(committed.StateJson), utcNow);
        return true;
    }
}

public sealed record DailyLoginBridgePaths(string[] ClaimsRoots, string RewardsJson, string ProfessionNanosJson)
{
    public static DailyLoginBridgePaths Resolve()
    {
        string[] configuredRoots = Configured("AO_REBIRTH_DAILY_LOGIN_CLAIMS_ROOTS");
        string state = Environment.GetEnvironmentVariable("AO_REBIRTH_ZONE_STATE_DIR") ?? string.Empty;
        // Retain the original combined roots: an added configured bridge must not hide old account progress.
        string[] roots = configuredRoots
            .Concat(!string.IsNullOrWhiteSpace(state) ? [Path.Combine(state.Trim(), "daily-login", "claims")] : Array.Empty<string>())
            .Concat(OperatingSystem.IsWindows()
                ? [@"C:\xampp\htdocs\daily\data\claims", @"C:\xampp\htdocs\uwg.daily.icc-rk\data\claims"] : Array.Empty<string>())
            .Distinct(StringComparer.Ordinal).ToArray();
        if (roots.Length == 0)
            roots = [Path.Combine(AppContext.BaseDirectory, "Content", "Daily", "data", "claims")];
        string[] rewardPaths = Configured("AO_REBIRTH_DAILY_LOGIN_REWARDS_JSON");
        if (rewardPaths.Length == 0)
            rewardPaths = [Path.Combine(AppContext.BaseDirectory, "Content", "Daily", "rewards.json")];
        string reward = rewardPaths.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("Daily Login rewards.json is unavailable; configure the existing AO_REBIRTH_DAILY_LOGIN_REWARDS_JSON setting.");
        return new DailyLoginBridgePaths(roots, reward,
            Path.Combine(AppContext.BaseDirectory, "Content", "Daily", "day10-profession-nanos.json"));
    }

    static string[] Configured(string variable)
        => (Environment.GetEnvironmentVariable(variable) ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
