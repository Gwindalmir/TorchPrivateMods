using System.Windows;

namespace Phoenix.Torch.Plugin.PrivateMods
{
    /// <summary>
    /// Interaction logic for InputBox.xaml
    /// </summary>
    public partial class InputBox : Window
    {
        public InputBox()
        {
            InitializeComponent();
        }

        public InputBox(string prompt, string title = null, string prefilledText = null) : this()
        {
            inputPrompt.Content = prompt;

            if (title != null)
                this.Title = title;

            inputText.Text = prefilledText;
        }

        private void OK_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = true;
            this.Close();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }

        public string InputText
        {
            get { return inputText.Text; }
        }
    }
}
