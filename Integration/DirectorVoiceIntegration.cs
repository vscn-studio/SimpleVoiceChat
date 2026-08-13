using System.Reflection;
using System.Reflection.Emit;
using SimpleVoiceChat.Audio;
using SimpleVoiceChat.Config;
using SimpleVoiceChat.Networking;
using Vintagestory.API.Client;

namespace SimpleVoiceChat.Integration;

/// <summary>
/// Optional VS Director bridge. The main assembly deliberately has no compile-time
/// reference to VSDirector.dll; the bridge activates only when the mod is loaded.
/// </summary>
internal sealed class DirectorVoiceIntegration : IDisposable
{
    private const long ListenerUpdateIntervalMilliseconds = 100;
    private const long ReflectionRetryIntervalMilliseconds = 2_000;
    private const long StreamIdleMilliseconds = 2_000;
    private const int MaximumDecodedFramesPerTick = 16;

    private readonly ICoreClientAPI capi;
    private readonly bool directorModEnabled;
    private readonly Dictionary<string, DirectorVoiceStream> streams = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DirectorVoiceSource> sources = new(StringComparer.Ordinal);
    private DirectorReflection? reflection;
    private long lastReflectionAttemptMilliseconds = long.MinValue;
    private long lastListenerUpdateMilliseconds;
    private bool listenerWasActive;
    private bool disposed;

    internal DirectorVoiceIntegration(ICoreClientAPI capi)
    {
        this.capi = capi;
        directorModEnabled = capi.ModLoader.IsModEnabled("vsdirector");
        TryGetReflection(out _);
    }

    internal void UpdateListener(IClientNetworkChannel? controlChannel)
    {
        if (disposed || !TryGetReflection(out DirectorReflection director) || controlChannel?.Connected != true)
        {
            return;
        }

        long now = capi.World.ElapsedMilliseconds;
        if (now - lastListenerUpdateMilliseconds < ListenerUpdateIntervalMilliseconds)
        {
            return;
        }

        DirectorVoicePositionData position = default;
        bool active = director.TryGetActiveVoiceListener(out position);
        if (!active && !listenerWasActive)
        {
            return;
        }

        controlChannel.SendPacket(new DirectorVoiceListenerUpdatePacket
        {
            Active = active,
            X = active ? position.X : 0d,
            Y = active ? position.Y : 0d,
            Z = active ? position.Z : 0d,
            Dimension = active ? position.Dimension : 0
        });
        lastListenerUpdateMilliseconds = now;
        listenerWasActive = active;
    }

    internal void Enqueue(DirectorVoiceRelayFrameV3Packet packet)
    {
        if (disposed || !TryGetReflection(out _) || !VoiceProtocolValidation.IsValidDirectorRelayShape(packet))
        {
            return;
        }

        string key = packet.SpeakerUid;
        if (!streams.TryGetValue(key, out DirectorVoiceStream? stream))
        {
            stream = new DirectorVoiceStream();
            streams[key] = stream;
        }
        stream.Enqueue(packet, capi.World.ElapsedMilliseconds);
    }

    internal void Update(ServerVoiceConfigPacket serverConfig)
    {
        if (disposed || !TryGetReflection(out DirectorReflection director))
        {
            return;
        }

        if (!serverConfig.EnableDirectorProximityCapture)
        {
            ClearRemoteStreams();
            return;
        }

        if (!director.IsCaptureEnabled)
        {
            return;
        }

        long now = capi.World.ElapsedMilliseconds;
        int remaining = MaximumDecodedFramesPerTick;
        foreach (string speakerUid in streams.Keys.ToArray())
        {
            DirectorVoiceStream stream = streams[speakerUid];
            while (remaining > 0 && stream.TryDecode(out short[] samples, out DirectorVoiceFrameMetadata metadata))
            {
                remaining--;
                DirectorVoiceSource source = GetSource(speakerUid, metadata.SpeakerName);
                object spatialization = director.CreateSpatialization(
                    new DirectorVoicePositionData(metadata.X, metadata.Y, metadata.Z, metadata.Dimension),
                    metadata.MaxDistance,
                    metadata.ReferenceDistance,
                    metadata.RolloffFactor);
                source.Submit(samples, VoiceConstants.SampleRate, spatialization, metadata.TimestampMilliseconds);
            }

            if (now - stream.LastActivityMilliseconds <= StreamIdleMilliseconds)
            {
                continue;
            }

            stream.Dispose();
            streams.Remove(speakerUid);
            if (sources.Remove(speakerUid, out DirectorVoiceSource? speakerSource))
            {
                speakerSource.Dispose();
            }
        }
    }

    internal void SubmitLocalFrame(
        ReadOnlySpan<short> samples,
        long timestampMilliseconds,
        VoiceMode mode,
        VoiceTransmitTarget transmitTarget,
        ServerVoiceConfigPacket serverConfig)
    {
        if (disposed || samples.IsEmpty || !TryGetReflection(out DirectorReflection director))
        {
            return;
        }

        var entity = capi.World.Player.Entity;
        int dimension = entity.Pos.Dimension;
        if (!serverConfig.EnableDirectorProximityCapture
            || transmitTarget is not (VoiceTransmitTarget.Proximity or VoiceTransmitTarget.ProximityAndChannel)
            || !director.TryGetActiveVoiceListener(out DirectorVoicePositionData listenerPosition)
            || dimension != listenerPosition.Dimension)
        {
            return;
        }

        double dx = entity.Pos.X - listenerPosition.X;
        double dy = entity.Pos.Y - listenerPosition.Y;
        double dz = entity.Pos.Z - listenerPosition.Z;
        float range = Math.Min(serverConfig.GetRange(mode), serverConfig.MaxRange);
        if (dx * dx + dy * dy + dz * dz > range * range)
        {
            return;
        }

        DirectorVoiceSource source = GetSource(capi.World.Player.PlayerUID, capi.World.Player.PlayerName);
        object spatialization = director.CreateSpatialization(
            new DirectorVoicePositionData(entity.Pos.X, entity.Pos.Y, entity.Pos.Z, dimension),
            range,
            CalculateReferenceDistance(range),
            CalculateRolloff(range));
        source.Submit(samples.ToArray(), VoiceConstants.SampleRate, spatialization, timestampMilliseconds);
    }

    internal bool CanCaptureLocalFrame(
        VoiceTransmitTarget transmitTarget,
        ServerVoiceConfigPacket serverConfig)
        => !disposed
            && TryGetReflection(out DirectorReflection director)
            && serverConfig.EnableDirectorProximityCapture
            && transmitTarget is (VoiceTransmitTarget.Proximity or VoiceTransmitTarget.ProximityAndChannel)
            && director.IsCaptureEnabled
            && director.TryGetActiveVoiceListener(out _);

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        ClearRemoteStreams();
        foreach (DirectorVoiceSource source in sources.Values)
        {
            source.Dispose();
        }
        sources.Clear();
    }

    private DirectorVoiceSource GetSource(string speakerUid, string? speakerName)
    {
        if (sources.TryGetValue(speakerUid, out DirectorVoiceSource? source))
        {
            return source;
        }

        source = new DirectorVoiceSource(
            reflection!,
            speakerUid,
            speakerName);
        sources[speakerUid] = source;
        return source;
    }

    private bool TryGetReflection(out DirectorReflection director)
    {
        if (reflection is not null)
        {
            director = reflection;
            return true;
        }
        if (!directorModEnabled)
        {
            director = null!;
            return false;
        }

        long now = capi.World.ElapsedMilliseconds;
        if (lastReflectionAttemptMilliseconds != long.MinValue
            && now - lastReflectionAttemptMilliseconds < ReflectionRetryIntervalMilliseconds)
        {
            director = null!;
            return false;
        }

        lastReflectionAttemptMilliseconds = now;
        reflection = DirectorReflection.TryCreate(capi);
        director = reflection!;
        return reflection is not null;
    }

    private void ClearRemoteStreams()
    {
        foreach (KeyValuePair<string, DirectorVoiceStream> entry in streams)
        {
            entry.Value.Dispose();
            if (sources.Remove(entry.Key, out DirectorVoiceSource? source))
            {
                source.Dispose();
            }
        }
        streams.Clear();
    }

    private static float CalculateRolloff(float range)
        => range > 1f ? (float)-Math.Log(0.01d) / (float)Math.Log(range) : 1f;

    private static float CalculateReferenceDistance(float range)
        => (float)Math.Max(3d, Math.Sqrt(Math.Max(range, 1f)) - 2d);

    private readonly record struct DirectorVoiceFrameMetadata(
        double X,
        double Y,
        double Z,
        int Dimension,
        float MaxDistance,
        float ReferenceDistance,
        float RolloffFactor,
        string SpeakerName,
        long ArrivalMilliseconds,
        long TimestampMilliseconds);

    private readonly record struct DirectorVoicePositionData(double X, double Y, double Z, int Dimension);

    private sealed class DirectorVoiceSource : IDisposable
    {
        private readonly DirectorReflection reflection;
        private readonly object source;
        private bool disposed;

        internal DirectorVoiceSource(DirectorReflection reflection, string speakerUid, string? speakerName)
        {
            this.reflection = reflection;
            source = reflection.RegisterSpeaker(speakerUid, speakerName);
        }

        internal void Submit(short[] samples, int sampleRate, object spatialization, long timestampMilliseconds)
        {
            if (!disposed)
            {
                reflection.SubmitPcm16(source, samples, sampleRate, spatialization, timestampMilliseconds);
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            reflection.DisposeSource(source);
        }
    }

    private sealed class DirectorReflection
    {
        private readonly object director;
        private readonly object voiceApi;
        private readonly PropertyInfo captureEnabledProperty;
        private readonly MethodInfo tryGetListenerMethod;
        private readonly MethodInfo registerSpeakerMethod;
        private readonly MethodInfo disposeSourceMethod;
        private readonly Action<object, short[], int, object, long> submitPcm16;
        private readonly PropertyInfo positionX;
        private readonly PropertyInfo positionY;
        private readonly PropertyInfo positionZ;
        private readonly PropertyInfo positionDimension;
        private readonly Type spatializationType;

        private DirectorReflection(
            object director,
            object voiceApi,
            PropertyInfo captureEnabledProperty,
            MethodInfo tryGetListenerMethod,
            MethodInfo registerSpeakerMethod,
            MethodInfo disposeSourceMethod,
            Action<object, short[], int, object, long> submitPcm16,
            PropertyInfo positionX,
            PropertyInfo positionY,
            PropertyInfo positionZ,
            PropertyInfo positionDimension,
            Type spatializationType)
        {
            this.director = director;
            this.voiceApi = voiceApi;
            this.captureEnabledProperty = captureEnabledProperty;
            this.tryGetListenerMethod = tryGetListenerMethod;
            this.registerSpeakerMethod = registerSpeakerMethod;
            this.disposeSourceMethod = disposeSourceMethod;
            this.submitPcm16 = submitPcm16;
            this.positionX = positionX;
            this.positionY = positionY;
            this.positionZ = positionZ;
            this.positionDimension = positionDimension;
            this.spatializationType = spatializationType;
        }

        internal bool IsCaptureEnabled
            => captureEnabledProperty.GetValue(voiceApi) as bool? == true;

        internal object RegisterSpeaker(string speakerUid, string? speakerName)
            => registerSpeakerMethod.Invoke(voiceApi, new object?[] { "simplevoicechat", speakerUid, speakerName })!;

        internal object CreateSpatialization(
            DirectorVoicePositionData position,
            float maxDistance,
            float referenceDistance,
            float rolloffFactor)
        {
            Type positionType = spatializationType.GetProperty("Position")!.PropertyType;
            object positionValue = Activator.CreateInstance(
                positionType,
                new object[] { position.X, position.Y, position.Z, position.Dimension })!;
            return Activator.CreateInstance(
                spatializationType,
                new object[] { positionValue, maxDistance, referenceDistance, rolloffFactor })!;
        }

        internal bool TryGetActiveVoiceListener(out DirectorVoicePositionData position)
        {
            object?[] args = { null };
            bool active = (bool)(tryGetListenerMethod.Invoke(director, args) ?? false);
            if (!active || args[0] is null)
            {
                position = default;
                return false;
            }

            object value = args[0]!;
            position = new DirectorVoicePositionData(
                Convert.ToDouble(positionX.GetValue(value)),
                Convert.ToDouble(positionY.GetValue(value)),
                Convert.ToDouble(positionZ.GetValue(value)),
                Convert.ToInt32(positionDimension.GetValue(value)));
            return true;
        }

        internal void SubmitPcm16(
            object source,
            short[] samples,
            int sampleRate,
            object spatialization,
            long timestampMilliseconds)
            => submitPcm16(source, samples, sampleRate, spatialization, timestampMilliseconds);

        internal void DisposeSource(object source)
            => disposeSourceMethod.Invoke(source, null);

        internal static DirectorReflection? TryCreate(ICoreClientAPI capi)
        {
            try
            {
                if (!capi.ModLoader.IsModEnabled("vsdirector"))
                {
                    return null;
                }

                Type? systemType = FindLoadedType("VSDirector.VSDirectorModSystem");
                Type? positionType = FindLoadedType("VSDirector.DirectorVoicePosition");
                Type? spatializationType = FindLoadedType("VSDirector.DirectorVoiceSpatialization");
                if (systemType is null || positionType is null || spatializationType is null)
                {
                    return null;
                }

                object director = ResolveModSystem(capi.ModLoader, systemType)
                    ?? throw new InvalidOperationException("VS Director client system is unavailable.");
                PropertyInfo voiceApiProperty = systemType.GetProperty("VoiceApi")
                    ?? throw new MissingMemberException(systemType.FullName, "VoiceApi");
                object voiceApi = voiceApiProperty.GetValue(director)
                    ?? throw new InvalidOperationException("VS Director voice API is unavailable.");
                Type voiceApiType = voiceApi.GetType();
                PropertyInfo versionProperty = voiceApiType.GetProperty("Version")
                    ?? throw new MissingMemberException(voiceApiType.FullName, "Version");
                if (Convert.ToInt32(versionProperty.GetValue(voiceApi)) != 2)
                {
                    throw new InvalidOperationException("VS Director voice API version 2 is required.");
                }
                PropertyInfo captureEnabledProperty = voiceApiType.GetProperty("IsCaptureEnabled")
                    ?? throw new MissingMemberException(voiceApiType.FullName, "IsCaptureEnabled");
                MethodInfo tryGetListenerMethod = systemType.GetMethod("TryGetActiveVoiceListener")
                    ?? throw new MissingMethodException(systemType.FullName, "TryGetActiveVoiceListener");
                MethodInfo registerSpeakerMethod = voiceApiType.GetMethods()
                    .FirstOrDefault(method => method.Name == "RegisterSpeaker" && method.GetParameters().Length == 3)
                    ?? throw new MissingMethodException(voiceApiType.FullName, "RegisterSpeaker");
                Type sourceType = registerSpeakerMethod.ReturnType;
                MethodInfo disposeSourceMethod = sourceType.GetMethod("Dispose", Type.EmptyTypes)
                    ?? throw new MissingMethodException(sourceType.FullName, "Dispose");
                MethodInfo submitMethod = sourceType.GetMethods()
                    .FirstOrDefault(method => method.Name == "SubmitPcm16"
                        && method.GetParameters().Length == 5
                        && method.GetParameters()[0].ParameterType == typeof(ReadOnlySpan<short>))
                    ?? throw new MissingMethodException(sourceType.FullName, "SubmitPcm16");
                Action<object, short[], int, object, long> submitPcm16 = CreateSubmitDelegate(sourceType, spatializationType, submitMethod);
                PropertyInfo positionX = positionType.GetProperty("X")
                    ?? throw new MissingMemberException(positionType.FullName, "X");
                PropertyInfo positionY = positionType.GetProperty("Y")
                    ?? throw new MissingMemberException(positionType.FullName, "Y");
                PropertyInfo positionZ = positionType.GetProperty("Z")
                    ?? throw new MissingMemberException(positionType.FullName, "Z");
                PropertyInfo positionDimension = positionType.GetProperty("Dimension")
                    ?? throw new MissingMemberException(positionType.FullName, "Dimension");
                return new DirectorReflection(
                    director,
                    voiceApi,
                    captureEnabledProperty,
                    tryGetListenerMethod,
                    registerSpeakerMethod,
                    disposeSourceMethod,
                    submitPcm16,
                    positionX,
                    positionY,
                    positionZ,
                    positionDimension,
                    spatializationType);
            }
            catch (Exception exception) when (exception is FileNotFoundException
                or FileLoadException
                or BadImageFormatException
                or TypeLoadException
                or MissingMemberException
                or MissingMethodException
                or InvalidOperationException
                or TargetInvocationException)
            {
                capi.Logger.Debug("SimpleVoiceChat: optional VS Director integration is unavailable: {0}", exception.Message);
                return null;
            }
        }

        private static object? ResolveModSystem(object modLoader, Type systemType)
        {
            Type loaderType = modLoader.GetType();
            MethodInfo? getModSystem = EnumerateLoaderContracts(loaderType)
                .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                .FirstOrDefault(IsCompatibleGetModSystemMethod);
            if (getModSystem is null)
            {
                throw new MissingMethodException("Vintage Story GetModSystem<T>(bool) is unavailable.");
            }

            object?[]? arguments = getModSystem.GetParameters().Length == 0
                ? null
                : new object?[] { true };
            return getModSystem.MakeGenericMethod(systemType).Invoke(modLoader, arguments);
        }

        private static IEnumerable<Type> EnumerateLoaderContracts(Type loaderType)
        {
            yield return loaderType;
            foreach (Type interfaceType in loaderType.GetInterfaces())
            {
                yield return interfaceType;
            }
        }

        private static bool IsCompatibleGetModSystemMethod(MethodInfo method)
        {
            if (method.Name != "GetModSystem"
                || !method.IsGenericMethodDefinition
                || method.GetGenericArguments().Length != 1)
            {
                return false;
            }

            ParameterInfo[] parameters = method.GetParameters();
            return parameters.Length == 0
                || (parameters.Length == 1 && parameters[0].ParameterType == typeof(bool));
        }

        private static Action<object, short[], int, object, long> CreateSubmitDelegate(
            Type sourceType,
            Type spatializationType,
            MethodInfo submitMethod)
        {
            ConstructorInfo spanConstructor = typeof(ReadOnlySpan<short>).GetConstructor(new[] { typeof(short[]) })
                ?? throw new MissingMethodException(typeof(ReadOnlySpan<short>).FullName, ".ctor(short[])");
            DynamicMethod dynamicMethod = new(
                "SimpleVoiceChatSubmitDirectorPcm16",
                typeof(void),
                new[] { typeof(object), typeof(short[]), typeof(int), typeof(object), typeof(long) },
                typeof(DirectorVoiceIntegration).Module,
                skipVisibility: true);
            ILGenerator il = dynamicMethod.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, sourceType);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Newobj, spanConstructor);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Ldarg_3);
            il.Emit(spatializationType.IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass, spatializationType);
            il.Emit(OpCodes.Ldarg, 4);
            il.Emit(OpCodes.Ldc_R4, 1f);
            il.Emit(OpCodes.Callvirt, submitMethod);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ret);
            return (Action<object, short[], int, object, long>)dynamicMethod.CreateDelegate(
                typeof(Action<object, short[], int, object, long>));
        }

        private static Type? FindLoadedType(string fullName)
            => AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(fullName, throwOnError: false, ignoreCase: false))
                .FirstOrDefault(type => type is not null);
    }

    private sealed class DirectorVoiceStream : IDisposable
    {
        private readonly EncodedJitterBuffer encodedFrames = new();
        private readonly Dictionary<ushort, DirectorVoiceFrameMetadata> metadataBySequence = new();
        private IVoiceDecoder? decoder;
        private int codec;
        private int sessionId = -1;
        private DirectorVoiceFrameMetadata latestMetadata;
        private bool hasMetadata;
        private readonly VoiceFrameSequenceTimeline timestampTimeline = new();

        internal long LastActivityMilliseconds { get; private set; }

        internal void Enqueue(DirectorVoiceRelayFrameV3Packet packet, long arrivalMilliseconds)
        {
            if (sessionId != packet.SessionId || codec != packet.Codec)
            {
                sessionId = packet.SessionId;
                codec = packet.Codec;
                encodedFrames.Reset();
                metadataBySequence.Clear();
                decoder?.Dispose();
                decoder = VoiceCodecFactory.CreateDecoder(packet.Codec);
                hasMetadata = false;
                timestampTimeline.Reset();
            }

            DirectorVoiceFrameMetadata metadata = new(
                packet.X,
                packet.Y,
                packet.Z,
                packet.Dimension,
                packet.MaxDistance,
                packet.ReferenceDistance,
                packet.RolloffFactor,
                packet.SpeakerName,
                arrivalMilliseconds,
                arrivalMilliseconds);
            metadataBySequence[packet.Sequence] = metadata;
            latestMetadata = metadata;
            hasMetadata = true;
            while (metadataBySequence.Count > 24)
            {
                metadataBySequence.Remove(metadataBySequence.Keys.First());
            }

            encodedFrames.Enqueue(packet.Sequence, packet.Payload.ToArray(), arrivalMilliseconds);
            LastActivityMilliseconds = arrivalMilliseconds;
        }

        internal bool TryDecode(out short[] samples, out DirectorVoiceFrameMetadata metadata)
        {
            samples = Array.Empty<short>();
            metadata = default;
            if (decoder == null
                || !hasMetadata
                || !encodedFrames.TryDequeue(out EncodedJitterFrame encoded))
            {
                return false;
            }

            if (!metadataBySequence.Remove(encoded.Sequence, out metadata))
            {
                metadata = latestMetadata;
            }
            metadata = metadata with
            {
                TimestampMilliseconds = timestampTimeline.Resolve(encoded.Sequence, metadata.ArrivalMilliseconds)
            };

            samples = new short[VoiceConstants.SamplesPerFrame];
            VoiceDecoderSafety.DecodeOrSilence(decoder, encoded.Payload, samples, encoded.UseFec);
            return true;
        }

        public void Dispose()
        {
            decoder?.Dispose();
            decoder = null;
            metadataBySequence.Clear();
        }
    }
}
