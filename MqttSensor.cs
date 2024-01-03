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
