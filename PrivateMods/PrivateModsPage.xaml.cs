using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace Phoenix.Torch.Plugin.PrivateMods
{
    /// <summary>
    /// Interaction logic for PrivateModsPage.xaml
    /// </summary>
    public partial class PrivateModsPage : UserControl
    {
        public PrivateModsPage()
        {
            InitializeComponent();
        }

        private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            // Keep password secure by doing manual binding
            if (this.DataContext != null)
                ((PrivateModsPlugin)this.DataContext).SteamPassword = ((PasswordBox)sender).SecurePassword;
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog()
            {
                CheckFileExists = true,
                CheckPathExists = true,
                FileName = "steamcmd.exe",
                Filter = "steamcmd.exe|steamcmd.exe",
                InitialDirectory = System.IO.Path.GetDirectoryName(Assembly.GetEntryAssembly().Location),
                Title = "Browse for SteamCMD.exe"
            };

            var result = dialog.ShowDialog();
            if( result == true)
            {
                pathTextBox.Text = dialog.FileName;
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            ((PrivateModsPlugin)this.DataContext).SaveSettings();
        }
    }
}
