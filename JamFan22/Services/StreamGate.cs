using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Amazon.EC2;
using Amazon.EC2.Model;

namespace JamFan22
{
    public static class StreamGate
    {
        private const string Ec2InstanceId = "i-047079b266f783bac";
        private const string Ec2Region     = "us-west-2";
        class GateState
        {
            public string ActiveIp { get; set; } = "";
            public DateTime ExpiryUtc { get; set; } = DateTime.MinValue;
        }

        private static GateState _currentState = new();
        private static readonly object _gateLock = new object();
        private const string StateFile = "data/stream-gate.json";
        private const string TargetFile = "wwwroot/stream-target.txt";

        public static void Load()
        {
            try
            {
                if (!File.Exists(StateFile)) return;
                var json = File.ReadAllText(StateFile);
                var loaded = JsonSerializer.Deserialize<GateState>(json);
                if (loaded != null) _currentState = loaded;
                Console.WriteLine($"[StreamGate] Loaded: ActiveIp={_currentState.ActiveIp} Expiry={_currentState.ExpiryUtc:u}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[StreamGate] Load failed, starting fresh: {ex.Message}");
            }
        }

        private static void SaveState()
        {
            var json = JsonSerializer.Serialize(_currentState, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(StateFile, json);
        }

        public static string TryRequestStream(string requestIp, bool isWeekly)
        {
            bool allocated = false;
            string message;

            lock (_gateLock)
            {
                if (DateTime.UtcNow < _currentState.ExpiryUtc)
                {
                    if (_currentState.ActiveIp == requestIp)
                        return $"You are already streaming. Your slot expires at {_currentState.ExpiryUtc.ToLocalTime():HH:mm} local time.";

                    return $"Sorry, the stream is currently in use by another server until {_currentState.ExpiryUtc.ToLocalTime():HH:mm} local time. Please try again later.";
                }

                _currentState.ActiveIp = requestIp;
                _currentState.ExpiryUtc = DateTime.UtcNow.AddHours(4);
                SaveState();
                File.WriteAllText(TargetFile, requestIp);
                allocated = true;
                message = $"Stream allocated! Stream is yours until {_currentState.ExpiryUtc.ToLocalTime():HH:mm} local time.";
            }

            if (allocated)
                _ = WakeEc2InstanceAsync();

            return message;
        }

        private static async Task WakeEc2InstanceAsync()
        {
            try
            {
                using var ec2 = new AmazonEC2Client(Amazon.RegionEndpoint.GetBySystemName(Ec2Region));
                var response = await ec2.StartInstancesAsync(new StartInstancesRequest
                {
                    InstanceIds = { Ec2InstanceId }
                });
                var state = response.StartingInstances.FirstOrDefault()?.CurrentState?.Name?.Value ?? "unknown";
                Console.WriteLine($"[StreamGate] EC2 {Ec2InstanceId} start requested. State={state}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[StreamGate] EC2 wake failed: {ex.Message}");
            }
        }

        public static string ResetStream(string requestIp)
        {
            lock (_gateLock)
            {
                if (_currentState.ActiveIp != requestIp)
                    return "You do not currently own the active stream slot.";

                _currentState.ExpiryUtc = DateTime.MinValue;
                _currentState.ActiveIp = "";
                SaveState();
                File.WriteAllText(TargetFile, "");
            }

            _ = StopEc2InstanceAsync();
            return "Your stream has been stopped and the slot is now free.";
        }

        private static async Task StopEc2InstanceAsync()
        {
            try
            {
                using var ec2 = new AmazonEC2Client(Amazon.RegionEndpoint.GetBySystemName(Ec2Region));
                var response = await ec2.StopInstancesAsync(new StopInstancesRequest
                {
                    InstanceIds = { Ec2InstanceId }
                });
                var state = response.StoppingInstances.FirstOrDefault()?.CurrentState?.Name?.Value ?? "unknown";
                Console.WriteLine($"[StreamGate] EC2 {Ec2InstanceId} stop requested. State={state}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[StreamGate] EC2 stop failed: {ex.Message}");
            }
        }
    }
}
