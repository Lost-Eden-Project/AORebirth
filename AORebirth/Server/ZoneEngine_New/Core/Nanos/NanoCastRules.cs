namespace ZoneEngine_New.Core.Nanos
{
    /// <summary>Why a cast attempt was refused. <see cref="None"/> means the cast may start.</summary>
    public enum NanoCastRefusal
    {
        None,
        CasterDead,
        AlreadyCasting,
        Recharging,
        NotUploaded,
        RequirementsNotMet,
        NotEnoughNano,
        /// <summary>Start-only: target cannot hold the buff. Land-time NCU failure still completes the cast.</summary>
        NotEnoughNcu,
        InvalidTarget,
        TargetDead,
        /// <summary>The nano's strain is still on its local cooldown for this caster.</summary>
        NanoLineLocked,

        /// <summary>Start-only: the target already runs a stronger nano of the same strain (a better root, buff, ...).</summary>
        BetterNanoRunning,

        /// <summary>The nano's SpawnItem can only make a Unique the caster already holds (Engineer pet nanos).</summary>
        UniqueItemHeld,

        /// <summary>The caster is stunned: no casting, at the start or when a running bar finishes.</summary>
        CasterStunned,

        /// <summary>A player's friendly nano at an NPC that is not the player's own pet (client 1004f9c4).</summary>
        FriendlyOnNpc,

        /// <summary>A friendly nano at a monster-side target that is not the caster's pet (client 1004eb0f).</summary>
        FriendlyOnEnemy,

        // The player cast gates below follow the client's cast start (Gamecode.dll 1004f9c4), in its order.

        /// <summary>The caster is in a vehicle (MechData set).</summary>
        CasterInVehicle,

        /// <summary>The caster is moving.</summary>
        MustStandStill,

        Swimming,

        Crawling,

        Falling,

        /// <summary>The caster is sitting, sleeping or lounging.</summary>
        MustStandUp,

        /// <summary>The target is in a vehicle (MechData set).</summary>
        TargetInVehicle,

        /// <summary>A CannotRefresh nano while the caster already runs its strain.</summary>
        CantReplaceOther,

        /// <summary>The target is beyond the nano's range (attack range scaled by the caster's NanoRange, at most 40 m).</summary>
        OutOfRange,

        NoLineOfSight,
    }

    /// <summary>
    /// The cast gate, as a pure decision over already-resolved facts. The same gate runs twice:
    /// once when the client asks to cast, and once when the cast bar finishes, because nano,
    /// target and recharge state can all change while the bar runs. Target NCU is not part of
    /// this gate: start checks it separately, and land refuses apply without interrupting finish.
    /// </summary>
    public static class NanoCastRules
    {
        public static NanoCastRefusal Evaluate(in NanoCastAttempt attempt)
        {
            if (attempt.CasterIsDead)
                return NanoCastRefusal.CasterDead;
            if (attempt.CasterIsStunned)
                return NanoCastRefusal.CasterStunned;
            if (attempt.CasterIsCasting)
                return NanoCastRefusal.AlreadyCasting;
            if (attempt.CasterIsRecharging)
                return NanoCastRefusal.Recharging;
            if (attempt.NanoLineLocked)
                return NanoCastRefusal.NanoLineLocked;
            if (!attempt.IsUploaded)
                return NanoCastRefusal.NotUploaded;
            if (!attempt.TargetExists)
                return NanoCastRefusal.InvalidTarget;
            if (attempt.TargetIsDead)
                return NanoCastRefusal.TargetDead;
            if (!attempt.RequirementsMet)
                return NanoCastRefusal.RequirementsNotMet;
            if (attempt.CurrentNano < attempt.NanoCost)
                return NanoCastRefusal.NotEnoughNano;

            return NanoCastRefusal.None;
        }

        /// <summary>Human-readable refusal for the caster's chat window.</summary>
        public static string Describe(NanoCastRefusal refusal) => refusal switch
        {
            NanoCastRefusal.CasterDead => "You cannot cast nano programs while dead.",
            NanoCastRefusal.CasterStunned => "You cannot cast nano programs while stunned.",
            NanoCastRefusal.AlreadyCasting => "You are already casting a nano program.",
            NanoCastRefusal.Recharging => "Your nano programs are still recharging.",
            NanoCastRefusal.NotUploaded => "You do not have that nano program uploaded.",
            NanoCastRefusal.RequirementsNotMet => "You do not meet the requirements for that nano program.",
            NanoCastRefusal.NotEnoughNano => "You do not have enough nano energy.",
            NanoCastRefusal.NotEnoughNcu => "Not enough NCU.",
            NanoCastRefusal.InvalidTarget => "That is not a valid target for that nano program.",
            NanoCastRefusal.TargetDead => "Your target is dead.",
            NanoCastRefusal.NanoLineLocked => "That nano line is still cooling down.",
            NanoCastRefusal.BetterNanoRunning => "NCU error: Better nano program already running.",
            _ => string.Empty,
        };
    }

    /// <summary>Resolved inputs for one <see cref="NanoCastRules.Evaluate"/> call.</summary>
    public readonly struct NanoCastAttempt
    {
        public bool CasterIsDead { get; init; }

        public bool CasterIsStunned { get; init; }

        public bool CasterIsCasting { get; init; }

        public bool CasterIsRecharging { get; init; }

        /// <summary>The caster's lock on this nano's strain has not run out.</summary>
        public bool NanoLineLocked { get; init; }

        public bool IsUploaded { get; init; }

        public bool RequirementsMet { get; init; }

        public bool TargetExists { get; init; }

        public bool TargetIsDead { get; init; }

        public int CurrentNano { get; init; }

        public int NanoCost { get; init; }
    }
}
