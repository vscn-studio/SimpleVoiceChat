#pragma once

#include <atomic>
#include <cstdint>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

struct SimpleVoiceChatRecordingSession {
    std::string sessionId;
    std::string sessionDirectory;
    int64_t startUtcUnixMilliseconds = 0;
    int64_t obsRecordingStartUtcUnixMilliseconds = 0;
};

class SimpleVoiceChatMultiTrackExporter {
public:
    explicit SimpleVoiceChatMultiTrackExporter(std::atomic<bool>& running);
    ~SimpleVoiceChatMultiTrackExporter();

    void enqueue(const std::string& sourceVideo,
                 const SimpleVoiceChatRecordingSession& session);
    void stop();

private:
    void exportSession(std::string sourceVideo,
                       SimpleVoiceChatRecordingSession session);

    std::atomic<bool>& running;
    std::mutex mutex;
    std::vector<std::thread> workers;
    std::vector<std::string> scheduledKeys;
};
