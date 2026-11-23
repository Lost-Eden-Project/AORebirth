namespace ZoneEngine_New.Core.Characters
{
    using System.Collections.Frozen;
    using System.Collections.Generic;

    /// <summary>
    /// The skin race (stat Race) a Solitus head implies. The client builds the same fixed table at startup
    /// (Gamecode FUN_100c3012 from the mesh names head_solitus{male,female}_{african,asian}NN.abiff) and skins the
    /// body with VisualCATMesh_t::SetSkinData(Breed, Sex, Race). Its full spawn looks the head up, but its appearance
    /// refresh (FUN_1007eb09, on every cloth/texture change) reads stat Race as sent, so a wrong Race paints a dark
    /// head onto a light body. Character creation sends no race (Client_t::CreateCharacter takes breed, sex,
    /// profession, head, scale and fatness), so the server derives it from the head the same way.
    /// </summary>
    public static class HeadSkinRace
    {
        /// <summary>Race for every head not in the table, and the stat's legacy default.</summary>
        public const int Default = 1;

        public const int African = 2;

        public const int Asian = 3;

        // Mesh ids from the client resource database (type 1010001).
        static readonly FrozenDictionary<int, int> ByHead = new Dictionary<int, int>
        {
            // head_solitusfemale_african00..07
            [40625] = African, [40615] = African, [40616] = African, [40617] = African,
            [40618] = African, [40619] = African, [40620] = African, [40621] = African,
            // head_solitusmale_african00..08
            [40685] = African, [40667] = African, [40668] = African, [40669] = African, [40670] = African,
            [40671] = African, [40672] = African, [40673] = African, [40674] = African,
            // head_solitusfemale_asian00..06
            [40622] = Asian, [40623] = Asian, [40624] = Asian, [40614] = Asian,
            [40626] = Asian, [40627] = Asian, [40628] = Asian,
            // head_solitusmale_asian00..08
            [40675] = Asian, [40676] = Asian, [40677] = Asian, [40678] = Asian, [40679] = Asian,
            [40680] = Asian, [223826] = Asian, [223828] = Asian, [223829] = Asian,
        }.ToFrozenDictionary();

        /// <summary>The race <paramref name="headMesh"/> implies, or false for a head with no skin race of its own.</summary>
        public static bool TryGet(int headMesh, out int race) => ByHead.TryGetValue(headMesh, out race);
    }
}
