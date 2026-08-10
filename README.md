# SimpleVoiceChat

`SimpleVoiceChat 1.0.1` is a general-purpose voice chat mod for Vintage Story. It provides proximity voice, whisper/normal/shout modes, custom channels, audio processing, permission management, and capacity protection.

## Voice

- Proximity voice supports whisper, normal speech, and shout ranges configured by the server.
- Players can choose proximity only, the selected channel only, or both targets.
- Opus is preferred, with optional ADPCM fallback. Protocol V3 is required; V2 clients are rejected.
- Client settings provide microphone selection, push-to-talk, continuous talk, gain, noise gate, noise suppression, echo cancellation, output volume, per-player volume, mute, jitter, occlusion, performance, and diagnostics.

## Channels

Every channel is a custom channel. A channel has an owner, moderators, members, listen-only members, and banned players.

- Owners create, rename, lock, disband, and assign roles.
- Moderators can invite, mute, remove, and ban lower-role members.
- Members can speak, subject to the channel talker limit.
- Listen-only members receive audio but cannot speak.
- Invites require acceptance and expire after 10 seconds.
- Channel IDs use the stable `channel-<guid>` format.

The HUD shows the selected channel and its currently speaking members. The channel page provides channel selection, send target, channel volume, leave, and invitation actions. Server administrators also receive channel creation and management controls.

## Commands

Client commands:

```text
/svc status
/svc volume <0-200>
/svc volumeplayer <player> <0-200>
/svc mute <player>
/svc unmute <player>
/svc channelinvite <player>
/svc channelleave [channel-id]
/svc channel
/svc diag
```

Server channel commands:

```text
/svc channelcreate <name>
/svc channelinvite <channel-id> <player>
/svc channelleave <channel-id>
/svc channeladd <channel-id> <player-or-uid>
/svc channelremove <channel-id> <player-or-uid>
/svc channelrole <channel-id> <player-or-uid> listenonly|member|moderator
/svc channellock <channel-id>
/svc channelunlock <channel-id>
/svc channelmute <channel-id> <player-or-uid>
/svc channelunmute <channel-id> <player-or-uid>
/svc channelban <channel-id> <player-or-uid>
/svc channelunban <channel-id> <player-or-uid>
```

## Server Configuration

The channel controls are:

```csharp
public int MaxChannelMembers { get; set; } = 100;
public int MaxChannelTalkers { get; set; } = 3;
public bool EnableChannels { get; set; } = true;
```

When a configuration older than version 5 is loaded, persistent channel names, members, permissions, locks, mutes, and bans are retained. Each channel receives a new `channel-<guid>` ID and obsolete channel type data is discarded.

## Integration

External mods can synchronize general channels through `SimpleVoiceChat.Integration.IVoiceChannelProvider`:

```csharp
public interface IVoiceChannelProvider
{
    string ProviderId { get; }
    bool TryGetChannels(out IReadOnlyList<VoiceChannelSnapshot> channels, out string error);
}
```

`VoiceChannelSnapshot` contains a channel ID, display name, owner, capacity limits, and members with roles. Externally managed channels are read-only for local membership and naming changes; server permission controls remain authoritative.

### VS Director Proximity Capture

When `VS Director 0.16.94` is installed with SimpleVoiceChat, a director client with the server `controlserver` privilege can record only the proximity voices around an active replay or live offscreen camera. Custom channels are never sent to the director audio path.

The server owner must explicitly enable the feature in `SimpleVoiceChat.Server.json`:

```json
{
  "EnableDirectorProximityCapture": true,
  "MaxDirectorListeners": 1,
  "MaxDirectorStreamsPerListener": 6
}
```

The listener position expires after 750 ms unless refreshed by the active director client. Voice still obeys the configured whisper/talk/shout distance, local mute, global mute, moderation, stream limits, and egress budgets.

## Verification

Run:

```text
dotnet test Tests\SimpleVoiceChat.Tests.csproj
dotnet build SimpleVoiceChat.csproj -c Release
```

The test suite covers V3 compatibility, generic channel data contracts, permissions, invitation lifecycle, locking, muting, bans, disbanding, configuration migration, provider synchronization, protocol validation, and language-key alignment.
