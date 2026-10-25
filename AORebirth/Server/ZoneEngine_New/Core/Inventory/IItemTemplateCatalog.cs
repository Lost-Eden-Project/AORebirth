namespace ZoneEngine_New.Core.Inventory
{
    using System;
    using System.Collections.Generic;

    public interface IItemTemplateCatalog
    {
        /// <summary>All actual catalog records, without synthesizing unknown-template fallbacks.</summary>
        IReadOnlyCollection<ItemTemplate> Templates => Array.Empty<ItemTemplate>();

        bool TryGet(int aoid, out ItemTemplate template);

        ItemTemplate Require(int aoid);
    }
}
