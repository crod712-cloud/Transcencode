namespace HandBrakeWPF.Views
{
    using System.Windows.Controls;

    public partial class TranscencodeLiveEngineView : UserControl
    {
        public TranscencodeLiveEngineView()
        {
            this.InitializeComponent();
        }

        private void LogBox_OnTextChanged(object sender, TextChangedEventArgs e)
        {
            this.LogBox.ScrollToEnd();
        }
    }
}
