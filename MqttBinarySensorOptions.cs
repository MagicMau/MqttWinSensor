using System;

namespace MqttWinSensor
{
    public class MqttBinarySensorOptions : MqttSensorOptions
    {
        public MqttBinarySensorOptions()
        {
            SensorType = "binary_sensor";
            Name = Environment.MachineName.Replace(' ', '_');
            UniqueId = "binary_sensor.winpc." + Name;
            DeviceClass = "lock";
            Topic = $"winpc/{Name}";
        }
    }
}
