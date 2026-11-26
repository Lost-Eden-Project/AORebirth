namespace ZoneEngine_New.Core.GameData
{
    using System;
    using System.Collections.Generic;
    using System.IO;

    /// <summary>
    /// GameData ItemGroups.csv: every template of one item line under one group number (Perennium Sniper, group 3405, is
    /// 246408..246414 across QL 50-200). Columns are named by the header row (<c>group</c>, <c>item_id</c>, optional
    /// <c>in_game</c>); a row with <c>in_game</c> 0 is not an item a player can have and is left out. A template listed in
    /// two groups belongs to neither, so an unclear line never changes what is made. A missing file is an empty catalog.
    /// </summary>
    public sealed class ItemLineCatalog
    {
        public const string FileName = "ItemGroups.csv";

        public static readonly ItemLineCatalog Empty = new(new Dictionary<int, int[]>());

        readonly Dictionary<int, int[]> _lines;

        ItemLineCatalog(Dictionary<int, int[]> lines) => _lines = lines;

        /// <summary>Templates that belong to a line.</summary>
        public int Count => _lines.Count;

        /// <summary>Every template of the line <paramref name="templateId"/> belongs to, itself included.</summary>
        public bool TryGetLine(int templateId, out IReadOnlyList<int> line)
        {
            if (_lines.TryGetValue(templateId, out int[]? found))
            {
                line = found;
                return true;
            }

            line = Array.Empty<int>();
            return false;
        }

        /// <summary>Reads ItemGroups.csv through the GameData override folder. Reads a file: never on a playfield tick.</summary>
        public static ItemLineCatalog Load(string gameDataRoot)
        {
            string path = AORebirth.Core.GameData.GameDataPaths.ResolveFile(gameDataRoot, FileName);
            return File.Exists(path) ? Parse(File.ReadLines(path)) : Empty;
        }

        public static ItemLineCatalog Parse(IEnumerable<string> rows)
        {
            ArgumentNullException.ThrowIfNull(rows);
            int groupColumn = -1, idColumn = -1, inGameColumn = -1;
            var groups = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            var groupOf = new Dictionary<int, string?>();
            bool header = true;
            foreach (string row in rows)
            {
                if (string.IsNullOrWhiteSpace(row))
                    continue;
                string[] cells = row.Split(',');
                if (header)
                {
                    header = false;
                    groupColumn = Array.FindIndex(cells, c => c.Trim().Equals("group", StringComparison.OrdinalIgnoreCase));
                    idColumn = Array.FindIndex(cells, c => c.Trim().Equals("item_id", StringComparison.OrdinalIgnoreCase));
                    inGameColumn = Array.FindIndex(cells, c => c.Trim().Equals("in_game", StringComparison.OrdinalIgnoreCase));
                    if (groupColumn < 0 || idColumn < 0)
                        throw new InvalidDataException(FileName + " needs group and item_id columns.");
                    continue;
                }

                if (cells.Length <= Math.Max(groupColumn, idColumn)
                    || !int.TryParse(cells[idColumn].Trim(), out int id) || id <= 0
                    || (inGameColumn >= 0 && inGameColumn < cells.Length && cells[inGameColumn].Trim() == "0"))
                    continue;

                string group = cells[groupColumn].Trim();
                if (group.Length == 0)
                    continue;
                if (groupOf.TryGetValue(id, out string? existing))
                {
                    if (existing != null && !string.Equals(existing, group, StringComparison.Ordinal))
                        groupOf[id] = null;
                    continue;
                }

                groupOf[id] = group;
                if (!groups.TryGetValue(group, out List<int>? members))
                    groups[group] = members = new List<int>();
                members.Add(id);
            }

            var lines = new Dictionary<int, int[]>();
            foreach (List<int> members in groups.Values)
            {
                int[] line = members.FindAll(id => groupOf[id] != null).ToArray();
                if (line.Length < 2)
                    continue;
                foreach (int id in line)
                    lines[id] = line;
            }

            return new ItemLineCatalog(lines);
        }
    }
}
