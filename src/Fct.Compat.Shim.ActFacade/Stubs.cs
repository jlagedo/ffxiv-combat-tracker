namespace Advanced_Combat_Tracker
{
    /// <summary>
    /// Behavior-free stand-in for ACT's bulk-import progress dialog. <c>ActGlobals.oFormImportProgress</c>
    /// stays null in the net10 host (nothing imports); this type exists only so the field has a type
    /// and a plugin's <c>oFormImportProgress?.Visible</c> read compiles.
    /// </summary>
    public sealed class FormImportProgress
    {
        public bool Visible { get; set; }
    }

    /// <summary>
    /// Behavior-free stand-in for ACT's built-in spell-timer engine. Kept non-null but inert — the
    /// spell-timer / custom-trigger subsystems are out of the compat shim's data path (they stay
    /// stubbed, as the net48 facade stubs them).
    /// </summary>
    public sealed class FormSpellTimers
    {
    }
}
