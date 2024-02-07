using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using ManagedNativeWifi;
using Microsoft.Win32;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace MqttWinSensor
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private readonly MqttBinarySensor mqttBinarySensor;
        private readonly TeamsPresenceIndicator? teamsPresenceIndicator;
        private readonly CancellationTokenSource cancellationTokenSource = new();
        private readonly DispatcherTimer dispatcherTimer = new();
        private string lastToolTipText = string.Empty;
        private string hyperionRemotePath = string.Empty;

        public App()
        {
            bool isCheckForPower = "true".Equals(ConfigurationManager.AppSettings["check_if_on_power"], StringComparison.OrdinalIgnoreCase);
            bool isCheckForWifi = "true".Equals(ConfigurationManager.AppSettings["check_if_on_wireless_network"], StringComparison.OrdinalIgnoreCase);
            string brokerUri = ConfigurationManager.AppSettings["mqtt_broker_uri"] ?? string.Empty;
            string brokerUser = ConfigurationManager.AppSettings["mqtt_broker_user"] ?? string.Empty;
            string brokerPassword = ConfigurationManager.AppSettings["mqtt_broker_pwd"] ?? string.Empty;
            string expireAfterText = ConfigurationManager.AppSettings["mqtt_expire_after"] ?? "600";
            string wifiNetworksText = ConfigurationManager.AppSettings["wireless_networks"] ?? string.Empty;
            string wifiTextDelimiter = ConfigurationManager.AppSettings["wireless_networks_delimiter"] ?? ";";
            int expireAfter = -1;
            bool isMonitorTeamsPresence = "true".Equals(ConfigurationManager.AppSettings["monitor_teams_presence"], StringComparison.OrdinalIgnoreCase);
            string monitorIntervalText = ConfigurationManager.AppSettings["monitor_teams_interval"] ?? "600";
            int monitorInterval = -1;
            hyperionRemotePath = ConfigurationManager.AppSettings["hyperion_remote_path"] ?? string.Empty;

            try
            {
                expireAfter = Convert.ToInt32(expireAfterText);
                dispatcherTimer.Interval = TimeSpan.FromSeconds((expireAfter > 1 ? Math.Max(1, expireAfter) : 0) / 2);
            }
            catch
            {
                dispatcherTimer.Interval = TimeSpan.Zero;
            }

            try
            {
                monitorInterval = Convert.ToInt32(monitorIntervalText);
            }
            catch
            {
            }

            mqttBinarySensor = new MqttBinarySensor(new MqttBinarySensorOptions
            {
                BrokerUri = brokerUri,
                BrokerUsername = brokerUser,
                BrokerPassword = brokerPassword,
                ExpireAfter = expireAfter,
                IsCheckForPower = isCheckForPower,
                IsCheckForWifi = isCheckForWifi,
                WifiNetworks = wifiNetworksText.Split(wifiTextDelimiter, StringSplitOptions.RemoveEmptyEntries),
            });

            if (isMonitorTeamsPresence)
            {
                teamsPresenceIndicator = new TeamsPresenceIndicator(new TeamsPresenceIndicatorOptions
                {
                    BrokerUri = brokerUri,
                    BrokerUsername = brokerUser,
                    BrokerPassword = brokerPassword,
                    ExpireAfter = expireAfter,
                    IsCheckForPower = isCheckForPower,
                    IsCheckForWifi = isCheckForWifi,
                    WifiNetworks = wifiNetworksText.Split(wifiTextDelimiter, StringSplitOptions.RemoveEmptyEntries),
                    PollingInterval = monitorInterval,
                });
                teamsPresenceIndicator.Start(cancellationTokenSource.Token);
            }

            if (!Path.Exists(hyperionRemotePath) || !hyperionRemotePath.EndsWith("hyperion-remote.exe"))
            {
                hyperionRemotePath = string.Empty;
            }

        }

        /// <summary>
        /// Update the notification icon's tooltip text. Also save the text in case we are not able to 
        /// update the icon yet.
        /// </summary>
        /// <param name="text"></param>
        public void SetTooltip(string? text = null)
        {
            if (string.IsNullOrEmpty(text))
                text = lastToolTipText;
            else
            {
                Trace.TraceInformation("Reason: " + text);
                lastToolTipText = text;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (MainWindow is MainWindow wnd)
                {
                    wnd.TaskbarIcon.ToolTipText = "MQTT Sensor: " + Environment.MachineName + (text == null ? "" : " | " + text);
                }
            }));
        }

        /// <summary>
        /// When starting up, register for the session events and start the dispatcher timer.
        /// </summary>
        /// <param name="e"></param>
        protected override async void OnStartup(StartupEventArgs e)
        {
            await UpdateStateAsync(true, "Started");

            SystemEvents.SessionSwitch += SystemEvents_SessionSwitch;

            dispatcherTimer.Tick += DispatcherTimer_Tick;
            if (dispatcherTimer.Interval > TimeSpan.Zero)
                dispatcherTimer.Start();

            await RunHyperionRemote(true);
        }

        protected override async void OnExit(ExitEventArgs e)
        {
            base.OnExit(e);
            dispatcherTimer.Stop();
            teamsPresenceIndicator?.Stop();

            Task.Run(async () =>
            {
                await UpdateStateAsync(false, "Exited");
            }).Wait(cancellationTokenSource.Token);

            await RunHyperionRemote(false);

            cancellationTokenSource.Cancel();
            SystemEvents.SessionSwitch -= SystemEvents_SessionSwitch;
        }

        private async Task<bool> UpdateStateAsync(bool isEnabled, string reason)
        {
            SetTooltip(reason);
            return await mqttBinarySensor.UpdateStateAsync(isEnabled, cancellationTokenSource.Token);
        }

        private async void SystemEvents_SessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            bool isEnabled = false;
            string reason = string.Empty;
            switch (e.Reason)
            {
                case SessionSwitchReason.SessionLogon: isEnabled = true; reason = "Logged On"; break;
                case SessionSwitchReason.SessionLogoff: isEnabled = false; reason = "Logged Off"; break;
                case SessionSwitchReason.SessionLock: isEnabled = false; reason = "Locked"; break;
                case SessionSwitchReason.SessionUnlock: isEnabled = true; reason = "Unlocked"; break;
            }

            if ((await UpdateStateAsync(isEnabled, reason)))
            {
                await RunHyperionRemote(isEnabled);
            }
        }

        private async Task RunHyperionRemote(bool isEnabled)
        {
            int screenCount = System.Windows.Forms.Screen.AllScreens.Length;

            if (string.IsNullOrEmpty(hyperionRemotePath) || screenCount == 1)
                return;

            var startInfo = new ProcessStartInfo(hyperionRemotePath)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                Arguments = isEnabled ? "--resume" : "--suspend"
            };
            try
            {
                using Process? process = Process.Start(startInfo);
                if (process != null)
                {
                    await process.WaitForExitAsync(cancellationTokenSource.Token);
                    string output = process.StandardOutput.ReadToEnd();
                    Trace.TraceInformation(output);
                }
            }
            catch { }
        }

        private async void DispatcherTimer_Tick(object? sender, EventArgs e)
        {
            if (mqttBinarySensor.IsRegistered)
                await mqttBinarySensor.ResendStateAsync(cancellationTokenSource.Token);
        }
    }
}
