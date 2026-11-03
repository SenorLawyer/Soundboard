#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <wrl/client.h>
#include <array>
#include <cstdint>
#include <cstdio>
#include <memory>
#include <vector>

using Microsoft::WRL::ComPtr;
namespace {
constexpr uint64_t maxBytes = 128ull * 1024 * 1024;
struct Apartment {
    HRESULT com = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    HRESULT mf = SUCCEEDED(com) ? MFStartup(MF_VERSION) : com;
    ~Apartment() {
        if (SUCCEEDED(mf)) MFShutdown();
        if (SUCCEEDED(com)) CoUninitialize();
    }
};

struct WaveHeader {
    char riff[4] = {'R','I','F','F'};
    uint32_t size = 0;
    char wave[8] = {'W','A','V','E','f','m','t',' '};
    uint32_t fmtSize = 16;
    uint16_t format = 1, channels = 2;
    uint32_t rate = 0, bytesPerSecond = 0;
    uint16_t align = 4, bits = 16;
    char data[4] = {'d','a','t','a'};
    uint32_t bytes = 0;
};
static_assert(sizeof(WaveHeader) == 44);

HRESULT decode(const wchar_t* source, const wchar_t* dest, int* duration) {
    Apartment runtime;
    if (FAILED(runtime.mf)) return runtime.mf;
    ComPtr<IMFByteStream> stream;
    HRESULT hr = MFCreateFile(MF_ACCESSMODE_READ, MF_OPENMODE_FAIL_IF_NOT_EXIST, MF_FILEFLAGS_NONE, source, &stream);
    if (FAILED(hr)) return hr;
    ComPtr<IMFSourceReader> reader;
    hr = MFCreateSourceReaderFromByteStream(stream.Get(), nullptr, &reader);
    if (FAILED(hr)) return hr;
    constexpr DWORD audio = static_cast<DWORD>(MF_SOURCE_READER_FIRST_AUDIO_STREAM);
    hr = reader->SetStreamSelection(static_cast<DWORD>(MF_SOURCE_READER_ALL_STREAMS), FALSE);
    if (FAILED(hr)) return hr;
    hr = reader->SetStreamSelection(audio, TRUE);
    if (FAILED(hr)) return hr;
    ComPtr<IMFMediaType> request;
    if (FAILED(hr = MFCreateMediaType(&request)) ||
        FAILED(hr = request->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio)) ||
        FAILED(hr = request->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_PCM)) ||
        FAILED(hr = request->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16)) ||
        FAILED(hr = reader->SetCurrentMediaType(audio, nullptr, request.Get()))) return hr;
    ComPtr<IMFMediaType> actual;
    if (FAILED(hr = reader->GetCurrentMediaType(audio, &actual))) return hr;
    const UINT32 rate = MFGetAttributeUINT32(actual.Get(), MF_MT_AUDIO_SAMPLES_PER_SECOND, 0);
    const UINT32 channels = MFGetAttributeUINT32(actual.Get(), MF_MT_AUDIO_NUM_CHANNELS, 0);
    if (rate < 8000 || rate > 192000 || (channels != 1 && channels != 2) ||
        MFGetAttributeUINT32(actual.Get(), MF_MT_AUDIO_BITS_PER_SAMPLE, 0) != 16) return HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED);

    std::unique_ptr<FILE, decltype(&fclose)> file(_wfopen(dest, L"wb"), fclose);
    if (!file) return HRESULT_FROM_WIN32(ERROR_OPEN_FAILED);
    WaveHeader header;
    header.rate = rate;
    header.bytesPerSecond = rate * 4;
    if (fwrite(&header, sizeof(header), 1, file.get()) != 1) return STG_E_WRITEFAULT;
    std::vector<int16_t> stereo;
    for (;;) {
        DWORD flags = 0;
        ComPtr<IMFSample> sample;
        hr = reader->ReadSample(audio, 0, nullptr, &flags, nullptr, &sample);
        if (FAILED(hr)) return hr;
        if (flags & (MF_SOURCE_READERF_ERROR | MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED)) return E_FAIL;
        if (sample) {
            ComPtr<IMFMediaBuffer> buffer;
            if (FAILED(hr = sample->ConvertToContiguousBuffer(&buffer))) return hr;
            DWORD length = 0;
            if (FAILED(hr = buffer->GetCurrentLength(&length))) return hr;
            if (length % (channels * 2) != 0) return E_INVALIDARG;
            const size_t frames = length / (channels * 2);
            const uint64_t total = header.bytes + frames * 4ull;
            if (total > maxBytes || total > rate * 4ull * 300) return HRESULT_FROM_WIN32(ERROR_FILE_TOO_LARGE);
            if (channels == 1) stereo.resize(frames * 2);
            BYTE* bytes = nullptr;
            if (FAILED(hr = buffer->Lock(&bytes, nullptr, nullptr))) return hr;
            const auto* pcm = reinterpret_cast<const int16_t*>(bytes);
            if (channels == 1) {
                for (size_t i = 0; i < frames; ++i) stereo[i * 2] = stereo[i * 2 + 1] = pcm[i];
                pcm = stereo.data();
            }
            const auto written = fwrite(pcm, 4, frames, file.get());
            buffer->Unlock();
            if (written != frames) return STG_E_WRITEFAULT;
            header.bytes = static_cast<uint32_t>(total);
        }
        if (flags & MF_SOURCE_READERF_ENDOFSTREAM) break;
    }
    if (!header.bytes) return E_INVALIDARG;
    header.size = header.bytes + 36;
    if (fseek(file.get(), 0, SEEK_SET) || fwrite(&header, sizeof(header), 1, file.get()) != 1 ||
        fflush(file.get()) != 0) return STG_E_WRITEFAULT;
    *duration = static_cast<int>((header.bytes / 4ull * 1000 + rate - 1) / rate);
    return S_OK;
}
}

extern "C" __declspec(dllexport) HRESULT sb_decode(const wchar_t* source, const wchar_t* dest, int* duration) noexcept {
    try { return decode(source, dest, duration); }
    catch (const std::bad_alloc&) { return E_OUTOFMEMORY; }
    catch (...) { return E_FAIL; }
}
