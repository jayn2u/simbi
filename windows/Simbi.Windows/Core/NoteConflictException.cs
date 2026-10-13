namespace Simbi.Windows.Core;

public sealed class NoteConflictException(string fileName, string recoveryPath, string message) : IOException(message)
{
    public string FileName { get; } = fileName;
    public string RecoveryPath { get; } = recoveryPath;
}
