namespace AORebirth.World.Contracts
{
    using AORebirth.World.Contracts.V1;

    public static class LeaseTokens
    {
        /// <summary>Tuple order: epoch first, then generation. Null sorts lowest.</summary>
        public static int Compare(LeaseToken? left, LeaseToken? right)
        {
            if (left == null) return right == null ? 0 : -1;
            if (right == null) return 1;
            int epoch = left.Epoch.CompareTo(right.Epoch);
            return epoch != 0 ? epoch : left.Generation.CompareTo(right.Generation);
        }

        public static bool SameAs(LeaseToken? left, LeaseToken? right) => left != null && right != null && Compare(left, right) == 0;

        public static string Describe(LeaseToken? token)
            => token == null ? "none" : token.Epoch.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/"
                + token.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
