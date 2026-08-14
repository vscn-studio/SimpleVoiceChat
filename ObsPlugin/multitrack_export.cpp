#include "multitrack_export.h"

#include <obs-module.h>

extern "C" {
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libavutil/error.h>
#include <libavutil/mathematics.h>
}

#include <algorithm>
#include <chrono>
#include <cctype>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <limits>
#include <numeric>
#include <regex>
#include <sstream>

namespace {

namespace fs = std::filesystem;
constexpr int64_t ExportWaitMilliseconds = 30 * 60 * 1000;
constexpr int64_t ExportPollMilliseconds = 500;

struct TimeValue {
    int64_t value = 0;
    int64_t scale = 1;
};

struct WavInput {
    fs::path path;
    std::string name;
    AVFormatContext* format = nullptr;
    int streamIndex = -1;
    int outputStreamIndex = -1;
    int64_t offsetMilliseconds = 0;
    TimeValue duration;
};

struct PacketCursor {
    AVFormatContext* format = nullptr;
    int outputStreamIndex = -1;
    int inputStreamIndex = -1;
    int64_t offsetMilliseconds = 0;
    AVPacket* packet = nullptr;
    bool hasPacket = false;
};

struct VideoInfo {
    int width = 1920;
    int height = 1080;
    AVRational frameRate{60, 1};
    TimeValue duration{0, 1};
    int audioSources = 0;
    int audioChannels = 0;
};

std::string pathUtf8(const fs::path& path)
{
#ifdef _WIN32
    const auto value = path.u8string();
    return std::string(value.begin(), value.end());
#else
    return path.string();
#endif
}

std::string ffmpegError(int error)
{
    char buffer[AV_ERROR_MAX_STRING_SIZE]{};
    av_strerror(error, buffer, sizeof(buffer));
    return std::string(buffer);
}

std::string jsonQuote(const std::string& value)
{
    std::ostringstream result;
    result << '"';
    for (unsigned char character : value) {
        switch (character) {
        case '"': result << "\\\""; break;
        case '\\': result << "\\\\"; break;
        case '\n': result << "\\n"; break;
        case '\r': result << "\\r"; break;
        case '\t': result << "\\t"; break;
        default:
            if (character < 0x20) {
                result << "\\u" << std::hex << std::setw(4) << std::setfill('0')
                       << static_cast<unsigned int>(character) << std::dec;
            } else {
                result << character;
            }
            break;
        }
    }
    result << '"';
    return result.str();
}

std::string xmlEscape(const std::string& value)
{
    std::string result;
    result.reserve(value.size());
    for (char character : value) {
        switch (character) {
        case '&': result += "&amp;"; break;
        case '<': result += "&lt;"; break;
        case '>': result += "&gt;"; break;
        case '"': result += "&quot;"; break;
        case '\'': result += "&apos;"; break;
        default: result += character; break;
        }
    }
    return result;
}

std::string fileUri(const fs::path& path)
{
    std::string value = pathUtf8(path);
#ifdef _WIN32
    std::replace(value.begin(), value.end(), '\\', '/');
#endif
    std::ostringstream result;
#ifdef _WIN32
    result << "file:///";
#else
    result << "file://";
#endif
    constexpr char hex[] = "0123456789ABCDEF";
    for (unsigned char character : value) {
        const bool safe = std::isalnum(character) || character == '-' || character == '_'
            || character == '.' || character == '/' || character == ':';
        if (safe) {
            result << static_cast<char>(character);
        } else {
            result << '%' << hex[character >> 4] << hex[character & 0x0F];
        }
    }
    return result.str();
}

int64_t gcd64(int64_t left, int64_t right)
{
    left = left < 0 ? -left : left;
    right = right < 0 ? -right : right;
    return std::gcd(left, right);
}

std::string rationalTime(int64_t value, int64_t scale)
{
    if (scale <= 0)
        scale = 1;
    const int64_t divisor = gcd64(value, scale);
    return std::to_string(value / divisor) + "/" + std::to_string(scale / divisor) + "s";
}

std::string optionalDuration(const TimeValue& duration)
{
    if (duration.value <= 0)
        return "0/1s";
    return rationalTime(duration.value, duration.scale);
}

TimeValue streamDuration(const AVFormatContext* format, const AVStream* stream)
{
    if (stream != nullptr && stream->duration != AV_NOPTS_VALUE && stream->duration >= 0) {
        const int64_t numerator = stream->time_base.num > 0 ? stream->time_base.num : 1;
        return {stream->duration * numerator, stream->time_base.den > 0 ? stream->time_base.den : 1};
    }
    if (format != nullptr && format->duration != AV_NOPTS_VALUE && format->duration >= 0)
        return {format->duration, AV_TIME_BASE};
    return {};
}

bool openInput(const fs::path& path, AVFormatContext** result, std::string& error)
{
    const std::string filename = pathUtf8(path);
    int status = avformat_open_input(result, filename.c_str(), nullptr, nullptr);
    if (status < 0) {
        error = "Cannot open " + filename + ": " + ffmpegError(status);
        return false;
    }
    status = avformat_find_stream_info(*result, nullptr);
    if (status < 0) {
        error = "Cannot inspect " + filename + ": " + ffmpegError(status);
        avformat_close_input(result);
        return false;
    }
    return true;
}

std::vector<fs::path> listWavs(const fs::path& sessionDirectory)
{
    std::vector<fs::path> result;
    std::error_code error;
    for (const fs::directory_entry& entry : fs::directory_iterator(sessionDirectory, error)) {
        if (error || !entry.is_regular_file(error))
            continue;
        std::string extension = entry.path().extension().string();
        std::transform(extension.begin(), extension.end(), extension.begin(),
                       [](unsigned char value) { return static_cast<char>(std::tolower(value)); });
        if (extension == ".wav")
            result.push_back(entry.path());
    }
    std::sort(result.begin(), result.end());
    return result;
}

bool readAlignment(const fs::path& sessionDirectory, int64_t& offset, std::string& error)
{
    const fs::path path = sessionDirectory / "obs-sync.json";
    std::ifstream input(path, std::ios::binary);
    if (!input) {
        error = "obs-sync.json is not available yet";
        return false;
    }
    const std::string json((std::istreambuf_iterator<char>(input)), std::istreambuf_iterator<char>());
    std::smatch match;
    const std::regex pattern(R"("wavZeroMinusObsStartMilliseconds"\s*:\s*(-?[0-9]+))");
    if (!std::regex_search(json, match, pattern)) {
        error = "obs-sync.json does not contain wavZeroMinusObsStartMilliseconds";
        return false;
    }
    try {
        offset = std::stoll(match[1].str());
    } catch (...) {
        error = "Invalid wavZeroMinusObsStartMilliseconds in obs-sync.json";
        return false;
    }
    return true;
}

void writeExportStatus(const SimpleVoiceChatRecordingSession& session,
                       const std::string& status,
                       const std::string& sourceVideo,
                       const std::string& mkv,
                       const std::string& fcpxml,
                       const std::string& error,
                       const int64_t* offset)
{
    std::error_code ignored;
    fs::create_directories(session.sessionDirectory, ignored);
    std::ofstream output(fs::path(session.sessionDirectory) / "obs-export.json", std::ios::binary | std::ios::trunc);
    if (!output)
        return;
    output << "{\n"
           << "  \"status\": " << jsonQuote(status) << ",\n"
           << "  \"sessionId\": " << jsonQuote(session.sessionId) << ",\n"
           << "  \"sourceVideo\": " << jsonQuote(sourceVideo) << ",\n"
           << "  \"multitrackMkv\": " << jsonQuote(mkv) << ",\n"
           << "  \"fcpxml\": " << jsonQuote(fcpxml) << ",\n"
           << "  \"wavZeroMinusObsStartMilliseconds\": ";
    if (offset != nullptr)
        output << *offset;
    else
        output << "null";
    output << ",\n  \"error\": " << jsonQuote(error) << "\n}\n";
}

bool loadVideoInfo(AVFormatContext* format, VideoInfo& info)
{
    int videoIndex = -1;
    for (unsigned int index = 0; index < format->nb_streams; ++index) {
        AVStream* stream = format->streams[index];
        if (stream->codecpar->codec_type == AVMEDIA_TYPE_VIDEO) {
            videoIndex = static_cast<int>(index);
            info.width = stream->codecpar->width > 0 ? stream->codecpar->width : info.width;
            info.height = stream->codecpar->height > 0 ? stream->codecpar->height : info.height;
            if (stream->avg_frame_rate.num > 0 && stream->avg_frame_rate.den > 0)
                info.frameRate = stream->avg_frame_rate;
            else if (stream->r_frame_rate.num > 0 && stream->r_frame_rate.den > 0)
                info.frameRate = stream->r_frame_rate;
            info.duration = streamDuration(format, stream);
            break;
        }
    }
    for (unsigned int index = 0; index < format->nb_streams; ++index) {
        const AVCodecParameters* parameters = format->streams[index]->codecpar;
        if (parameters->codec_type == AVMEDIA_TYPE_AUDIO) {
            ++info.audioSources;
            info.audioChannels += parameters->ch_layout.nb_channels > 0
                ? parameters->ch_layout.nb_channels : 1;
        }
    }
    if (videoIndex < 0)
        return false;
    if (info.duration.value <= 0 && format->duration != AV_NOPTS_VALUE)
        info.duration = {format->duration, AV_TIME_BASE};
    return true;
}

bool replacePacketDataForNegativeTimestamp(AVPacket* packet,
                                           AVStream* inputStream,
                                           int64_t shiftedPts,
                                           std::string& error)
{
    if (shiftedPts >= 0 || packet->size <= 0)
        return true;
    const AVCodecParameters* parameters = inputStream->codecpar;
    if (parameters->codec_type != AVMEDIA_TYPE_AUDIO || parameters->block_align <= 0
        || parameters->sample_rate <= 0)
        return true;

    const int64_t samplesToDrop = av_rescale_q(-shiftedPts,
                                                AV_TIME_BASE_Q,
                                                AVRational{1, parameters->sample_rate});
    const int64_t bytesToDrop = samplesToDrop * parameters->block_align;
    if (bytesToDrop >= packet->size) {
        packet->size = 0;
        return true;
    }
    if (bytesToDrop <= 0)
        return true;

    AVPacket* replacement = av_packet_alloc();
    if (replacement == nullptr) {
        error = "Cannot allocate a packet while trimming pre-roll audio";
        return false;
    }
    const int remaining = packet->size - static_cast<int>(bytesToDrop);
    if (av_new_packet(replacement, remaining) < 0) {
        av_packet_free(&replacement);
        error = "Cannot allocate trimmed pre-roll audio";
        return false;
    }
    std::memcpy(replacement->data, packet->data + bytesToDrop, remaining);
    replacement->pts = packet->pts;
    replacement->dts = packet->dts;
    replacement->duration = packet->duration - samplesToDrop;
    if (replacement->pts != AV_NOPTS_VALUE)
        replacement->pts = 0;
    if (replacement->dts != AV_NOPTS_VALUE)
        replacement->dts = 0;
    av_packet_unref(packet);
    av_packet_move_ref(packet, replacement);
    av_packet_free(&replacement);
    return true;
}

bool muxMkv(const fs::path& sourcePath,
            const fs::path& outputPath,
            const std::vector<fs::path>& wavPaths,
            int64_t offsetMilliseconds,
            std::string& error)
{
    AVFormatContext* source = nullptr;
    if (!openInput(sourcePath, &source, error))
        return false;

    std::vector<WavInput> wavs;
    for (const fs::path& path : wavPaths) {
        WavInput wav;
        wav.path = path;
        wav.name = path.stem().string();
        wav.offsetMilliseconds = offsetMilliseconds;
        if (!openInput(path, &wav.format, error))
            goto fail;
        for (unsigned int index = 0; index < wav.format->nb_streams; ++index) {
            if (wav.format->streams[index]->codecpar->codec_type == AVMEDIA_TYPE_AUDIO) {
                wav.streamIndex = static_cast<int>(index);
                break;
            }
        }
        if (wav.streamIndex < 0) {
            error = "No audio stream found in " + pathUtf8(path);
            goto fail;
        }
        wav.duration = streamDuration(wav.format, wav.format->streams[wav.streamIndex]);
        wavs.push_back(std::move(wav));
    }

    {
        AVFormatContext* output = nullptr;
        int status = avformat_alloc_output_context2(&output, nullptr, "matroska", pathUtf8(outputPath).c_str());
        if (status < 0 || output == nullptr) {
            error = "Cannot create Matroska output: " + ffmpegError(status);
            goto fail;
        }

        std::vector<int> sourceMap(source->nb_streams, -1);
        for (unsigned int index = 0; index < source->nb_streams; ++index) {
            AVStream* stream = avformat_new_stream(output, nullptr);
            if (stream == nullptr) {
                error = "Cannot allocate an OBS output stream";
                avformat_free_context(output);
                goto fail;
            }
            if (avcodec_parameters_copy(stream->codecpar, source->streams[index]->codecpar) < 0) {
                error = "Cannot copy an OBS stream description";
                avformat_free_context(output);
                goto fail;
            }
            stream->codecpar->codec_tag = 0;
            stream->time_base = source->streams[index]->time_base;
            av_dict_copy(&stream->metadata, source->streams[index]->metadata, 0);
            sourceMap[index] = stream->index;
        }
        for (WavInput& wav : wavs) {
            AVStream* stream = avformat_new_stream(output, nullptr);
            if (stream == nullptr) {
                error = "Cannot allocate a player voice output stream";
                avformat_free_context(output);
                goto fail;
            }
            AVStream* inputStream = wav.format->streams[wav.streamIndex];
            if (avcodec_parameters_copy(stream->codecpar, inputStream->codecpar) < 0) {
                error = "Cannot copy a player voice stream description";
                avformat_free_context(output);
                goto fail;
            }
            stream->codecpar->codec_tag = 0;
            stream->time_base = inputStream->time_base;
            av_dict_set(&stream->metadata, "title", wav.name.c_str(), 0);
            wav.outputStreamIndex = stream->index;
        }
        if (!(output->oformat->flags & AVFMT_NOFILE)) {
            status = avio_open(&output->pb, pathUtf8(outputPath).c_str(), AVIO_FLAG_WRITE);
            if (status < 0) {
                error = "Cannot open Matroska output: " + ffmpegError(status);
                avformat_free_context(output);
                goto fail;
            }
        }
        status = avformat_write_header(output, nullptr);
        if (status < 0) {
            error = "Cannot write Matroska header: " + ffmpegError(status);
            if (!(output->oformat->flags & AVFMT_NOFILE))
                avio_closep(&output->pb);
            avformat_free_context(output);
            goto fail;
        }

        std::vector<PacketCursor> cursors;
        cursors.push_back({source, -1, -1, 0, av_packet_alloc(), false});
        for (const WavInput& wav : wavs)
            cursors.push_back({wav.format, wav.outputStreamIndex, wav.streamIndex,
                               wav.offsetMilliseconds, av_packet_alloc(), false});

        bool failed = false;
        while (!failed) {
            int selected = -1;
            int64_t selectedTime = std::numeric_limits<int64_t>::max();
            for (size_t cursorIndex = 0; cursorIndex < cursors.size(); ++cursorIndex) {
                PacketCursor& cursor = cursors[cursorIndex];
                if (cursor.packet == nullptr)
                    continue;
                if (!cursor.hasPacket) {
                    const int readStatus = av_read_frame(cursor.format, cursor.packet);
                    if (readStatus < 0)
                        continue;
                    cursor.hasPacket = true;
                }
                const AVStream* inputStream = cursor.format->streams[cursor.packet->stream_index];
                const int64_t timestamp = cursor.packet->dts != AV_NOPTS_VALUE
                    ? cursor.packet->dts : cursor.packet->pts;
                int64_t time = timestamp == AV_NOPTS_VALUE
                    ? 0 : av_rescale_q(timestamp, inputStream->time_base, AV_TIME_BASE_Q);
                if (cursorIndex != 0)
                    time += cursor.offsetMilliseconds * 1000;
                if (selected < 0 || time < selectedTime) {
                    selected = static_cast<int>(cursorIndex);
                    selectedTime = time;
                }
            }
            if (selected < 0)
                break;

            PacketCursor& cursor = cursors[static_cast<size_t>(selected)];
            AVStream* inputStream = cursor.format->streams[cursor.packet->stream_index];
            AVPacket* packet = cursor.packet;
            if (selected != 0) {
                if (packet->pts != AV_NOPTS_VALUE)
                    packet->pts += av_rescale_q(cursor.offsetMilliseconds * 1000, AV_TIME_BASE_Q,
                                                inputStream->time_base);
                if (packet->dts != AV_NOPTS_VALUE)
                    packet->dts += av_rescale_q(cursor.offsetMilliseconds * 1000, AV_TIME_BASE_Q,
                                                inputStream->time_base);
                if (!replacePacketDataForNegativeTimestamp(packet, inputStream,
                                                            packet->pts == AV_NOPTS_VALUE ? 0
                                                                                          : selectedTime,
                                                            error)) {
                    failed = true;
                }
                if (packet->size == 0) {
                    av_packet_unref(packet);
                    cursor.hasPacket = false;
                    continue;
                }
            }
            if (!failed) {
                const int destinationStream = selected == 0
                    ? sourceMap[packet->stream_index] : cursor.outputStreamIndex;
                packet->stream_index = destinationStream;
                av_packet_rescale_ts(packet, inputStream->time_base, output->streams[destinationStream]->time_base);
                status = av_interleaved_write_frame(output, packet);
                if (status < 0) {
                    error = "Cannot mux an OBS or player packet: " + ffmpegError(status);
                    failed = true;
                }
            }
            av_packet_unref(packet);
            cursor.hasPacket = false;
        }
        for (PacketCursor& cursor : cursors)
            av_packet_free(&cursor.packet);
        if (!failed) {
            status = av_write_trailer(output);
            if (status < 0) {
                error = "Cannot finalize Matroska output: " + ffmpegError(status);
                failed = true;
            }
        }
        if (!(output->oformat->flags & AVFMT_NOFILE))
            avio_closep(&output->pb);
        avformat_free_context(output);
        if (failed)
            goto fail;
    }

    avformat_close_input(&source);
    for (WavInput& wav : wavs)
        avformat_close_input(&wav.format);
    return true;

fail:
    avformat_close_input(&source);
    for (WavInput& wav : wavs)
        avformat_close_input(&wav.format);
    return false;
}

bool writeFcpxml(const fs::path& sourcePath,
                 const fs::path& outputPath,
                 const std::vector<fs::path>& wavPaths,
                 int64_t offsetMilliseconds,
                 std::string& error)
{
    AVFormatContext* source = nullptr;
    if (!openInput(sourcePath, &source, error))
        return false;
    VideoInfo video;
    if (!loadVideoInfo(source, video)) {
        error = "The OBS recording contains no video stream";
        avformat_close_input(&source);
        return false;
    }
    std::vector<WavInput> wavs;
    for (const fs::path& path : wavPaths) {
        WavInput wav;
        wav.path = path;
        wav.name = path.stem().string();
        if (!openInput(path, &wav.format, error))
            goto fail;
        for (unsigned int index = 0; index < wav.format->nb_streams; ++index) {
            if (wav.format->streams[index]->codecpar->codec_type == AVMEDIA_TYPE_AUDIO) {
                wav.streamIndex = static_cast<int>(index);
                break;
            }
        }
        if (wav.streamIndex < 0) {
            error = "No audio stream found in " + pathUtf8(path);
            goto fail;
        }
        wav.duration = streamDuration(wav.format, wav.format->streams[wav.streamIndex]);
        wavs.push_back(std::move(wav));
    }

    {
        const std::string formatId = "r1";
        const AVRational frameRate = video.frameRate.num > 0 && video.frameRate.den > 0
            ? video.frameRate : AVRational{60, 1};
        std::ofstream output(outputPath, std::ios::binary | std::ios::trunc);
        if (!output) {
            error = "Cannot write FCPXML: " + pathUtf8(outputPath);
            goto fail;
        }
        output << "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE fcpxml>\n"
               << "<fcpxml version=\"1.10\">\n  <resources>\n"
               << "    <format id=\"" << formatId << "\" name=\"FFVideoFormat"
               << video.width << "x" << video.height << "p\" frameDuration=\""
               << rationalTime(frameRate.den, frameRate.num) << "\" width=\""
               << video.width << "\" height=\"" << video.height << "\"/>\n"
               << "    <asset id=\"r2\" name=\"OBS recording\" src=\""
               << xmlEscape(fileUri(sourcePath)) << "\" start=\"0s\" duration=\""
               << optionalDuration(video.duration) << "\" hasVideo=\"1\" hasAudio=\""
               << (video.audioSources > 0 ? "1" : "0") << "\" format=\"" << formatId
               << "\" audioSources=\"" << video.audioSources << "\" audioChannels=\""
               << video.audioChannels << "\"/>\n";
        for (size_t index = 0; index < wavs.size(); ++index) {
            const WavInput& wav = wavs[index];
            const AVCodecParameters* parameters = wav.format->streams[wav.streamIndex]->codecpar;
            output << "    <asset id=\"r" << index + 3 << "\" name=\""
                   << xmlEscape(wav.name) << "\" src=\"" << xmlEscape(fileUri(wav.path))
                   << "\" start=\"0s\" duration=\"" << optionalDuration(wav.duration)
                   << "\" hasVideo=\"0\" hasAudio=\"1\" audioSources=\"1\" audioChannels=\""
                   << (parameters->ch_layout.nb_channels > 0 ? parameters->ch_layout.nb_channels : 1)
                   << "\" audioRate=\"" << parameters->sample_rate << "\"/>\n";
        }
        TimeValue timelineDuration = video.duration;
        output << "  </resources>\n  <library>\n    <event name=\"SimpleVoiceChat\">\n"
               << "      <project name=\"" << xmlEscape(sourcePath.stem().string())
               << "\">\n        <sequence format=\"" << formatId << "\" duration=\""
               << optionalDuration(timelineDuration) << "\" tcStart=\"0s\" tcFormat=\"NDF\">\n"
               << "          <spine>\n            <asset-clip ref=\"r2\" name=\"OBS recording\" offset=\"0s\" start=\"0s\" duration=\""
               << optionalDuration(video.duration) << "\" format=\"" << formatId << "\">\n";
        for (size_t index = 0; index < wavs.size(); ++index) {
            const WavInput& wav = wavs[index];
            const AVStream* stream = wav.format->streams[wav.streamIndex];
            int64_t trimValue = 0;
            if (offsetMilliseconds < 0)
                trimValue = av_rescale_q(-offsetMilliseconds, AVRational{1, 1000}, stream->time_base);
            TimeValue clipDuration = wav.duration;
            if (trimValue > 0 && clipDuration.value > 0) {
                const int64_t trimDuration = trimValue * (stream->time_base.num > 0 ? stream->time_base.num : 1);
                clipDuration.value = std::max<int64_t>(0, clipDuration.value - trimDuration);
            }
            if (clipDuration.value <= 0)
                continue;
            output << "              <asset-clip ref=\"r" << index + 3 << "\" name=\""
                   << xmlEscape(wav.name) << "\" offset=\""
                   << (offsetMilliseconds > 0 ? rationalTime(offsetMilliseconds, 1000) : "0s")
                   << "\" start=\"" << (trimValue > 0
                       ? rationalTime(trimValue * (stream->time_base.num > 0 ? stream->time_base.num : 1),
                                      stream->time_base.den)
                       : "0s")
                   << "\" duration=\"" << optionalDuration(clipDuration)
                   << "\" lane=\"-" << index + 1 << "\"/>\n";
        }
        output << "            </asset-clip>\n          </spine>\n        </sequence>\n"
               << "      </project>\n    </event>\n  </library>\n</fcpxml>\n";
        output.flush();
        if (!output)
            error = "FCPXML write failed";
    }

    avformat_close_input(&source);
    for (WavInput& wav : wavs)
        avformat_close_input(&wav.format);
    return error.empty();

fail:
    avformat_close_input(&source);
    for (WavInput& wav : wavs)
        avformat_close_input(&wav.format);
    return false;
}

} // namespace

SimpleVoiceChatMultiTrackExporter::SimpleVoiceChatMultiTrackExporter(std::atomic<bool>& running)
    : running(running)
{
}

SimpleVoiceChatMultiTrackExporter::~SimpleVoiceChatMultiTrackExporter()
{
    stop();
}

void SimpleVoiceChatMultiTrackExporter::enqueue(const std::string& sourceVideo,
                                                const SimpleVoiceChatRecordingSession& session)
{
    if (sourceVideo.empty() || session.sessionDirectory.empty() || session.sessionId.empty())
        return;
    const std::string key = sourceVideo + "\n" + session.sessionId;
    std::lock_guard<std::mutex> lock(mutex);
    if (std::find(scheduledKeys.begin(), scheduledKeys.end(), key) != scheduledKeys.end())
        return;
    scheduledKeys.push_back(key);
    workers.emplace_back([this, sourceVideo, session]() { exportSession(sourceVideo, session); });
}

void SimpleVoiceChatMultiTrackExporter::stop()
{
    std::vector<std::thread> pending;
    {
        std::lock_guard<std::mutex> lock(mutex);
        pending.swap(workers);
    }
    for (std::thread& worker : pending) {
        if (worker.joinable())
            worker.join();
    }
}

void SimpleVoiceChatMultiTrackExporter::exportSession(std::string sourceVideo,
                                                       SimpleVoiceChatRecordingSession session)
{
    const fs::path sourcePath = fs::path(sourceVideo);
    const fs::path sessionDirectory = fs::path(session.sessionDirectory);
    writeExportStatus(session, "waiting", sourceVideo, "", "", "", nullptr);
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(ExportWaitMilliseconds);
    std::vector<fs::path> wavs;
    while (running.load() && std::chrono::steady_clock::now() < deadline) {
        if (fs::exists(sourcePath) && fs::exists(sessionDirectory / "session.json")
            && fs::exists(sessionDirectory / "obs-sync.json")) {
            wavs = listWavs(sessionDirectory);
            if (!wavs.empty())
                break;
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(ExportPollMilliseconds));
    }
    if (!running.load())
        return;
    if (wavs.empty()) {
        const std::string error = "Timed out waiting for finalized session.json, obs-sync.json, and WAV files";
        writeExportStatus(session, "failed", sourceVideo, "", "", error, nullptr);
        blog(LOG_WARNING, "SimpleVoiceChat OBS: %s (%s)", error.c_str(), session.sessionId.c_str());
        return;
    }

    int64_t offset = 0;
    std::string error;
    if (!readAlignment(sessionDirectory, offset, error)) {
        writeExportStatus(session, "failed", sourceVideo, "", "", error, nullptr);
        blog(LOG_WARNING, "SimpleVoiceChat OBS: export %s failed: %s", session.sessionId.c_str(), error.c_str());
        return;
    }
    const std::string safeId = [&session]() {
        std::string value = session.sessionId;
        for (char& character : value) {
            if (!std::isalnum(static_cast<unsigned char>(character)) && character != '-' && character != '_')
                character = '_';
        }
        return value;
    }();
    fs::path base = sourcePath.parent_path() / (sourcePath.stem().string() + "-" + safeId + "-multitrack");
    fs::path mkvPath = base;
    mkvPath += ".mkv";
    fs::path fcpxmlPath = base;
    fcpxmlPath += ".fcpxml";
    int suffix = 2;
    while (fs::exists(mkvPath) || fs::exists(fcpxmlPath)) {
        mkvPath = sourcePath.parent_path() / (base.filename().string() + "-" + std::to_string(suffix) + ".mkv");
        fcpxmlPath = sourcePath.parent_path() / (base.filename().string() + "-" + std::to_string(suffix) + ".fcpxml");
        ++suffix;
    }
    writeExportStatus(session, "exporting", sourceVideo, pathUtf8(mkvPath), pathUtf8(fcpxmlPath), "", &offset);
    if (!muxMkv(sourcePath, mkvPath, wavs, offset, error)
        || !writeFcpxml(sourcePath, fcpxmlPath, wavs, offset, error)) {
        writeExportStatus(session, "failed", sourceVideo, pathUtf8(mkvPath), pathUtf8(fcpxmlPath), error, &offset);
        blog(LOG_WARNING, "SimpleVoiceChat OBS: export %s failed: %s", session.sessionId.c_str(), error.c_str());
        return;
    }
    writeExportStatus(session, "completed", sourceVideo, pathUtf8(mkvPath), pathUtf8(fcpxmlPath), "", &offset);
    blog(LOG_INFO, "SimpleVoiceChat OBS: exported %s to %s and %s", session.sessionId.c_str(),
         pathUtf8(mkvPath).c_str(), pathUtf8(fcpxmlPath).c_str());
}
