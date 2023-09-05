using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MqttWinSensor
{
    public class MqttSensorOptions
    {
        protected MqttSensorOptions() { }

        public string SensorType { get; protected set; } = string.Empty;
        public string Name { get; protected set; } = string.Empty;
        public string UniqueId { get; protected set; } = string.Empty;
        public string DeviceClass { get; protected set; } = string.Empty;
        public string Topic { get; protected set; } = string.Empty;

        public string BrokerUri { get; set; } = string.Empty;
        public string BrokerUsername { get; set; } = string.Empty;
        public string BrokerPassword { get; set; } = string.Empty;
        public int ExpireAfter { get; set; } = -1;
        public IEnumerable<string> WifiNetworks { get; set; } = Array.Empty<string>();
        public bool IsCheckForPower { get; set; } = false;
        public bool IsCheckForWifi { get; set; } = false;
    }
}
