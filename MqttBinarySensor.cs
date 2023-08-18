using MQTTnet.Client;
using MQTTnet;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Serialization;
using System.Windows.Forms;
using ManagedNativeWifi;
using System.Xml;
using System.Diagnostics;

namespace MqttWinSensor
{
    public class MqttBinarySensor
    {
        private readonly MqttFactory mqttFactory;
        private readonly MqttClientOptions? mqttClientOptions;
        private readonly JsonSerializerSettings serializerSettings;
        private readonly MqttBinarySensorOptions options;
        private bool currentState = false;

        public bool IsRegistered { get; set; }

        /// <summary>
        /// Create a new MqttBinarySensor
        /// </summary>
        public MqttBinarySensor(MqttBinarySensorOptions options)
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

        /// <summary>
        /// Update the sensor's state
        /// </summary>
        /// <param name="isEnabled"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public Task<bool> UpdateStateAsync(bool isEnabled, CancellationToken cancellationToken) =>
            PublishAsync(CreateStateMessage(isEnabled), cancellationToken);

        public Task<bool> ResendStateAsync(CancellationToken cancellationToken) => UpdateStateAsync(currentState, cancellationToken);

        /// <summary>
        /// Register with Home Assistant as a binary sensor
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task<bool> RegisterAsync(CancellationToken cancellationToken)
        {
            var applicationMessages = new MqttApplicationMessage[] {
                new MqttApplicationMessageBuilder()
                        .WithTopic($"homeassistant/binary_sensor/{Environment.MachineName}/config")
                        .WithPayload(JsonConvert.SerializeObject(new
                        {
                            name = Environment.MachineName,
                            uniqueId = "binary_sensor.winpc." + Environment.MachineName,
                            deviceClass = "lock",
                            stateTopic = $"winpc/{Environment.MachineName}/state",
                            offDelay = options.ExpireAfter > 1 ? options.ExpireAfter.ToString() : null,
                        }, serializerSettings))
                        .WithRetainFlag()
                        .Build()
            };

            IsRegistered = await PublishWithNoCheckAsync(applicationMessages, cancellationToken);
            return IsRegistered;
        }

        /// <summary>
        /// Crate an ApplicationMessage about the state of the sensor.
        /// </summary>
        /// <param name="isEnabled"></param>
        /// <returns></returns>
        private MqttApplicationMessage CreateStateMessage(bool isEnabled)
        {
            currentState = isEnabled;
            return CreateApplicationMessage("state", isEnabled ? "ON" : "OFF");
        }

        /// <summary>
        /// Create an ApplicationMessage with the given topic and payload.
        /// </summary>
        /// <param name="topic"></param>
        /// <param name="payload"></param>
        /// <returns></returns>
        private static MqttApplicationMessage CreateApplicationMessage(string topic, string payload)
        {
            return new MqttApplicationMessageBuilder()
                .WithTopic($"winpc/{Environment.MachineName}/{topic}")
                .WithPayload(payload)
                .Build();
        }

        /// <summary>
        /// Publish an ApplicationMessage to the MQTT broker.
        /// </summary>
        /// <param name="applicationMessage"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private Task<bool> PublishAsync(MqttApplicationMessage applicationMessage, CancellationToken cancellationToken) =>
            PublishAsync(new MqttApplicationMessage[] { applicationMessage }, cancellationToken);

        /// <summary>
        /// 
        /// </summary>
        /// <param name="mqttApplicationMessages"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task<bool> PublishAsync(MqttApplicationMessage[] applicationMessages, CancellationToken cancellationToken)
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
    }
}
