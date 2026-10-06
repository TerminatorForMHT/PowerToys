#pragma once

#include <windows.h>
#include <string>
#include <mutex>
#include <fstream>

// Minimal logger: writes to debugger output and a rolling log file under
// %LocalAppData%\LightSwitch\logs\.
class Logger
{
public:
    static void Init(const std::wstring& appName);
    static void Shutdown();

    static void Info(const std::wstring& msg);
    static void Warn(const std::wstring& msg);
    static void Error(const std::wstring& msg);
    static void Debug(const std::wstring& msg);

private:
    static void Write(const wchar_t* level, const std::wstring& msg);

    static std::mutex s_mutex;
    static std::wofstream s_file;
    static bool s_initialized;
};
