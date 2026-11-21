namespace ZoneEngine_New.Core.Helpers
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json;

    using LostEden.Protocol.GameData;

    /// <summary>
    /// The weapon damage attack rating multiplier, from GameData/ArMultipliers.json. Up to the soft cap (AR 1000) every
    /// profession uses AR / 400 + 1. Past it each profession follows its own measured curve: the samples are linearly
    /// interpolated from the soft cap on, and past the last sample the multiplier holds (the hardcap). No closed formula
    /// fits the curves, so the table is the rule. A profession without a curve (Monster, None) gains a flat
    /// fallbackGainPer400 per 400 AR past the soft cap; 1.0 keeps NPCs on AR / 400 + 1 with no soft cap (live: mob
    /// damage scales linearly with Add All Off. debuffs well past AR 1000). Without the file every profession uses that
    /// flat gain.
    /// </summary>
    public static class ArMultiplierTable
    {
        public const string FileName = "ArMultipliers.json";

        sealed record Table(double SoftCapAr, double SoftCapMultiplier, double ArPerPoint, double FallbackGainPer400,
            Dictionary<int, (double Ar, double Multiplier)[]> Curves);

        sealed class Document
        {
            public SoftCapData? SoftCap { get; set; }

            public double? FallbackGainPer400 { get; set; }

            public List<ProfessionData> Professions { get; set; } = [];
        }

        sealed class SoftCapData
        {
            public double Ar { get; set; }

            public double Multiplier { get; set; }

            public double ArPerPoint { get; set; }
        }

        sealed class ProfessionData
        {
            public string Id { get; set; } = string.Empty;

            public double[][] Points { get; set; } = [];
        }

        static readonly Table Default = new(1000, 3.5, 400, 1.0, []);

        static readonly object Gate = new();
        static string? _loadedFrom;
        static Table _table = Default;

        /// <summary>The damage multiplier for an attacker of <paramref name="profession"/> (the Profession stat) at <paramref name="attackRating"/>.</summary>
        public static double For(string? gameDataRoot, int profession, int attackRating)
        {
            Table table = gameDataRoot != null ? Load(gameDataRoot) : Default;
            double ar = Math.Max(0, attackRating);
            if (ar <= table.SoftCapAr)
                return 1 + (ar / table.ArPerPoint);

            if (!table.Curves.TryGetValue(profession, out (double Ar, double Multiplier)[]? curve))
                return table.SoftCapMultiplier + ((ar - table.SoftCapAr) * table.FallbackGainPer400 / 400);

            if (ar >= curve[^1].Ar)
                return curve[^1].Multiplier;

            // The first sample past the soft cap; the soft cap itself is the segment's start before the first sample.
            int upper = Array.FindIndex(curve, point => point.Ar > ar);
            (double lowAr, double lowMultiplier) = upper == 0
                ? (table.SoftCapAr, table.SoftCapMultiplier)
                : curve[upper - 1];
            (double highAr, double highMultiplier) = curve[upper];
            double t = (ar - lowAr) / (highAr - lowAr);
            return lowMultiplier + ((highMultiplier - lowMultiplier) * t);
        }

        static Table Load(string gameDataRoot)
        {
            string path = Path.Combine(gameDataRoot, FileName);
            lock (Gate)
            {
                if (string.Equals(_loadedFrom, path, StringComparison.OrdinalIgnoreCase))
                    return _table;

                _table = File.Exists(path) ? Parse(path) : Default;
                _loadedFrom = path;
                return _table;
            }
        }

        static Table Parse(string path)
        {
            Document? document;
            try
            {
                document = JsonSerializer.Deserialize<Document>(
                    File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(path + " is not valid JSON.", exception);
            }

            SoftCapData softCap = document?.SoftCap ?? throw new InvalidDataException(path + ": softCap is required.");
            if (!(softCap.Ar > 0) || !(softCap.Multiplier > 0) || !(softCap.ArPerPoint > 0))
                throw new InvalidDataException(path + ": softCap ar, multiplier and arPerPoint must be positive.");
            double fallbackGain = document.FallbackGainPer400 ?? Default.FallbackGainPer400;
            if (!double.IsFinite(fallbackGain) || fallbackGain < 0)
                throw new InvalidDataException(path + ": fallbackGainPer400 must be a finite, non-negative number.");

            var professionsByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (Profession value in Enum.GetValues<Profession>())
                professionsByName[Letters(value.ToString())] = (int)value;

            var curves = new Dictionary<int, (double Ar, double Multiplier)[]>();
            foreach (ProfessionData entry in document.Professions)
            {
                if (!professionsByName.TryGetValue(Letters(entry.Id), out int profession))
                    throw new InvalidDataException(path + ": profession id '" + entry.Id + "' is not a profession.");
                if (curves.ContainsKey(profession))
                    throw new InvalidDataException(path + ": profession '" + entry.Id + "' is listed twice.");
                if (entry.Points.Length == 0)
                    throw new InvalidDataException(path + ": profession '" + entry.Id + "' has no points.");

                var curve = new (double Ar, double Multiplier)[entry.Points.Length];
                double previousAr = softCap.Ar;
                for (int i = 0; i < entry.Points.Length; i++)
                {
                    double[] point = entry.Points[i];
                    if (point is not { Length: 2 } || !double.IsFinite(point[0]) || !double.IsFinite(point[1]) || point[1] <= 0)
                        throw new InvalidDataException(path + ": profession '" + entry.Id + "' point " + i + " must be [ar, multiplier].");
                    if (point[0] <= previousAr)
                        throw new InvalidDataException(path + ": profession '" + entry.Id + "' points must rise in AR past the soft cap.");
                    previousAr = point[0];
                    curve[i] = (point[0], point[1]);
                }

                curves[profession] = curve;
            }

            return new Table(softCap.Ar, softCap.Multiplier, softCap.ArPerPoint, fallbackGain, curves);
        }

        static string Letters(string text)
        {
            var letters = new System.Text.StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (char.IsLetter(c))
                    letters.Append(c);
            }

            return letters.ToString();
        }
    }
}
