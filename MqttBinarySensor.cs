using MQTTnet.Client;
using MQTTnet;
using Newtonsoft.Json;
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
using static System.Windows.Forms.Design.AxImporter;

namespace MqttWinSensor
{
    public class MqttBinarySensor : MqttSensor<MqttBinarySensorOptions>
    {
        /// <summary>
        /// Create a new MqttBinarySensor
        /// </summary>
        public MqttBinarySensor(MqttBinarySensorOptions options)
            : base(options)
        {
        }


        /// <summary>
        /// Update the sensor's state
        /// </summary>
        /// <param name="isEnabled"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public Task<bool> UpdateStateAsync(bool isEnabled, CancellationToken cancellationToken) =>
            PublishAsync(CreateStateMessage(isEnabled), cancellationToken);

        /// <summary>
        /// Create an ApplicationMessage about the state of the sensor.
        /// </summary>
        /// <param name="isEnabled"></param>
        /// <returns></returns>
        private MqttApplicationMessage CreateStateMessage(bool isEnabled) => CreateStateMessage(isEnabled ? "ON" : "OFF");
    }
}
