namespace ZoneEngine_New.Core.Organizations;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

using AORebirth.Core.GameData;

using ZoneEngine_New.Core.Logging;

/// <summary>One governing form: its name and its rank names, highest authority (0, the leader's) first.</summary>
public sealed class GoverningForm(int id, string name, IReadOnlyList<string> ranks)
{
    public int Id { get; } = id;
    public string Name { get; } = name;
    public IReadOnlyList<string> Ranks { get; } = ranks;
    public int LowestRank => Ranks.Count - 1;

    /// <summary>A form with one rank (Anarchism): every member holds it and the rank rules do not apply.</summary>
    public bool SingleRank => Ranks.Count == 1;

    public string RankName(int rank) => rank >= 0 && rank < Ranks.Count ? Ranks[rank] : string.Empty;
}

/// <summary>
/// Organization rules: Organizations.json (limits, rank distances, vote rules) and GoverningForms.json (the forms and
/// their ranks). Loaded once with the GameData; anything invalid leaves organizations disabled, with the reason logged,
/// rather than running on a partial rule set.
/// </summary>
public sealed class OrganizationRules
{
    /// <summary>The longest name the database holds (org_organizations.Name).</summary>
    public const int NameColumnLength = 40;

    /// <summary>The longest description, objective or history the database holds.</summary>
    public const int TextColumnLength = 255;

    /// <summary>org_votes.Choices holds every choice, one per line.</summary>
    public const int ChoicesColumnLength = 1024;

    /// <summary>The client names ten vote places (text 508/0..9).</summary>
    public const int MaxVotePlaces = 10;

    public static OrganizationRules Disabled(string reason) => new() { DisabledReason = reason };

    /// <summary>Why organizations are off; null when the rules loaded.</summary>
    public string? DisabledReason { get; private init; }

    public bool Enabled => DisabledReason == null;

    public IReadOnlyDictionary<int, GoverningForm> Forms { get; private init; } = new Dictionary<int, GoverningForm>();

    /// <summary>The forms in the order /org governingform lists them: every other form by id, the default one last (retail).</summary>
    public IReadOnlyList<GoverningForm> FormListOrder { get; private init; } = [];

    public int DefaultForm { get; private init; }
    public int CreateTeamSize { get; private init; }
    public bool GmCreateWithoutTeam { get; private init; }
    public int NameMinLength { get; private init; }
    public int NameMaxLength { get; private init; }
    public int NameMinLetters { get; private init; }
    public string NamePunctuation { get; private init; } = string.Empty;
    public int MaxTextLength { get; private init; }
    public int InviteMinRanksAboveLowest { get; private init; }
    public float InteractionRange { get; private init; }
    public TimeSpan InvitationLifetime { get; private init; }
    public TimeSpan LeadershipConfirmWindow { get; private init; }
    public long BankCap { get; private init; }
    public int VoteMinMinutes { get; private init; }
    public int VoteMaxMinutes { get; private init; }
    public int VoteMinChoices { get; private init; }
    public int VoteMaxChoices { get; private init; }
    public int VoteMaxQuestionLength { get; private init; }
    public int VoteMaxChoiceLength { get; private init; }
    public int VoteMinMinutesBetween { get; private init; }
    public TimeSpan VoteOpenDelay { get; private init; }
    public string LeadershipTransferConfirmText { get; private init; } = string.Empty;

    /// <summary>The organization's form; an id no longer in the data falls back to the default form.</summary>
    public GoverningForm FormOf(int id) => Forms.TryGetValue(id, out GoverningForm? form) ? form : Forms[DefaultForm];

    /// <summary>
    /// The rank a member effectively holds: the leader always 0, anyone else 1 to the lowest rank (0 only in a single-rank
    /// form). Stored ranks outside that (legacy data, a form change in GameData) are read this way, never trusted.
    /// </summary>
    public int EffectiveRank(int form, int storedRank, bool isLeader)
    {
        GoverningForm governing = FormOf(form);
        if (isLeader || governing.SingleRank)
            return 0;
        return Math.Clamp(storedRank, 1, governing.LowestRank);
    }

    /// <summary>Whether a member of <paramref name="rank"/> may invite (and, with a higher rank than the target, kick).</summary>
    public bool CanRecruit(int form, int rank, bool isLeader)
    {
        GoverningForm governing = FormOf(form);
        return isLeader || governing.SingleRank || governing.LowestRank - rank >= InviteMinRanksAboveLowest;
    }

    public static OrganizationRules Load(string root, IZoneLogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        OrganizationRules rules;
        try
        {
            rules = Parse(root);
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException
                                              or UnauthorizedAccessException or FormatException)
        {
            rules = Disabled(exception.Message);
        }

        if (rules.Enabled)
            logger.Info(string.Format(CultureInfo.InvariantCulture,
                "GameData organizations: {0} governing forms, default {1}", rules.Forms.Count, rules.FormOf(rules.DefaultForm).Name));
        else
            logger.Error("Organizations are disabled: " + rules.DisabledReason);
        return rules;
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    static OrganizationRules Parse(string root)
    {
        string rulesPath = Path.Combine(root, GameDataPaths.OrganizationsFileName);
        string formsPath = Path.Combine(root, GameDataPaths.GoverningFormsFileName);
        if (!File.Exists(rulesPath))
            return Disabled(GameDataPaths.OrganizationsFileName + " not found at " + rulesPath);
        if (!File.Exists(formsPath))
            return Disabled(GameDataPaths.GoverningFormsFileName + " not found at " + formsPath);

        RulesFile file = JsonSerializer.Deserialize<RulesFile>(File.ReadAllText(rulesPath), JsonOptions)
            ?? throw new InvalidDataException(GameDataPaths.OrganizationsFileName + " is empty.");
        FormsFile formsFile = JsonSerializer.Deserialize<FormsFile>(File.ReadAllText(formsPath), JsonOptions)
            ?? throw new InvalidDataException(GameDataPaths.GoverningFormsFileName + " is empty.");

        var forms = new Dictionary<int, GoverningForm>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, FormEntry? entry) in formsFile.Forms ?? [])
        {
            if (!int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out int id))
                Fail("GoverningForms.json: form key '" + key + "' is not a non-negative number.");
            string formName = (entry?.Name ?? string.Empty).Trim();
            if (formName.Length is 0 or > 64 || !names.Add(formName))
                Fail("GoverningForms.json: form " + id + " needs a unique name of 1 to 64 characters.");
            string[] ranks = (entry!.Ranks ?? []).Select(rank => (rank ?? string.Empty).Trim()).ToArray();
            if (ranks.Length == 0 || ranks.Any(rank => rank.Length is 0 or > 64))
                Fail("GoverningForms.json: form " + id + " needs at least one rank, each 1 to 64 characters.");
            forms[id] = new GoverningForm(id, formName, Array.AsReadOnly(ranks));
        }

        if (forms.Count == 0)
            Fail("GoverningForms.json lists no form.");
        if (!forms.ContainsKey(file.DefaultGoverningForm))
            Fail("Organizations.json: DefaultGoverningForm " + file.DefaultGoverningForm + " is not in GoverningForms.json.");

        NameRules name = file.Name ?? Fail<NameRules>("Organizations.json: Name is missing.");
        VoteRules votes = file.Votes ?? Fail<VoteRules>("Organizations.json: Votes is missing.");
        CreateRules create = file.Create ?? Fail<CreateRules>("Organizations.json: Create is missing.");
        Require(create.TeamSize is >= 1 and <= 6, "Create.TeamSize must be 1 to 6.");
        Require(name.MinLength >= 1 && name.MaxLength >= name.MinLength && name.MaxLength <= NameColumnLength,
            "Name.MinLength/MaxLength must satisfy 1 <= Min <= Max <= " + NameColumnLength + ".");
        Require(name.MinLetters >= 1 && name.MinLetters <= name.MaxLength, "Name.MinLetters must be 1 to Name.MaxLength.");
        string punctuation = name.AllowedPunctuation ?? string.Empty;
        Require(punctuation.All(c => c is >= ' ' and <= '~' && !char.IsLetterOrDigit(c) && c != '~'),
            "Name.AllowedPunctuation may hold printable ASCII punctuation only (no '~').");
        Require(file.MaxTextLength is >= 1 and <= TextColumnLength, "MaxTextLength must be 1 to " + TextColumnLength + ".");
        Require(file.InviteMinRanksAboveLowest >= 0, "InviteMinRanksAboveLowest must not be negative.");
        Require(file.InteractionRange > 0 && file.InteractionRange <= 1000, "InteractionRange must be above 0 and at most 1000.");
        Require(file.InvitationSeconds is >= 5 and <= 3600, "InvitationSeconds must be 5 to 3600.");
        Require(file.LeadershipConfirmSeconds is >= 5 and <= 600, "LeadershipConfirmSeconds must be 5 to 600.");
        // The balance is sent as an unsigned 32-bit client parameter.
        Require(file.BankCap is >= 1 and <= uint.MaxValue, "BankCap must be 1 to " + uint.MaxValue + ".");
        Require(votes.MinMinutes >= 1 && votes.MaxMinutes >= votes.MinMinutes, "Votes.MinMinutes/MaxMinutes are invalid.");
        Require(votes.MinChoices >= 2 && votes.MaxChoices >= votes.MinChoices && votes.MaxChoices <= MaxVotePlaces,
            "Votes.MinChoices/MaxChoices must satisfy 2 <= Min <= Max <= " + MaxVotePlaces + ".");
        Require(votes.MaxQuestionLength is >= 1 and <= 120, "Votes.MaxQuestionLength must be 1 to 120.");
        Require(votes.MaxChoiceLength is >= 1 and <= 32, "Votes.MaxChoiceLength must be 1 to 32.");
        Require(votes.MaxChoices * (votes.MaxChoiceLength + 1) <= ChoicesColumnLength, "Votes choices do not fit the database column.");
        Require(votes.MinMinutesBetween >= 0, "Votes.MinMinutesBetween must not be negative.");
        Require(votes.OpenDelaySeconds is >= 0 and <= 300, "Votes.OpenDelaySeconds must be 0 to 300.");
        string confirm = file.Texts?.LeadershipTransferConfirm ?? string.Empty;
        Require(confirm.Length > 0, "Texts.LeadershipTransferConfirm is missing.");
        _ = string.Format(CultureInfo.InvariantCulture, confirm, "Name", 30);

        GoverningForm[] order = forms.Values.Where(form => form.Id != file.DefaultGoverningForm).OrderBy(form => form.Id)
            .Append(forms[file.DefaultGoverningForm]).ToArray();

        return new OrganizationRules
        {
            Forms = forms,
            FormListOrder = Array.AsReadOnly(order),
            DefaultForm = file.DefaultGoverningForm,
            CreateTeamSize = create.TeamSize,
            GmCreateWithoutTeam = create.GmWithoutTeam,
            NameMinLength = name.MinLength,
            NameMaxLength = name.MaxLength,
            NameMinLetters = name.MinLetters,
            NamePunctuation = punctuation,
            MaxTextLength = file.MaxTextLength,
            InviteMinRanksAboveLowest = file.InviteMinRanksAboveLowest,
            InteractionRange = file.InteractionRange,
            InvitationLifetime = TimeSpan.FromSeconds(file.InvitationSeconds),
            LeadershipConfirmWindow = TimeSpan.FromSeconds(file.LeadershipConfirmSeconds),
            BankCap = file.BankCap,
            VoteMinMinutes = votes.MinMinutes,
            VoteMaxMinutes = votes.MaxMinutes,
            VoteMinChoices = votes.MinChoices,
            VoteMaxChoices = votes.MaxChoices,
            VoteMaxQuestionLength = votes.MaxQuestionLength,
            VoteMaxChoiceLength = votes.MaxChoiceLength,
            VoteMinMinutesBetween = votes.MinMinutesBetween,
            VoteOpenDelay = TimeSpan.FromSeconds(votes.OpenDelaySeconds),
            LeadershipTransferConfirmText = confirm
        };
    }

    static void Require(bool condition, string message)
    {
        if (!condition)
            Fail(GameDataPaths.OrganizationsFileName + ": " + message);
    }

    static void Fail(string message) => throw new InvalidDataException(message);

    static T Fail<T>(string message) => throw new InvalidDataException(message);

    sealed class RulesFile
    {
        public int DefaultGoverningForm { get; set; }
        public CreateRules? Create { get; set; }
        public NameRules? Name { get; set; }
        public int MaxTextLength { get; set; }
        public int InviteMinRanksAboveLowest { get; set; }
        public float InteractionRange { get; set; }
        public int InvitationSeconds { get; set; }
        public int LeadershipConfirmSeconds { get; set; }
        public long BankCap { get; set; }
        public VoteRules? Votes { get; set; }
        public TextRules? Texts { get; set; }
    }

    sealed class CreateRules
    {
        public int TeamSize { get; set; }
        public bool GmWithoutTeam { get; set; }
    }

    sealed class NameRules
    {
        public int MinLength { get; set; }
        public int MaxLength { get; set; }
        public int MinLetters { get; set; }
        public string? AllowedPunctuation { get; set; }
    }

    sealed class VoteRules
    {
        public int MinMinutes { get; set; }
        public int MaxMinutes { get; set; }
        public int MinChoices { get; set; }
        public int MaxChoices { get; set; }
        public int MaxQuestionLength { get; set; }
        public int MaxChoiceLength { get; set; }
        public int MinMinutesBetween { get; set; }
        public int OpenDelaySeconds { get; set; }
    }

    sealed class TextRules
    {
        public string? LeadershipTransferConfirm { get; set; }
    }

    sealed class FormsFile
    {
        public Dictionary<string, FormEntry?>? Forms { get; set; }
    }

    sealed class FormEntry
    {
        public string? Name { get; set; }
        public string[]? Ranks { get; set; }
    }
}
