#include "pch.h"
#include "Logger.h"
#include <shlobj.h>
#include <filesystem>

std::mutex Logger::s_mutex;
std::wofstream Logger::s_file;
bool Logger::s_initialized = false;

void Logger::Init(const std::wstring& appName)
{
    std::lock_guard<std::mutex> lock(s_mutex);
    if (s_initialized)
        return;

    wchar_t* localAppData = nullptr;
    if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &localAppData)))
    {
        std::filesystem::path logDir = std::filesystem::path(localAppData) / appName / L"logs";
        CoTaskMemFree(localAppData);

        std::error_code ec;
        std::filesystem::create_directories(logDir, ec);

        auto logFile = logDir / (appName + L".log");
        s_file.open(logFile, std::ios::app);
    }

    s_initialized = true;
}

void Logger::Shutdown()
{
    std::lock_guard<std::mutex> lock(s_mutex);
    if (s_file.is_open())
        s_file.close();
    s_initialized = false;
}

void Logger::Write(const wchar_t* level, const std::wstring& msg)
{
    SYSTEMTIME st;
    GetLocalTime(&st);

    wchar_t timestamp[64];
    swprintf_s(timestamp, L"%04d-%02d-%02d %02d:%02d:%02d.%03d",
               st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);

    std::wstring line = std::wstring(timestamp) + L" [" + level + L"] " + msg + L"\n";

    std::lock_guard<std::mutex> lock(s_mutex);
    OutputDebugStringW(line.c_str());
    if (s_file.is_open())
    {
        s_file << line;
        s_file.flush();
    }
}

void Logger::Info(const std::wstring& msg) { Write(L"INFO", msg); }
void Logger::Warn(const std::wstring& msg) { Write(L"WARN", msg); }
void Logger::Error(const std::wstring& msg) { Write(L"ERROR", msg); }
void Logger::Debug(const std::wstring& msg) { Write(L"DEBUG", msg); }
