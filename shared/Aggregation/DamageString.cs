namespace Advanced_Combat_Tracker
{
    // ACT's number renderer for the "-*" / "-k|m|b" ExportVariables: optional K/M/B/T/Q suffix
    // (two decimals when UseDecimals, integer division otherwise); plain "{Damage}" with no suffix
    // or below the smallest threshold. NaN/Infinity sentinels surface verbatim. Stateless, so the
    // aggregation engine (and both facades' FormActMain) call it directly instead of reaching through
    // ActGlobals.oFormActMain.
    internal static class DamageString
    {
        public static string Create(long damage, bool useSuffix, bool useDecimals)
        {
            if (damage == long.MinValue) return float.NaN.ToString();
            if (damage == long.MaxValue) return float.PositiveInfinity.ToString();
            if (damage < 0) return new Dnum(damage).ToString();
            if (useSuffix)
            {
                if (useDecimals)
                {
                    if (damage >= 1000000000000000L) return $"{(double)damage / 1000000000000000.0:0.00}Q";
                    if (damage >= 1000000000000L) return $"{(double)damage / 1000000000000.0:0.00}T";
                    if (damage >= 1000000000L) return $"{(double)damage / 1000000000.0:0.00}B";
                    if (damage >= 1000000L) return $"{(double)damage / 1000000.0:0.00}M";
                    if (damage >= 1000L) return $"{(double)damage / 1000.0:0.00}K";
                }
                else
                {
                    if (damage >= 10000000000000000L) return $"{damage / 1000000000000000L}Q";
                    if (damage >= 10000000000000L) return $"{damage / 1000000000000L}T";
                    if (damage >= 10000000000L) return $"{damage / 1000000000L}B";
                    if (damage >= 10000000L) return $"{damage / 1000000L}M";
                    if (damage >= 10000L) return $"{damage / 1000L}K";
                }
            }
            return $"{damage}";
        }
    }
}
