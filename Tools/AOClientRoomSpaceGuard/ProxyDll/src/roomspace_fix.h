#pragma once

namespace aorf
{
    enum class ClientProfile
    {
        Unknown,
        NewClient,
        OldClient
    };

    ClientProfile GetLoadedN3ClientProfile();
    bool VerifyLoadedModuleFileHash(void* module, const char* expected);
    bool InstallClientCrashMitigation();
    bool RunClientCrashMitigationSelfTest();
}
