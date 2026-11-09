namespace AORebirth.ChatModules
{
    using System;

    /// <summary>Writes to the chat engine log, prefixed with the module id.</summary>
    public interface IChatModuleLogger
    {
        void Debug(string message);

        void Info(string message);

        void Warn(string message);

        void Error(string message);

        void Error(Exception exception, string message);
    }
}
