namespace ZoneEngine_New.Core.Organizations
{
    using System;
    using System.Text;

    using LostEden.Protocol.GameData;
    using LostEden.Protocol.Messages.N3Messages;

    /// <summary>
    /// Organization replies. The client holds every sentence (text.mdb); the server sends a reference it expands:
    /// <c>~&amp;</c>, the base-85 category, the base-85 message id, the arguments, <c>~</c>. Arguments are 's' + one character
    /// holding the length + 1 + the text, 'u' + a base-85 unsigned int, 'R' + a base-85 category and id (another client
    /// text). Retail org replies use category 508 (ref <c>#*:Xm</c> is 508/110113751, "You're not in an organization.";
    /// see docs/generated/org_commands_scenario_capture_20261031.md); a few come from 110 or as Feedback 501/508.
    /// Every id below was checked against the client's text.mdb. The ids identify client text, not game content.
    /// </summary>
    public static class OrgText
    {
        public const int OrgCategory = 508;
        public const int FeedbackCategory = 110;
        public const int DialogCategory = 501;

        // ---- 508: replies to the actor
        public const int NotInOrganization = 110113751;            // You're not in an organization.
        public const int InfoSelfNoOrganization = 34521794;        // You are not a member of an organization.
        public const int InfoTargetNoOrganization = 113648514;     // Target is not a member of an organization.
        public const int RanksHeader = 197560331;                  // Governing form: %s\r\nRanks:
        public const int InvalidGoverningForm = 137012854;         // Invalid governing form. Valid governing forms are:\r\n
        public const int GoverningFormChanged = 165999076;         // Governing form changed for the organization.
        public const int NotLeaderGoverningForm = 34568918;        // Only the leader can change the governing form ...
        public const int NotLeaderName = 130975365;                // Only the leader is allowed to change the name.
        public const int NotLeaderDescription = 130903587;         // ... change the description.
        public const int NotLeaderObjective = 197548405;           // ... change the objective.
        public const int NotLeaderHistory = 52554313;              // ... change the history.
        public const int PromoteNeedsMember = 203390621;           // You must target a player of your own organization to promote her/him.
        public const int DemoteNeedsMember = 131644071;            // ... to demote her/him.
        public const int PromoteTooHigh = 224259411;               // You can only promote someone to one rank-level below your own level ...
        public const int DemoteNotBelow = 182683299;               // You can only demote someone who is one rank-level below your own level.
        public const int DemoteFilledUp = 201946636;               // You cannot demote %s to that rank because it is filled up.
        public const int Promoted = 39868162;                      // You promoted %s to the rank of %s.
        public const int Demoted = 67129906;                       // You demoted %s to the rank of %s.
        public const int InviteNeedsTarget = 225271141;            // You must target another player to invite.
        public const int InviteRankTooLow = 181351343;             // Your rank is not high enough for you to be able to invite new members ...
        public const int InviteOtherSide = 247974149;              // The target is not from your side. ...
        public const int AlreadyInOrganization = 87033703;         // %s is already member of another organization. ...
        public const int Joined = 172832727;                       // You've joined the organization.
        public const int CouldNotJoin = 127838135;                 // You couldn't join the organization.
        public const int LeaderCannotLeave = 162192978;            // You cannot leave the organization without appointing another leader first!
        public const int KickRankTooLow = 167722847;               // You have to be the leader, or someone 3 organizational ranks above the player to kick him ...
        public const int NoActiveCharacter = 53724837;             // There is currently no active character called '%s'.
        public const int KickSelf = 186172214;                     // You can't kick yourself. Use '/org leave' instead.
        public const int NotAMember = 248298914;                   // %s is not a member.
        public const int InvalidCharacterName = 44637365;          // The specified character name is not valid.
        public const int NotLeaderDisband = 63867572;              // Only the leader or a GM can disband your organization.
        public const int Disbanded = 217117108;                    // Organization disbanded.
        public const int BankBalance = 78644776;                   // Organization bank account: %u credits.
        public const int BankRankTooLow = 237299844;               // Your rank in this organization is too low to view bank account details.
        public const int CreditsNotAllowed = 5429092;              // The specified amount of credits is not allowed.
        public const int NotEnoughCredits = 89364632;              // You cannot add more credits than you own.
        public const int NotLeaderBankRemove = 222804051;          // Only leaders can take credits out of the organization.
        public const int BankInsufficient = 210489620;             // You cannot withdraw more credits ... The account holds %i credits right now.
        public const int NotLeaderStartVote = 258658757;           // Only the leader or a GM can initiate voting for your organization.
        public const int NotLeaderStopVote = 119211733;            // Only the leader or a GM can stop the voting for your organization.
        public const int VoteDuration = 68628373;                  // The legal vote time is between 2 and 10080 minutes. ...
        public const int VoteChoiceCount = 98440303;               // Voting should have a minimum of 2 and a maximum of 10 choices.
        public const int VoteChoiceFormat = 15846784;              // The vote options need to be one word only preceded by the choice number ...
        public const int VoteChoiceDuplicates = 76010659;          // Voting choices contain duplicates. Choices must be unique.
        public const int VoteNeedsQuestion = 178191954;            // Could not understand voting data. First parameter should be a description ...
        public const int VoteUsage = 111800288;                    // Could not understand voting data. Usage : /org startvote <desc> <duration> <choices> ...
        public const int VoteInProgress = 168300623;               // Voting is curently in progress. ...
        public const int VoteTooSoon = 114148739;                  // There is a minumum period of %d minutes between voting. Voting may be started in %d minutes from now.
        public const int VoteMembersInformed = 123361268;          // Organization members have been informed.
        public const int VoteBeginsSoon = 65798404;                // Voting can begin in a few seconds. Use '/org vote info' ...
        public const int NoVoteInProgress = 179440135;             // No voting in progress.
        public const int VoteNotOpenYet = 187598404;               // Not ready to accept votes yet. Voting can begin in a few seconds.
        public const int VoteJoinedAfter = 187320549;              // You cannot vote because you joined this organization after voting began.
        public const int VoteNumberTooHigh = 99189171;             // The number [%s] exceeds the maximum number of options on vote. ...
        public const int VoteAlreadyCast = 259056068;              // You have already cast your vote. ...
        public const int VoteNotUnderstood = 179467989;            // Could not understand vote. Example uses : /org vote info, /org vote 1
        public const int VoteCast = 213809944;                     // Your vote has been cast. You voted for %s
        public const int VoteStopped = 131953188;                  // Voting stopped.
        public const int VoteInfoHeader = 89144094;                // Voting information for "%s"...\r\nCandidates:
        public const int VoteStatus = 95062979;                    // \r\nStatus:
        public const int VoteMinutesRemaining = 67317284;          // %d minutes remaining.
        public const int VoteNotVotedYet = 97773172;               //  You have not voted yet. ...
        public const int VoteYouVotedFor = 181849080;              //  You have voted for %s
        public const int VoteJoinedAfterShort = 140104245;         //  You cannot vote because you joined this organization after voting began.

        // ---- 508: Org Msg channel lines to every member
        public const int MemberJoined = 173558247;                 // %s invited %s to your organization.
        public const int MemberLeft = 45978487;                    // %s just left your organization.
        public const int MemberKicked = 37093479;                  // %s kicked %s from your organization.
        public const int GoverningFormNotice = 138965334;          // %s changed governing form to '%s'.
        public const int LeadershipGiven = 241047288;              // Leadership has been given to %s.
        public const int DisbandNotice = 143442663;                // Org Message: Your leader, %s has disbanded your organization.
        public const int VoteNotice = 206008469;                   // Voting notice for "%s"...\r\nDuration: %d minutes\r\nCandidates: %s ...
        public const int VoteStoppedNotice = 166906756;            // Organization leader has stopped the voting with message : "%s"
        public const int VoteResults = 16378820;                   // Results for "%s"...\r\nStatus:
        public const int VoteMembersVoted = 112284196;             // %d member(s) voted.
        public const int VoteNoVotes = 88959635;                   // No votes were cast.
        public const int VotePlace = 5931833;                      // %s ( %d vote(s) ):
        public const int VotePlaceTied = 54457652;                 // %s ( %d vote(s) each ):
        public const int FirstPlace = 0;                           // 508/0..9: First place .. Tenth place

        // ---- 508: to the member concerned
        public const int YouWerePromoted = 105314766;              // You were just promoted by %s to the rank of %s.
        public const int YouWereDemoted = 205958398;               // You were just demoted by %s to the rank of %s.

        // ---- 110
        public const int YouWereKicked = 169766805;                // %s kicked you from your organization.
        public const int LeadershipGivenToYou = 98652424;          // Leadership was given to you by %s.

        // ---- 501, sent as Feedback
        public const int Created = 182257540;                      // You created an organization and became the leader of it.
        public const int NameLength = 89300600;                    // The name had an invalid length. ...
        public const int NameLetters = 24190273;                   // The organization name must contain at least 3 alphabetical characters.
        public const int NameLowercase = 13951557;                 // The name cannot start with a lowercase letter.
        public const int NameInvalidCharacter = 13952082;          // The organization name contained an invalid character. ...
        public const int NameTaken = 167950692;                    // The name is already used by another organization.
        public const int CreateRequirements = 174731972;           // You can only create an organization of a full 6 member team. ...
        public const int LeadershipTransferQuestion = 155922318;   // Are you sure you want to transfer organization leadership to %s?

        // ---- 508, sent as Feedback (captured: /org leave)
        public const int YouLeft = 45846567;                       // You've left the organization.

        /// <summary>The plain line retail sends every online member after a name/description/objective/history change.</summary>
        public const string InfoUpdated = "Organization info updated.";

        /// <summary>
        /// The longest 's' argument: its length + 1 goes out as one character the client reads as a single UTF-8 byte.
        /// </summary>
        public const int MaxStringArgument = 126;

        /// <summary>A client text used as an argument ('R').</summary>
        public readonly record struct TextRef(int Category, int Id);

        /// <summary>Category 508 text with <paramref name="args"/> (string, int/uint/long, or <see cref="TextRef"/>).</summary>
        public static string Org(int id, params object[] args) => Ref(OrgCategory, id, args);

        public static string Ref(int category, int id, params object[] args)
        {
            var text = new StringBuilder(32);
            text.Append("~&");
            AppendBase85(text, (uint)category);
            AppendBase85(text, (uint)id);
            foreach (object arg in args)
            {
                switch (arg)
                {
                    case TextRef reference:
                        text.Append('R');
                        AppendBase85(text, (uint)reference.Category);
                        AppendBase85(text, (uint)reference.Id);
                        break;
                    case int number:
                        text.Append('u');
                        AppendBase85(text, (uint)Math.Max(0, number));
                        break;
                    case long number:
                        text.Append('u');
                        AppendBase85(text, (uint)Math.Clamp(number, 0, uint.MaxValue));
                        break;
                    case uint number:
                        text.Append('u');
                        AppendBase85(text, number);
                        break;
                    default:
                        string value = Printable(arg?.ToString() ?? string.Empty, MaxStringArgument);
                        text.Append('s').Append((char)(value.Length + 1)).Append(value);
                        break;
                }
            }

            return text.Append('~').ToString();
        }

        /// <summary>
        /// <paramref name="value"/> cut to <paramref name="max"/> characters with control characters and anything outside
        /// printable ASCII replaced: player-written text never reaches the client's reference parser or its length byte
        /// unchecked.
        /// </summary>
        public static string Printable(string value, int max)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;
            var clean = new StringBuilder(Math.Min(value.Length, max));
            foreach (char c in value)
            {
                if (clean.Length >= max)
                    break;
                clean.Append(c is >= ' ' and <= '~' ? c : '?');
            }

            return clean.ToString();
        }

        static void AppendBase85(StringBuilder text, uint value)
        {
            Span<char> digits = stackalloc char[5];
            for (int i = 4; i >= 0; i--)
            {
                digits[i] = (char)('!' + value % 85);
                value /= 85;
            }

            text.Append(digits);
        }

        /// <summary>A reference line as retail frames org replies (ChatText Unknown1 0x1000, Unknown2 1).</summary>
        public static ChatTextMessage Line(Identity recipient, string text) => new()
        {
            Identity = recipient,
            Text = text,
            Unknown1 = 0x10 << 8,
            Unknown2 = 1
        };

        /// <summary>A plain-text line (retail "Organization info updated.": Unknown2 0).</summary>
        public static ChatTextMessage Plain(Identity recipient, string text) => new()
        {
            Identity = recipient,
            Text = text,
            Unknown1 = 0x10 << 8,
            Unknown2 = 0
        };

        public static FeedbackMessage Feedback(Identity recipient, int category, int id) => new()
        {
            Identity = recipient,
            Unknown = 1,
            Unknown1 = 0,
            CategoryId = category,
            MessageId = id
        };
    }
}
