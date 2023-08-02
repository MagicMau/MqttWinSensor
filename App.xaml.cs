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
        private MqttFactory mqttFactory = new();
        private MqttClientOptions? clientOptions = null;
        private JsonSerializerSettings serializerSettings = new();
        private bool isRegistered = false;
        private string? lastReason = null;
        private bool isExiting = false;

        // config values
        private readonly bool isCheckForPower = "true".Equals(ConfigurationManager.AppSettings["check_if_on_power"], StringComparison.OrdinalIgnoreCase);
        private readonly bool isCheckForWifi = "true".Equals(ConfigurationManager.AppSettings["check_if_on_wireless_network"], StringComparison.OrdinalIgnoreCase);
        private readonly string brokerUri = ConfigurationManager.AppSettings["mqtt_broker_uri"] ?? string.Empty;
        private readonly string brokerUser = ConfigurationManager.AppSettings["mqtt_broker_user"] ?? string.Empty;
        private readonly string brokerPassword = ConfigurationManager.AppSettings["mqtt_broker_pwd"] ?? string.Empty;
        private readonly string wifiNetworksText = ConfigurationManager.AppSettings["wireless_networks"] ?? string.Empty;
        private readonly string wifiTextDelimiter = ConfigurationManager.AppSettings["wireless_networks_delimiter"] ?? string.Empty;
        
        private string[] wifiNetworks = Array.Empty<string>();

        protected async override void OnStartup(StartupEventArgs e)
        {
            serializerSettings.ContractResolver = new DefaultContractResolver
            {
                NamingStrategy = new SnakeCaseNamingStrategy()
            };

            wifiNetworks = wifiNetworksText.Split(wifiTextDelimiter, StringSplitOptions.RemoveEmptyEntries);


            // register with HomeAssistant
            if (await IsConnectedToHome())
                await RegisterWithHomeAssistant();

            SystemEvents.SessionSwitch += SystemEvents_SessionSwitch;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            base.OnExit(e);
            isExiting = true;

            Task.Run(async () =>
            {
                await PublishMqttMessage("state", "OFF", "Exited");
                await PublishMqttMessage("available", "offline", "Exited");
            }).Wait();

            SystemEvents.SessionSwitch -= SystemEvents_SessionSwitch;
        }

        private async void SystemEvents_SessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            switch (e.Reason)
            {
                case SessionSwitchReason.SessionLogon: await PublishMqttMessage("state", "ON", "Logged On"); break;
                case SessionSwitchReason.SessionLogoff: await PublishMqttMessage("state", "OFF", "Logged Off"); break;
                case SessionSwitchReason.SessionLock: await PublishMqttMessage("state", "OFF", "Locked"); break;
                case SessionSwitchReason.SessionUnlock: await PublishMqttMessage("state", "ON", "Unlocked"); break;
            }
        }

        private MqttClientOptions CreateMqttClientOptions()
        {
            if (clientOptions == null)
                clientOptions = new MqttClientOptionsBuilder()
                        .WithWebSocketServer(brokerUri)
                        .WithCredentials(brokerUser, brokerPassword)
                        .Build();

            return clientOptions;
        }

        public async Task PublishMqttMessage(string topic, string message, string reason)
        {
            if (!(await IsConnectedToHome()))
                return;

            if (!isRegistered)
            {
                if (!(await RegisterWithHomeAssistant()))
                    return;
            }

            try
            {
                using (var mqttClient = mqttFactory.CreateMqttClient())
                {
                    var mqttClientOptions = CreateMqttClientOptions();

                    // This will throw an exception if the server is not available.
                    // The result from this message returns additional data which was sent 
                    // from the server. Please refer to the MQTT protocol specification for details.
                    await mqttClient.ConnectAsync(mqttClientOptions, CancellationToken.None);

                    var applicationMessage = new MqttApplicationMessageBuilder()
                        .WithTopic($"winpc/{Environment.MachineName}/{topic}")
                        .WithPayload(message)
                        .Build();

                    await mqttClient.PublishAsync(applicationMessage, CancellationToken.None);

                    await mqttClient.DisconnectAsync();

                    SetTooltip(reason);
                }
            }
            catch (Exception e) {
                SetTooltip("ERROR: " + e.Message);
            }
        }

        internal void SetTooltip(string? text = null)
        {
            if (!isExiting && this.MainWindow is MainWindow wnd)
            {
                if (text == null) {
                    text = lastReason;
                }
                try
                {
                    wnd.TaskbarIcon.ToolTipText = "MQTT Sensor: " + Environment.MachineName + (text == null ? "" : " | " + text);
                }
                catch { }
            }
            lastReason = text;
        }

        private async Task<bool> IsConnectedToHome()
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                if (attempt > 0)
                {
                    await Task.Delay(1000); // wait 1 second
                }

                if (!IsOnPower())
                {
                    SetTooltip("not connected to power");
                    continue;
                }

                if (!isCheckForWifi)
                    return true;

                var networks = NativeWifi.EnumerateConnectedNetworkSsids();
                bool isConnectedToHome = networks.Any(n => wifiNetworks.Any(w => w.Equals(n.ToString(), StringComparison.OrdinalIgnoreCase)));

                SetTooltip((isConnectedToHome ? "" : "not ") + "connected to home network");
                
                if (isConnectedToHome)
                    return true;
            }

            return false;
        }

        private bool IsOnPower()
        {
            if (!isCheckForPower) return true;

            return System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus != System.Windows.Forms.PowerLineStatus.Offline;
        }

        protected async Task<bool> RegisterWithHomeAssistant()
        {
            if (!(await IsConnectedToHome()))
                return false;

            SetTooltip("registering");
            try
            {
                using (var mqttClient = mqttFactory.CreateMqttClient())
                {
                    var mqttClientOptions = CreateMqttClientOptions();

                    // This will throw an exception if the server is not available.
                    // The result from this message returns additional data which was sent 
                    // from the server. Please refer to the MQTT protocol specification for details.
                    await mqttClient.ConnectAsync(mqttClientOptions, CancellationToken.None);

                    var applicationMessage = new MqttApplicationMessageBuilder()
                        .WithTopic($"homeassistant/binary_sensor/{Environment.MachineName}/config")
                        .WithPayload(JsonConvert.SerializeObject(new
                        {
                            name = Environment.MachineName,
                            deviceClass = "lock",
                            stateTopic = $"winpc/{Environment.MachineName}/state",
                            availabilityTopic = $"winpc/{Environment.MachineName}/available",
                        }, serializerSettings))
                        .WithRetainFlag()
                        .Build();

                    await mqttClient.PublishAsync(applicationMessage, CancellationToken.None);

                    await mqttClient.DisconnectAsync();
                    isRegistered = true;

                    await PublishMqttMessage("available", "online", "Started");
                    await PublishMqttMessage("state", "ON", "Started");
                    return true;
                }
            }
            catch (Exception e) {
                SetTooltip("ERROR initializing: " + e.Message);
            }

            return false;
        }
    }
}
