using System.Windows.Forms;

namespace Advanced_Combat_Tracker
{
    // ACT's tray-corner notification slider. In real ACT this slides a toast in from the system-tray
    // corner with up to four choice buttons; plugins construct it, set TrayTitle/TrayText, and call
    // ShowTraySlider to surface a message. FFXIV_ACT_Plugin's ProblemDiagnosisHelper.Diagnose uses it
    // to show the "Game Connection" troubleshooting result. The type exists so that IL binds against
    // our facade; the message is surfaced as a modal dialog and through the notification sink.
    public class TraySlider : Form
    {
        public enum ButtonLayoutEnum
        {
            OneButton,
            TwoButton,
            FourButton
        }

        private readonly Label lblTitle = new Label();
        private readonly Label lblText = new Label();
        private readonly Button btn0 = new Button();
        private readonly Button btn1 = new Button();
        private readonly Button btn2 = new Button();
        private readonly Button btn3 = new Button();
        private readonly Button btn4 = new Button();

        public Label TrayTitle => lblTitle;
        public Label TrayText => lblText;
        public Button ButtonNW => btn1;
        public Button ButtonNE => btn2;
        public Button ButtonSW => btn3;
        public Button ButtonSE => btn4;
        public Button ButtonOK => btn0;

        public ButtonLayoutEnum ButtonLayout { get; set; }
        public bool AddNotification { get; set; } = true;
        public int ShowDurationMs { get; set; } = 15000;
        public bool ForceShow { get; set; }

        public TraySlider()
        {
            // Stays a valid but never-shown Form; ShowTraySlider surfaces the message itself.
            ShowInTaskbar = false;
            ShowIcon = false;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
        }

        public void ShowTraySlider(string Message, string Title = "")
        {
            lblText.Text = Message;
            lblTitle.Text = Title;
            ShowTraySlider();
        }

        public void ShowTraySlider()
        {
            var title = string.IsNullOrEmpty(lblTitle.Text) ? "FFXIV Combat Tracker" : lblTitle.Text;
            try
            {
                MessageBox.Show(lblText.Text ?? "", title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch { }
            if (AddNotification)
            {
                try { ActGlobals.oFormActMain?.NotificationAdd(title, lblText.Text); } catch { }
            }
        }
    }
}
