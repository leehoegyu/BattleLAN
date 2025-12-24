using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace BattleLAN
{
    public partial class MainWindow : Window
    {
        private VirtualLAN virtualLAN;
        private const string ConfigFileName = "receivers.txt";

        public MainWindow()
        {
            InitializeComponent();
            virtualLAN = new VirtualLAN();
            LoadReceivers();
            UpdateUI();
        }

        private void IpAddressTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            string text = IpAddressTextBox.Text.Trim();
            AddButton.IsEnabled = !string.IsNullOrEmpty(text) && IsValidIP(text);
        }

        private void IpAddressTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && AddButton.IsEnabled)
            {
                AddButton_Click(sender, e);
            }
        }

        private bool IsValidIP(string ip)
        {
            return System.Net.IPAddress.TryParse(ip, out _);
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            string ip = IpAddressTextBox.Text.Trim();
            if (string.IsNullOrEmpty(ip) || !IsValidIP(ip))
            {
                MessageBox.Show("Please enter a valid IP address.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (virtualLAN.AddReceiver(ip))
            {
                UpdateReceiversList();
                IpAddressTextBox.Clear();
                AddButton.IsEnabled = false;
            }
            else
            {
                MessageBox.Show("Failed to add IP address.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RemoveButton_Click(object sender, RoutedEventArgs e)
        {
            if (ReceiversListBox.SelectedItem is string selectedIp)
            {
                virtualLAN.RemoveReceiver(selectedIp);
                UpdateReceiversList();
                RemoveButton.IsEnabled = false;
            }
        }

        private void ReceiversListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            RemoveButton.IsEnabled = ReceiversListBox.SelectedItem != null;
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (virtualLAN.GetReceivers().Count == 0)
                {
                    MessageBox.Show("At least one receiver IP must be added.", "Information", 
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                virtualLAN.Start();
                UpdateUI();
                UpdateStatus("Running", true);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to start VirtualLAN:\n{ex.Message}\n\nPlease ensure the application is running with administrator privileges.", 
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                virtualLAN.Stop();
                UpdateUI();
                UpdateStatus("Stopped", false);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to stop VirtualLAN:\n{ex.Message}", 
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SaveReceivers();
                MessageBox.Show("Receiver list saved successfully.", "Save Complete", 
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Save failed:\n{ex.Message}", 
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ExitButton_Click(object sender, RoutedEventArgs e)
        {
            if (virtualLAN.IsRunning)
            {
                var result = MessageBox.Show("VirtualLAN is running. Do you want to exit?", 
                    "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    virtualLAN.Stop();
                    Close();
                }
            }
            else
            {
                Close();
            }
        }

        private void UpdateReceiversList()
        {
            var receivers = virtualLAN.GetReceivers();
            ReceiversListBox.Items.Clear();
            foreach (var receiver in receivers)
            {
                ReceiversListBox.Items.Add(receiver);
            }
        }

        private void UpdateUI()
        {
            bool isRunning = virtualLAN.IsRunning;
            StartButton.IsEnabled = !isRunning;
            StopButton.IsEnabled = isRunning;
            AddButton.IsEnabled = !isRunning && !string.IsNullOrEmpty(IpAddressTextBox.Text.Trim()) 
                && IsValidIP(IpAddressTextBox.Text.Trim());
            RemoveButton.IsEnabled = !isRunning && ReceiversListBox.SelectedItem != null;
            IpAddressTextBox.IsEnabled = !isRunning;
            ReceiversListBox.IsEnabled = !isRunning;
        }

        private void UpdateStatus(string status, bool isRunning)
        {
            StatusTextBlock.Text = status;
            StatusIndicator.Fill = isRunning 
                ? new SolidColorBrush(Color.FromRgb(123, 31, 162))
                : new SolidColorBrush(Color.FromRgb(189, 189, 189));
        }

        private void SaveReceivers()
        {
            try
            {
                var receivers = virtualLAN.GetReceivers();
                File.WriteAllLines(ConfigFileName, receivers);
            }
            catch (Exception ex)
            {
                throw new Exception($"Error occurred while saving receiver list: {ex.Message}");
            }
        }

        private void LoadReceivers()
        {
            try
            {
                if (File.Exists(ConfigFileName))
                {
                    var receivers = File.ReadAllLines(ConfigFileName)
                        .Where(line => !string.IsNullOrWhiteSpace(line) && IsValidIP(line.Trim()));
                    
                    foreach (var receiver in receivers)
                    {
                        virtualLAN.AddReceiver(receiver.Trim());
                    }
                    
                    UpdateReceiversList();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error occurred while loading receiver list:\n{ex.Message}", 
                    "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            if (virtualLAN != null)
            {
                virtualLAN.Stop();
                virtualLAN.Dispose();
            }
            base.OnClosed(e);
        }
    }
}

