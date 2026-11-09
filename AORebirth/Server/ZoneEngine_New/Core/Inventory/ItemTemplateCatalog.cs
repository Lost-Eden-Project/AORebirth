namespace ZoneEngine_New.Core.Inventory
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;

    using AORebirth.Core.GameData;
    using AORebirth.Modules.Content;

    using ZoneEngine_New.Core.GameData;
    using ZoneEngine_New.Core.Inventory.Dat;
    using ZoneEngine_New.Core.Logging;

    /// <summary>
    /// GameData items.dat is the only source of item templates, names included. Read through the GameData override folder
    /// (<see cref="GameDataPaths.ResolveFile"/>) and swapped whole on <see cref="Reload"/>, so a lookup sees the old catalog
    /// or the new, never a mix. A reload that fails to read, or reads no templates, keeps the old catalog. Items already
    /// built keep the definition they were built from (<see cref="Item.Definition"/>) until they are built again.
    /// </summary>
    public sealed class ItemTemplateCatalog : IItemTemplateCatalog
    {
        /// <summary>The name the catalog reloads under.</summary>
        public const string ReloadName = "ItemsDat";

        public const string FileName = GameDataPaths.ItemsFileName;

        private readonly IZoneLogger _logger;
        private readonly string _root;
        private volatile Dictionary<int, ItemTemplate> _templates = new();

        public ItemTemplateCatalog(IGameData gameData, IZoneLogger logger)
        {
            ArgumentNullException.ThrowIfNull(gameData);
            ArgumentNullException.ThrowIfNull(logger);
            _logger = logger;
            _root = gameData.RootPath;

            GameDataReloadResult loaded = Reload();
            foreach (string problem in loaded.Problems)
                _logger.Warn("ItemTemplateCatalog: " + problem + "; item catalog is empty");

            _logger.Info(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "ItemTemplateCatalog ready with {0} templates",
                    _templates.Count));
        }

        public IReadOnlyCollection<ItemTemplate> Templates => _templates.Values;

        public int Count => _templates.Count;

        public bool TryGet(int aoid, out ItemTemplate template)
            => _templates.TryGetValue(aoid, out template!);

        public ItemTemplate Require(int aoid)
        {
            if (TryGet(aoid, out ItemTemplate template))
                return template;

            throw new KeyNotFoundException(
                string.Format(CultureInfo.InvariantCulture, "Item template {0} not found", aoid));
        }

        /// <summary>Reads items.dat again and swaps it in whole. Off the playfield tick: it reads a large file.</summary>
        public GameDataReloadResult Reload()
        {
            string path = GameDataPaths.ResolveFile(_root, FileName);
            if (!File.Exists(path))
                return new GameDataReloadResult(ReloadName, false, _templates.Count, [FileName + " not found at " + path]);

            Dictionary<int, ItemTemplate> next;
            try
            {
                List<DatItemTemplate> loaded = ItemsDatReader.Read(path);
                next = new Dictionary<int, ItemTemplate>(loaded.Count);
                foreach (DatItemTemplate dat in loaded)
                    next[dat.ID] = DatItemMapper.ToTemplate(dat, dat.Name ?? string.Empty);
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "Failed to load GameData items.dat from " + path);
                return new GameDataReloadResult(ReloadName, false, _templates.Count,
                    [FileName + ": " + exception.GetType().Name + ": " + exception.Message]);
            }

            if (next.Count == 0)
                return new GameDataReloadResult(ReloadName, false, _templates.Count, [FileName + " at " + path + " holds no templates"]);

            _templates = next;
            _logger.Info(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Loaded {0} item templates from {1}",
                    next.Count,
                    path));
            return new GameDataReloadResult(ReloadName, true, next.Count, []);
        }
    }
}
