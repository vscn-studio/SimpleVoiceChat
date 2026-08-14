using System.Text.Json;
using System.Text.Json.Nodes;

namespace SimpleVoiceChat.Audio;

internal static class MultiTrackSessionManifest
{
    internal const string ObsSyncFileName = "obs-sync.json";

    internal static void Merge(string sessionDirectory)
    {
        string corePath = Path.Combine(sessionDirectory, "session.core.json");
        if (!File.Exists(corePath))
        {
            return;
        }

        JsonObject root = JsonNode.Parse(File.ReadAllText(corePath))?.AsObject() ?? new JsonObject();
        string obsPath = Path.Combine(sessionDirectory, ObsSyncFileName);
        if (File.Exists(obsPath))
        {
            try
            {
                JsonObject? obs = JsonNode.Parse(File.ReadAllText(obsPath))?.AsObject();
                if (obs != null)
                {
                    root["obs"] = obs.DeepClone();
                    long sessionStart = root["timeline"]?["utcStartUnixMilliseconds"]?.GetValue<long>() ?? 0L;
                    long obsStart = obs["obsRecordingStartUtcUnixMilliseconds"]?.GetValue<long>() ?? 0L;
                    root["obsAlignment"] = new JsonObject
                    {
                        ["wavZeroMinusObsStartMilliseconds"] = obsStart > 0 ? sessionStart - obsStart : null,
                        ["formula"] = "obsTimeMs = wavTimeMs + wavZeroMinusObsStartMilliseconds"
                    };
                }
            }
            catch (JsonException)
            {
                root["obs"] = new JsonObject { ["status"] = "invalid-sync-response" };
            }
        }
        else
        {
            root["obs"] = new JsonObject { ["status"] = "awaiting-plugin" };
        }

        File.WriteAllText(Path.Combine(sessionDirectory, "session.json"), root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
