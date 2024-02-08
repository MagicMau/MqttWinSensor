using MQTTnet.Client;
using MQTTnet;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Serialization;
using static System.Windows.Forms.Design.AxImporter;
using ManagedNativeWifi;
using System.Threading;
using System.Windows.Forms;
using System.Diagnostics;
using Windows.Devices.Usb;
using System.Management;
using Microsoft.Win32;

namespace MqttWinSensor
{
    public class MqttSensor<TOptions> where TOptions : MqttSensorOptions
    {
        private readonly MqttFactory mqttFactory;
        private readonly MqttClientOptions? mqttClientOptions;
        private readonly JsonSerializerSettings serializerSettings;
        private string currentState = string.Empty;

        protected readonly TOptions options;


        public bool IsRegistered { get; set; }

        public MqttSensor(TOptions options)
        {
            serializerSettings = new()
            {
                ContractResolver = new DefaultContractResolver
                {
                    NamingStrategy = new SnakeCaseNamingStrategy()
                },
                NullValueHandling = NullValueHandling.Ignore
            };

            options.WifiNetworks ??= Array.Empty<string>();
            this.options = options;

            mqttFactory = new MqttFactory();
            mqttClientOptions = new MqttClientOptionsBuilder()
            .WithWebSocketServer(options.BrokerUri)
            .WithCredentials(options.BrokerUsername, options.BrokerPassword)
            .Build();
        }

        public Task<bool> ResendStateAsync(CancellationToken cancellationToken) => PublishAsync(CreateStateMessage(currentState), cancellationToken);

        /// <summary>
        /// Register with Home Assistant as a binary sensor
        /// </summary>
        /// <param name="sensorType">sensor or binary_sensor</param>
        /// <param name="name"></param>
        /// <param name="uniqueId"></param>
        /// <param name="deviceClass"></param>
        /// <param name="stateTopic"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task<bool> RegisterAsync(CancellationToken cancellationToken)
        {
            var applicationMessages = new MqttApplicationMessage[] {
                new MqttApplicationMessageBuilder()
                        .WithTopic($"homeassistant/{options.SensorType}/{options.Name}/config")
                        .WithPayload(JsonConvert.SerializeObject(new
                        {
                            options.Name,
                            options.UniqueId,
                            options.DeviceClass,
                            stateTopic = options.Topic + "/state",
                            offDelay = options.ExpireAfter > 1 ? options.ExpireAfter.ToString() : null,
                        }, serializerSettings))
                        .WithRetainFlag(true)
                        .Build()
            };

            IsRegistered = await PublishWithNoCheckAsync(applicationMessages, cancellationToken);
            return IsRegistered;
        }

        /// <summary>
        /// Create an ApplicationMessage about the state of the sensor.
        /// </summary>
        /// <param name="isEnabled"></param>
        /// <returns></returns>
        protected MqttApplicationMessage CreateStateMessage(string state)
        {
            currentState = state;
            return CreateApplicationMessage("state", state);
        }


        /// <summary>
        /// Create an ApplicationMessage with the given topic and payload.
        /// </summary>
        /// <param name="topic"></param>
        /// <param name="payload"></param>
        /// <returns></returns>
        protected MqttApplicationMessage CreateApplicationMessage(string topic, string payload)
        {
            return new MqttApplicationMessageBuilder()
                .WithTopic(options.Topic + "/" + topic)
                .WithPayload(payload)
                .Build();
        }

        /// <summary>
        /// Publish an ApplicationMessage to the MQTT broker.
        /// </summary>
        /// <param name="applicationMessage"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        protected Task<bool> PublishAsync(MqttApplicationMessage applicationMessage, CancellationToken cancellationToken) =>
            PublishAsync(new MqttApplicationMessage[] { applicationMessage }, cancellationToken);

        /// <summary>
        /// 
        /// </summary>
        /// <param name="mqttApplicationMessages"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        protected async Task<bool> PublishAsync(MqttApplicationMessage[] applicationMessages, CancellationToken cancellationToken)
        {
            int attempts = 0;

            while (attempts < 5)
            {
                if (await PublishAsync(applicationMessages, attempts, cancellationToken))
                    return true;

                await Task.Delay(2000, cancellationToken);
                attempts++;
            }

            return false;
        }

        protected async Task<bool> PublishAsync(MqttApplicationMessage[] applicationMessages, int attempt, CancellationToken cancellationToken)
        {
            if (options.IsCheckForPower)
            {
                if (!CheckForPower())
                    return false;
            }

            if (options.IsCheckForWifi)
            {
                if (!CheckForWifi())
                    return false;
            }

            if (!string.IsNullOrWhiteSpace(options.CheckComPort))
            {
                if (!CheckComPort(options.CheckComPort))
                    return false;
            }

            if (!IsRegistered)
            {
                if (!(await RegisterAsync(cancellationToken)))
                    return false;
            }

            return await PublishWithNoCheckAsync(applicationMessages, cancellationToken);
        }

        /// <summary>
        /// Publish an array of ApplicationMessages to the MQTT broker.
        /// This version does not check if the sensor is registered with the broker.
        /// </summary>
        /// <param name="applicationMessages"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task<bool> PublishWithNoCheckAsync(MqttApplicationMessage[] applicationMessages, CancellationToken cancellationToken)
        {
            using var mqttClient = mqttFactory.CreateMqttClient();
            try
            {
                await mqttClient.ConnectAsync(mqttClientOptions, cancellationToken);

                foreach (var applicationMessage in applicationMessages)
                {
                    System.Diagnostics.Trace.TraceInformation(applicationMessage.ConvertPayloadToString());
                    await mqttClient.PublishAsync(applicationMessage, cancellationToken);
                }

                await mqttClient.DisconnectAsync(cancellationToken: cancellationToken);
            }
            catch
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Check if we are connected to one of the provided Wifi networks
        /// </summary>
        /// <returns></returns>
        private bool CheckForWifi()
        {
            var networks = NativeWifi.EnumerateConnectedNetworkSsids();
            bool isConnectedToHome = networks.Any(n => options.WifiNetworks.Any(w => w.Equals(n.ToString(), StringComparison.OrdinalIgnoreCase)));

            if (isConnectedToHome)
                return true;

            return false;
        }

        /// <summary>
        /// Check if we are connected to mains power
        /// </summary>
        /// <returns></returns>
        private static bool CheckForPower()
        {
            return SystemInformation.PowerStatus.PowerLineStatus != PowerLineStatus.Offline;
        }

        private static bool CheckComPort(string comPort)
        {
            var serialDevices = GetSerialDevices();

            foreach (var serialDevice in serialDevices)
            {
                // grab the com port from the registry
                string regPath = "HKEY_LOCAL_MACHINE\\System\\CurrentControlSet\\Enum\\" + serialDevice.DeviceID + "\\Device Parameters";
                string portName = Registry.GetValue(regPath, "PortName", "")?.ToString() ?? string.Empty;

                if (portName.Equals(comPort, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Bit from here https://stackoverflow.com/a/75360065
        /// and a bit from here https://stackoverflow.com/a/3331509
        /// </summary>
        /// <returns></returns>
        private static List<USBDeviceInfo> GetSerialDevices()
        {
            List<USBDeviceInfo> devices = new List<USBDeviceInfo>();

            using var searcher = new ManagementObjectSearcher(
                @"Select * From Win32_PnPEntity");
            using ManagementObjectCollection collection = searcher.Get();

            foreach (var device in collection)
            {
                var guid = ((string?)device.GetPropertyValue("ClassGuid"))?.ToUpperInvariant();
                if ("{4D36E978-E325-11CE-BFC1-08002BE10318}".Equals(guid))
                {
                    devices.Add(new USBDeviceInfo(
                        (string)device.GetPropertyValue("DeviceID"),
                        (string)device.GetPropertyValue("PNPDeviceID"),
                        (string)device.GetPropertyValue("Description")
                        ));
                }
            }
            return devices;
        }


    }
}
