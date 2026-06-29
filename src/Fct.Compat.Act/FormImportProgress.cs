using System.Windows.Forms;

namespace Advanced_Combat_Tracker
{
    // ACT's bulk-log-import progress dialog. In real ACT this drives a progress bar while a
    // historical log file is imported; OverlayPlugin reads ActGlobals.oFormImportProgress.Visible
    // to suppress live DPS pushes mid-import (MiniParseEventSource). Our host tails/replays live
    // logs and never runs an interactive import, so the instance stays null and Visible is never
    // true. The type exists so OverlayPlugin's field reference binds against our facade.
    public class FormImportProgress : Form
    {
    }
}
