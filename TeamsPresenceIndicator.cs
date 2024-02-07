using MqttWinSensor.MiscUtil;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace MqttWinSensor
{
    public class TeamsPresenceIndicatorOptions : MqttSensorOptions
    {
        public int PollingInterval { get; set; } = 600;

        public TeamsPresenceIndicatorOptions()
        {
            string userName = Environment.UserName.Replace(' ', '_');

            SensorType = "sensor";
            Name = userName;
            UniqueId = "sensor.microsoft_teams." + userName;
            DeviceClass = "enum";
            Topic = $"microsoft_teams/{userName}";
        }
    }

    public partial class TeamsPresenceIndicator : MqttSensor<TeamsPresenceIndicatorOptions>
    {
        private readonly DispatcherTimer timer;
        private readonly string logsPath;
        private readonly Regex statusMatcher = StatusMatcher();
        private CancellationToken cancellationToken;

        public TeamsPresenceIndicator(TeamsPresenceIndicatorOptions options)
            : base(options)
        {
            logsPath = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Teams", "logs.txt");

            timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(600),
                IsEnabled = false
            };
            timer.Tick += Timer_Tick;
        }

        public void Start(CancellationToken cancellationToken)
        {
            this.cancellationToken = cancellationToken;
            timer.Interval = TimeSpan.FromSeconds(options.PollingInterval);

            if (Path.Exists(logsPath))
            {
                ReadTeamsLogsFile();
                timer.Start();
            }
        }

        public void Stop()
        {
            timer.Stop();
        }

        private void Timer_Tick(object? sender, EventArgs e) => ReadTeamsLogsFile();

        private void ReadTeamsLogsFile()
        {
            var lines = new ReverseLineReader(logsPath);
            var line = lines
                .SkipWhile(line => !statusMatcher.IsMatch(line))
                .Take(1)
                .First()
                ;

            var latestAvailability = (line == null ? null : statusMatcher.Match(line).Groups["Activity"]?.Value) ?? "N/A";

            PublishAsync(CreateStateMessage(latestAvailability), cancellationToken);
        }

        [GeneratedRegex("StatusIndicatorStateService: Added (?!NewActivity)(?<Activity>\\w+)", RegexOptions.Singleline)]
        private static partial Regex StatusMatcher();
    }
}
