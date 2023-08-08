using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.Drawing;
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
        private readonly CancellationTokenSource cancellationTokenSource = new();
        private readonly DispatcherTimer dispatcherTimer = new DispatcherTimer();
        private string lastToolTipText = string.Empty;

        public App()
        {
            bool isCheckForPower = "true".Equals(ConfigurationManager.AppSettings["check_if_on_power"], StringComparison.OrdinalIgnoreCase);
            bool isCheckForWifi = "true".Equals(ConfigurationManager.AppSettings["check_if_on_wireless_network"], StringComparison.OrdinalIgnoreCase);
            string brokerUri = ConfigurationManager.AppSettings["mqtt_broker_uri"] ?? string.Empty;
            string brokerUser = ConfigurationManager.AppSettings["mqtt_broker_user"] ?? string.Empty;
            string brokerPassword = ConfigurationManager.AppSettings["mqtt_broker_pwd"] ?? string.Empty;
            string expireAfterText = ConfigurationManager.AppSettings["mqtt_expire_after"] ?? string.Empty;
            string wifiNetworksText = ConfigurationManager.AppSettings["wireless_networks"] ?? string.Empty;
            string wifiTextDelimiter = ConfigurationManager.AppSettings["wireless_networks_delimiter"] ?? string.Empty;
            int expireAfter = -1;

            try
            {
                expireAfter = Convert.ToInt32(expireAfterText);
                dispatcherTimer.Interval = TimeSpan.FromSeconds((expireAfter > 1 ? Math.Max(1, expireAfter) : 0) / 2);
            }
            catch
            {
                dispatcherTimer.Interval = TimeSpan.Zero;
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
        }

        protected override void OnExit(ExitEventArgs e)
        {
            base.OnExit(e);
            dispatcherTimer.Stop();

            Task.Run(async () =>
            {
                await UpdateStateAsync(false, "Exited");
            }).Wait(cancellationTokenSource.Token);

            cancellationTokenSource.Cancel();
            SystemEvents.SessionSwitch -= SystemEvents_SessionSwitch;
        }

        private async Task<bool> UpdateAvailabilityAsync(bool isAvailable, string reason)
        {
            SetTooltip(reason);
            return await mqttBinarySensor.UpdateAvailabilityAsync(isAvailable, cancellationTokenSource.Token);
        }

        private async Task<bool> UpdateStateAsync(bool isEnabled, string reason)
        {
            SetTooltip(reason);
            return await mqttBinarySensor.UpdateStateAsync(isEnabled, cancellationTokenSource.Token);
        }

        private async void SystemEvents_SessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            switch (e.Reason)
            {
                case SessionSwitchReason.SessionLogon: await UpdateStateAsync(true, "Logged On"); break;
                case SessionSwitchReason.SessionLogoff: await UpdateStateAsync(false, "Logged Off"); break;
                case SessionSwitchReason.SessionLock: await UpdateStateAsync(false, "Locked"); break;
                case SessionSwitchReason.SessionUnlock: await UpdateStateAsync(true, "Unlocked"); break;
            }
        }

        private async void DispatcherTimer_Tick(object? sender, EventArgs e)
        {
            if (mqttBinarySensor.IsRegistered)
                await mqttBinarySensor.ResendStateAsync(cancellationTokenSource.Token);
        }
    }
}
