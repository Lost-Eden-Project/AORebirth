namespace ZoneEngine_New.Core.DailyLogin;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>The existing PHP file bridge. Files are requests/projections; the DAO owns committed progress.</summary>
public sealed class DailyLoginClaimFiles
{
    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    readonly string[] _roots;

    public DailyLoginClaimFiles(IEnumerable<string> roots)
    {
        _roots = roots.Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).ToArray();
        if (_roots.Length == 0)
            throw new InvalidDataException("Daily Login has no configured claim directory.");
    }

    public IReadOnlyList<DailyLoginPendingClaim> Pending(string account, int characterId)
    {
        var requests = new Dictionary<string, DailyLoginPendingClaim>(StringComparer.Ordinal);
        foreach (string root in _roots)
        {
            if (!Directory.Exists(root))
                continue;
            IEnumerable<string> paths = Directory.EnumerateFiles(root, "request-*.json", SearchOption.TopDirectoryOnly)
                .Concat(new[] { "pending-" + characterId + ".json", "pending-account-" + SafeKey(account) + ".json", "pending-latest.json" }
                    .Select(name => Path.Combine(root, name)).Where(File.Exists));
            foreach (string path in paths)
            {
                RejectLink(path);
                string json = File.ReadAllText(path);
                DailyLoginPendingClaim pending = JsonSerializer.Deserialize<DailyLoginPendingClaim>(json, JsonOptions)
                    ?? throw new InvalidDataException("Daily Login pending claim is empty.");
                bool legacyCharacterAlias = pending.CharacterId == characterId
                    && string.Equals(pending.AccountKey, "character:" + characterId, StringComparison.OrdinalIgnoreCase);
                // Browser identities are routing hints only. The authenticated game connection selects its own queue.
                if (pending.CharacterId > 0 && pending.CharacterId != characterId
                    || !legacyCharacterAlias && !string.IsNullOrWhiteSpace(pending.AccountKey) && !string.Equals(pending.AccountKey.Trim(), account, StringComparison.OrdinalIgnoreCase)
                    || pending.CharacterId <= 0 && string.IsNullOrWhiteSpace(pending.AccountKey))
                    continue;
                if (string.IsNullOrEmpty(pending.ClaimToken))
                    pending.ClaimToken = "legacy-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account.Trim().ToLowerInvariant() + "\n" + json)))[..48].ToLowerInvariant();
                if (!DailyLoginRewardService.IsValidToken(pending.ClaimToken))
                    throw new InvalidDataException("Daily Login pending claim has an invalid token.");
                if (requests.TryGetValue(pending.ClaimToken, out DailyLoginPendingClaim? prior))
                {
                    if (prior.CharacterId != pending.CharacterId || !string.Equals(prior.AccountKey, pending.AccountKey, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Daily Login pending token has conflicting owners.");
                    continue;
                }
                string receiptPath = Path.Combine(root, "result-" + SafeKey(pending.ClaimToken) + ".json");
                if (File.Exists(receiptPath))
                {
                    RejectLink(receiptPath);
                    DailyLoginClaimResult receipt = DailyLoginClaimResult.Parse(File.ReadAllText(receiptPath));
                    // Historical server receipts predate the atomic ledger. A completed old token must never
                    // become a fresh next-day grant merely because its pending file survived a crash.
                    if (receipt.Committed || receipt.Terminal || receipt.Ok && receipt.Day > 0)
                        continue;
                }
                requests.Add(pending.ClaimToken, pending);
            }
        }
        return requests.Values.OrderBy(value => value.RequestedUtc, StringComparer.Ordinal).ThenBy(value => value.ClaimToken, StringComparer.Ordinal).ToArray();
    }

    public DailyLoginAccountState ReadLegacyProgress(string account, DateTime utcNow)
    {
        var states = new List<DailyLoginAccountState>();
        foreach (string root in _roots)
        {
            string path = Path.Combine(root, "account-" + SafeKey(account) + ".json");
            if (!Directory.Exists(root))
                continue;
            if (File.Exists(path))
            {
                RejectLink(path);
                if (!string.Equals(SafeKey(account), account.Trim().ToLowerInvariant(), StringComparison.Ordinal))
                    throw new InvalidDataException("Legacy Daily Login filename ownership is ambiguous for this account; existing progress was retained for review.");
                Add(DailyLoginAccountState.Parse(File.ReadAllText(path)));
            }
            // A historical successful grant receipt can be newer than an interrupted account-file mirror.
            // Reconcile that existing evidence too, so losing a mirror cannot award an old reward again.
            foreach (string receiptPath in Directory.EnumerateFiles(root, "result-*.json", SearchOption.TopDirectoryOnly))
            {
                RejectLink(receiptPath);
                DailyLoginClaimResult receipt = DailyLoginClaimResult.Parse(File.ReadAllText(receiptPath));
                if (!receipt.Ok || !string.Equals(receipt.AccountKey, account, StringComparison.OrdinalIgnoreCase))
                    continue;
                Add(new DailyLoginAccountState
                {
                    Taken = receipt.Taken, ClaimedCount = receipt.ClaimedCount,
                    LastGrantedUtc = receipt.LastGrantedUtc, LastClaimUtc = receipt.LastClaimUtc,
                    CycleCompletedOn = receipt.CycleCompletedOn
                });
            }
        }
        if (states.Count == 0)
            return new DailyLoginAccountState();
        DateTime? newest = states.Max(state => state.LastGrantedDateUtc);
        DailyLoginAccountState[] current = states.Where(state => state.LastGrantedDateUtc == newest).ToArray();
        // Mirrors from the same grant date can differ after an interrupted historical write. Never unlock a taken day.
        DailyLoginAccountState merged = DailyLoginAccountState.Parse(current[0].ToJson());
        merged.Taken = current.SelectMany(state => state.Taken ?? []).Distinct().Order().ToArray();
        merged.ClaimedCount = merged.Taken.Length;
        merged.LastGrantedUtc = newest?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;
        merged.CycleCompletedOn = current.Select(state => state.CycleCompletedOn).Where(value => !string.IsNullOrEmpty(value)).Order(StringComparer.Ordinal).LastOrDefault() ?? string.Empty;
        return merged;

        void Add(DailyLoginAccountState state)
        {
            state.Normalize(utcNow); // Validate dates without resetting the retained import.
            states.Add(state);
        }
    }

    public void WriteResult(DailyLoginClaimResult result)
    {
        if (!DailyLoginRewardService.IsValidToken(result.ClaimToken))
            throw new InvalidDataException("Daily Login receipt token is invalid.");
        foreach (string root in _roots)
            AtomicWrite(root, "result-" + SafeKey(result.ClaimToken) + ".json", result.ToJson());
    }

    public void PublishBoard(string account, int characterId, DailyLoginAccountState state, DateTime utcNow)
    {
        DailyLoginAccountState current = state.Normalize(utcNow);
        DailyLoginClaimResult board = DailyLoginClaimResult.ForState(account, string.Empty, current, utcNow);
        board.Ok = true;
        foreach (string root in _roots)
        {
            string name = "account-" + SafeKey(account) + ".json";
            string original = Path.Combine(root, name);
            if (File.Exists(original))
            {
                RejectLink(original);
                string archive = Path.Combine(root, "legacy-progress");
                Directory.CreateDirectory(archive);
                RejectLink(archive);
                string retained = Path.Combine(archive, name);
                if (!File.Exists(retained))
                    File.Copy(original, retained, overwrite: false);
            }
            AtomicWrite(root, name, state.ToJson());
            AtomicWrite(root, "board-account-" + SafeKey(account) + ".json", board.ToJson());
            AtomicWrite(root, "board-character-" + characterId + ".json", board.ToJson());
        }
    }

    public static string SafeKey(string account)
    {
        string key = account.Trim().ToLowerInvariant();
        if (key.Length == 0)
            throw new InvalidDataException("Daily Login account identity is missing.");
        return new string(key.Select(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' ? character : '_').ToArray());
    }

    static void AtomicWrite(string root, string name, string json)
    {
        RejectExistingAncestors(root);
        Directory.CreateDirectory(root);
        RejectLink(root);
        string destination = Path.Combine(root, name);
        if (File.Exists(destination)) RejectLink(destination);
        string temporary = Path.Combine(root, ".daily-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    static void RejectLink(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Daily Login bridge paths cannot traverse symbolic links.");
    }

    static void RejectExistingAncestors(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (Directory.Exists(current) || File.Exists(current))
                RejectLink(current);
    }
}

public sealed class DailyLoginPendingClaim
{
    public int CharacterId { get; set; }
    public string AccountKey { get; set; } = string.Empty;
    public string ClaimToken { get; set; } = string.Empty;
    public string RequestedUtc { get; set; } = string.Empty;
}
