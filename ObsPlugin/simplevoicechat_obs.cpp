#include <obs-module.h>
#include <obs-frontend-api.h>
#include <util/platform.h>

#ifdef _WIN32
#include <Windows.h>
#else
#include <sys/socket.h>
#include <sys/un.h>
#include <unistd.h>
#endif

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

OBS_DECLARE_MODULE()

namespace {

constexpr char SourceId[] = "simplevoicechat_audio_bus";
#ifdef _WIN32
constexpr char PipePath[] = "\\\\.\\pipe\\simplevoicechat-audiobuses";
#else
constexpr char UnixSocketFileName[] = "simplevoicechat-audiobuses.sock";
#endif
constexpr uint8_t ProtocolVersion = 1;
constexpr uint8_t SessionMarkerBus = 0x7f;
constexpr uint8_t SessionAcknowledgementMessage = 1;
constexpr size_t FrameHeaderSize = 22;
constexpr size_t MaximumSamples = 4096;

#ifdef _WIN32
using TransportHandle = HANDLE;
const TransportHandle InvalidTransport = INVALID_HANDLE_VALUE;
#else
using TransportHandle = int;
constexpr TransportHandle InvalidTransport = -1;
#endif

struct BusSource {
    obs_source_t* source = nullptr;
    std::atomic<int> bus = 0;
};

std::atomic<bool> running = false;
std::thread pipeThread;
std::mutex sourcesMutex;
std::mutex pipeWriteMutex;
std::vector<BusSource*> sources;
std::atomic<TransportHandle> activeTransport = InvalidTransport;
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

bool read_exact(TransportHandle transport, void* destination, size_t count)
{
    auto* bytes = static_cast<uint8_t*>(destination);
    while (count > 0 && running.load()) {
#ifdef _WIN32
        DWORD available = 0;
        if (!PeekNamedPipe(transport, nullptr, 0, nullptr, &available, nullptr))
            return false;
        if (available == 0) {
            Sleep(10);
            continue;
        }
        DWORD read = 0;
        const DWORD wanted = static_cast<DWORD>(std::min<size_t>({ count, available, MAXDWORD }));
        if (!ReadFile(transport, bytes, wanted, &read, nullptr) || read == 0)
            return false;
#else
        const ssize_t read = recv(transport, bytes, count, 0);
        if (read <= 0)
            return false;
#endif
        bytes += read;
        count -= read;
    }
    return count == 0;
}

bool write_exact(TransportHandle transport, const void* source, size_t count)
{
    const auto* bytes = static_cast<const uint8_t*>(source);
    while (count > 0) {
#ifdef _WIN32
        DWORD written = 0;
        const DWORD wanted = static_cast<DWORD>(std::min<size_t>({ count, static_cast<size_t>(MAXDWORD) }));
        if (!WriteFile(transport, bytes, wanted, &written, nullptr) || written == 0)
            return false;
#else
        int sendFlags = 0;
#if defined(MSG_NOSIGNAL)
        sendFlags |= MSG_NOSIGNAL;
#endif
        const ssize_t written = send(transport, bytes, count, sendFlags);
        if (written <= 0)
            return false;
#endif
        bytes += written;
        count -= written;
    }
    return true;
}

int64_t utc_now_milliseconds()
{
#ifdef _WIN32
    FILETIME fileTime{};
    GetSystemTimeAsFileTime(&fileTime);
    ULARGE_INTEGER value{};
    value.LowPart = fileTime.dwLowDateTime;
    value.HighPart = fileTime.dwHighDateTime;
    return static_cast<int64_t>((value.QuadPart - 116444736000000000ULL) / 10000ULL);
#else
    using namespace std::chrono;
    return duration_cast<milliseconds>(system_clock::now().time_since_epoch()).count();
#endif
}

bool write_acknowledgement(const std::string& sessionId)
{
    std::lock_guard<std::mutex> lock(pipeWriteMutex);
    TransportHandle transport = activeTransport.load();
    if (transport == InvalidTransport || sessionId.empty() || sessionId.size() > 512)
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

    return write_exact(transport, message.data(), message.size());
}

void release_active_transport(TransportHandle transport)
{
    std::lock_guard<std::mutex> lock(pipeWriteMutex);
    if (activeTransport.load() == transport)
        activeTransport.store(InvalidTransport);
}

void cancel_active_transport_read()
{
    std::lock_guard<std::mutex> lock(pipeWriteMutex);
    TransportHandle transport = activeTransport.load();
    if (transport != InvalidTransport) {
#ifdef _WIN32
        CancelIoEx(transport, nullptr);
#else
        shutdown(transport, SHUT_RDWR);
#endif
    }
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

void consume_session_marker(TransportHandle transport)
{
    uint8_t markerTail[2]{};
    if (!read_exact(transport, markerTail, sizeof(markerTail)))
        return;

    const uint16_t idLength = read_u16(markerTail);
    if (idLength > 512)
        return;

    std::string sessionId(idLength, '\0');
    if (idLength != 0 && !read_exact(transport, sessionId.data(), idLength))
        return;

    {
        std::lock_guard<std::mutex> lock(sessionMutex);
        mostRecentSessionId = sessionId;
    }
    write_acknowledgement(sessionId);
    blog(LOG_INFO, "SimpleVoiceChat OBS: recording session %s received", sessionId.c_str());
}

void consume_transport(TransportHandle transport)
{
    uint8_t header[FrameHeaderSize]{};
    while (running.load() && read_exact(transport, header, sizeof(header))) {
        if (header[0] != 'S' || header[1] != 'V' || header[2] != 'C' || header[3] != 'B'
            || header[4] != ProtocolVersion) {
            blog(LOG_WARNING, "SimpleVoiceChat OBS: invalid IPC frame; reconnecting");
            break;
        }

        const uint8_t bus = header[5];
        if (bus == SessionMarkerBus) {
            consume_session_marker(transport);
            continue;
        }

        const uint32_t sampleRate = read_u32(header + 14);
        const uint32_t sampleCount = read_u32(header + 18);
        if (bus != 0 || sampleRate == 0 || sampleCount == 0 || sampleCount > MaximumSamples) {
            blog(LOG_WARNING, "SimpleVoiceChat OBS: rejected invalid audio frame");
            break;
        }

        std::vector<int16_t> samples(sampleCount);
        if (!read_exact(transport, samples.data(), samples.size() * sizeof(int16_t)))
            break;
        output_frame(bus, samples, sampleRate);
    }
}

#ifdef _WIN32
void run_pipe()
{
    while (running.load()) {
        HANDLE pipe = CreateFileA(PipePath, GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (pipe == InvalidTransport) {
            Sleep(250);
            continue;
        }

        activeTransport.store(pipe);
        consume_transport(pipe);
        release_active_transport(pipe);
        CloseHandle(pipe);
    }
}
#else
std::string unix_socket_path()
{
    const char* runtimeDirectory = std::getenv("XDG_RUNTIME_DIR");
    if (runtimeDirectory != nullptr && runtimeDirectory[0] == '/') {
        const std::string path = std::string(runtimeDirectory) + "/" + UnixSocketFileName;
        if (path.size() <= 96)
            return path;
    }

    const char* temporaryDirectory = std::getenv("TMPDIR");
    if (temporaryDirectory != nullptr && temporaryDirectory[0] == '/') {
        const std::string path = std::string(temporaryDirectory) + "/" + UnixSocketFileName;
        if (path.size() <= 96)
            return path;
    }

    return std::string("/tmp/") + UnixSocketFileName;
}

void run_pipe()
{
    const std::string path = unix_socket_path();
    sockaddr_un address{};
    if (path.size() >= sizeof(address.sun_path)) {
        blog(LOG_ERROR, "SimpleVoiceChat OBS: Unix socket path is too long");
        return;
    }

    while (running.load()) {
        const int socketFd = socket(AF_UNIX, SOCK_STREAM, 0);
        if (socketFd < 0) {
            std::this_thread::sleep_for(std::chrono::milliseconds(250));
            continue;
        }

#if defined(__APPLE__)
        const int noSigPipe = 1;
        setsockopt(socketFd, SOL_SOCKET, SO_NOSIGPIPE, &noSigPipe, sizeof(noSigPipe));
#endif

        address = {};
        address.sun_family = AF_UNIX;
        std::memcpy(address.sun_path, path.c_str(), path.size() + 1);
        const socklen_t addressLength = static_cast<socklen_t>(offsetof(sockaddr_un, sun_path) + path.size() + 1);
        if (connect(socketFd, reinterpret_cast<const sockaddr*>(&address), addressLength) != 0) {
            close(socketFd);
            std::this_thread::sleep_for(std::chrono::milliseconds(250));
            continue;
        }

        activeTransport.store(socketFd);
        consume_transport(socketFd);
        release_active_transport(socketFd);
        close(socketFd);
    }
}
#endif

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
    cancel_active_transport_read();
    if (pipeThread.joinable())
        pipeThread.join();
}
