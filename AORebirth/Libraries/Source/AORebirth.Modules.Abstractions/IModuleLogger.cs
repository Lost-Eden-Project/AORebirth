namespace AORebirth.Modules
{
    using System;

    /// <summary>Writes to the zone log, prefixed with the module id.</summary>
    public interface IModuleLogger
    {
        void Debug(string message);

        void Info(string message);

        void Warn(string message);

        void Error(string message);

        void Error(Exception exception, string message);
    }
}
