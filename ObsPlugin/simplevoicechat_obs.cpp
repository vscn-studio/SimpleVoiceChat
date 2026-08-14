#include <obs-module.h>
#include <obs-frontend-api.h>

#include <Windows.h>

#include <algorithm>
#include <atomic>
#include <cstdint>
#include <cstring>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

OBS_DECLARE_MODULE()

namespace {

constexpr char SourceId[] = "simplevoicechat_audio_bus";
constexpr char PipePath[] = "\\\\.\\pipe\\simplevoicechat-audiobuses";
constexpr uint8_t ProtocolVersion = 1;
constexpr uint8_t SessionMarkerBus = 0x7f;
constexpr uint8_t SessionAcknowledgementMessage = 1;
constexpr size_t FrameHeaderSize = 22;
constexpr size_t MaximumSamples = 4096;

struct BusSource {
    obs_source_t* source = nullptr;
    std::atomic<int> bus = 0;
};

std::atomic<bool> running = false;
std::thread pipeThread;
std::mutex sourcesMutex;
std::mutex pipeWriteMutex;
std::vector<BusSource*> sources;
std::atomic<HANDLE> activePipe = INVALID_HANDLE_VALUE;
std::atomic<int64_t> recordingStartedUtcMilliseconds = 0;
std::mutex sessionMutex;
std::string mostRecentSessionId;

uint16_t read_u16(const uint8_t* bytes)
{
    return static_cast<uint16_t>(bytes[0]) | (static_cast<uint16_t>(bytes[1]) << 8);
}

uint32_t read_u32(const uint8_t* bytes)
{
    return static_cast<uint32_t>(bytes[0])
        | (static_cast<uint32_t>(bytes[1]) << 8)
        | (static_cast<uint32_t>(bytes[2]) << 16)
        | (static_cast<uint32_t>(bytes[3]) << 24);
}

bool read_exact(HANDLE pipe, void* destination, size_t count)
{
    auto* bytes = static_cast<uint8_t*>(destination);
    while (count > 0 && running.load()) {
        DWORD available = 0;
        if (!PeekNamedPipe(pipe, nullptr, 0, nullptr, &available, nullptr))
            return false;
        if (available == 0) {
            Sleep(10);
            continue;
        }
        DWORD read = 0;
        const DWORD wanted = static_cast<DWORD>(std::min<size_t>({ count, available, MAXDWORD }));
        if (!ReadFile(pipe, bytes, wanted, &read, nullptr) || read == 0)
            return false;
        bytes += read;
        count -= read;
    }
    return count == 0;
}

int64_t utc_now_milliseconds()
{
    FILETIME fileTime{};
    GetSystemTimeAsFileTime(&fileTime);
    ULARGE_INTEGER value{};
    value.LowPart = fileTime.dwLowDateTime;
    value.HighPart = fileTime.dwHighDateTime;
    return static_cast<int64_t>((value.QuadPart - 116444736000000000ULL) / 10000ULL);
}

bool write_acknowledgement(const std::string& sessionId)
{
    std::lock_guard<std::mutex> lock(pipeWriteMutex);
    HANDLE pipe = activePipe.load();
    if (pipe == INVALID_HANDLE_VALUE || sessionId.empty() || sessionId.size() > 512)
        return false;

    std::vector<uint8_t> message(24 + sessionId.size());
    message[0] = 'S';
    message[1] = 'V';
    message[2] = 'C';
    message[3] = 'A';
    message[4] = ProtocolVersion;
    message[5] = SessionAcknowledgementMessage;
    const uint16_t idLength = static_cast<uint16_t>(sessionId.size());
    std::memcpy(message.data() + 6, &idLength, sizeof(idLength));
    const int64_t recordingStart = recordingStartedUtcMilliseconds.load();
    const int64_t markerReceived = utc_now_milliseconds();
    std::memcpy(message.data() + 8, &recordingStart, sizeof(recordingStart));
    std::memcpy(message.data() + 16, &markerReceived, sizeof(markerReceived));
    std::memcpy(message.data() + 24, sessionId.data(), sessionId.size());

    DWORD written = 0;
    return WriteFile(pipe, message.data(), static_cast<DWORD>(message.size()), &written, nullptr)
        && written == message.size();
}

void release_active_pipe(HANDLE pipe)
{
    std::lock_guard<std::mutex> lock(pipeWriteMutex);
    if (activePipe.load() == pipe)
        activePipe.store(INVALID_HANDLE_VALUE);
}

void cancel_active_pipe_read()
{
    std::lock_guard<std::mutex> lock(pipeWriteMutex);
    HANDLE pipe = activePipe.load();
    if (pipe != INVALID_HANDLE_VALUE)
        CancelIoEx(pipe, nullptr);
}

void frontend_event(enum obs_frontend_event event, void*)
{
    if (event == OBS_FRONTEND_EVENT_RECORDING_STARTED) {
        recordingStartedUtcMilliseconds.store(utc_now_milliseconds());
        std::lock_guard<std::mutex> lock(sessionMutex);
        write_acknowledgement(mostRecentSessionId);
    } else if (event == OBS_FRONTEND_EVENT_RECORDING_STOPPED) {
        recordingStartedUtcMilliseconds.store(0);
    }
}

void output_frame(uint8_t bus, const std::vector<int16_t>& samples, uint32_t sampleRate)
{
    std::lock_guard<std::mutex> lock(sourcesMutex);
    for (BusSource* target : sources) {
        if (target == nullptr || target->source == nullptr || target->bus.load() != bus)
            continue;

        obs_source_audio audio{};
        audio.data[0] = reinterpret_cast<const uint8_t*>(samples.data());
        audio.frames = static_cast<uint32_t>(samples.size());
        audio.speakers = SPEAKERS_MONO;
        audio.samples_per_sec = sampleRate;
        audio.format = AUDIO_FORMAT_16BIT;
        audio.timestamp = os_gettime_ns();
        obs_source_output_audio(target->source, &audio);
    }
}

void consume_session_marker(HANDLE pipe)
{
    uint8_t markerTail[2]{};
    if (!read_exact(pipe, markerTail, sizeof(markerTail)))
        return;

    const uint16_t idLength = read_u16(markerTail);
    if (idLength > 512)
        return;

    std::string sessionId(idLength, '\0');
    if (idLength != 0 && !read_exact(pipe, sessionId.data(), idLength))
        return;

    {
        std::lock_guard<std::mutex> lock(sessionMutex);
        mostRecentSessionId = sessionId;
    }
    write_acknowledgement(sessionId);
    blog(LOG_INFO, "SimpleVoiceChat OBS: recording session %s received", sessionId.c_str());
}

void run_pipe()
{
    while (running.load()) {
        HANDLE pipe = CreateFileA(PipePath, GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (pipe == INVALID_HANDLE_VALUE) {
            Sleep(250);
            continue;
        }

        activePipe.store(pipe);

        uint8_t header[FrameHeaderSize]{};
        while (running.load() && read_exact(pipe, header, sizeof(header))) {
            if (header[0] != 'S' || header[1] != 'V' || header[2] != 'C' || header[3] != 'B'
                || header[4] != ProtocolVersion) {
                blog(LOG_WARNING, "SimpleVoiceChat OBS: invalid pipe frame; reconnecting");
                break;
            }

            const uint8_t bus = header[5];
            if (bus == SessionMarkerBus) {
                consume_session_marker(pipe);
                continue;
            }

            const uint32_t sampleRate = read_u32(header + 14);
            const uint32_t sampleCount = read_u32(header + 18);
            if (bus != 0 || sampleRate == 0 || sampleCount == 0 || sampleCount > MaximumSamples) {
                blog(LOG_WARNING, "SimpleVoiceChat OBS: rejected invalid audio frame");
                break;
            }

            std::vector<int16_t> samples(sampleCount);
            if (!read_exact(pipe, samples.data(), samples.size() * sizeof(int16_t)))
                break;
            output_frame(bus, samples, sampleRate);
        }
        release_active_pipe(pipe);
        CloseHandle(pipe);
    }
}

const char* source_name(void*)
{
    return "SimpleVoiceChat Player Voice";
}

void* source_create(obs_data_t* settings, obs_source_t* source)
{
    auto* context = new BusSource();
    context->source = source;
    context->bus.store(static_cast<int>(obs_data_get_int(settings, "bus")));
    std::lock_guard<std::mutex> lock(sourcesMutex);
    sources.push_back(context);
    return context;
}

void source_destroy(void* data)
{
    auto* context = static_cast<BusSource*>(data);
    std::lock_guard<std::mutex> lock(sourcesMutex);
    std::erase(sources, context);
    delete context;
}

void source_update(void* data, obs_data_t* settings)
{
    static_cast<BusSource*>(data)->bus.store(static_cast<int>(obs_data_get_int(settings, "bus")));
}

void source_defaults(obs_data_t* settings)
{
    obs_data_set_default_int(settings, "bus", 0);
}

obs_properties_t* source_properties(void*)
{
    obs_properties_t* properties = obs_properties_create();
    obs_property_t* bus = obs_properties_add_list(
        properties, "bus", "Audio bus", OBS_COMBO_TYPE_LIST, OBS_COMBO_FORMAT_INT);
    obs_property_list_add_int(bus, "Player voice", 0);
    return properties;
}

obs_source_info sourceInfo = {
    .id = SourceId,
    .type = OBS_SOURCE_TYPE_INPUT,
    .output_flags = OBS_SOURCE_AUDIO,
    .get_name = source_name,
    .create = source_create,
    .destroy = source_destroy,
    .update = source_update,
    .get_defaults = source_defaults,
    .get_properties = source_properties,
};

} // namespace

bool obs_module_load(void)
{
    obs_register_source(&sourceInfo);
    obs_frontend_add_event_callback(frontend_event, nullptr);
    running.store(true);
    pipeThread = std::thread(run_pipe);
    blog(LOG_INFO, "SimpleVoiceChat OBS: module loaded");
    return true;
}

void obs_module_unload(void)
{
    running.store(false);
    obs_frontend_remove_event_callback(frontend_event, nullptr);
    cancel_active_pipe_read();
    if (pipeThread.joinable())
        pipeThread.join();
}
