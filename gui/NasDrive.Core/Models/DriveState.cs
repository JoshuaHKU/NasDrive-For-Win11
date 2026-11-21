namespace NasDrive.Models;

/// <summary>Runtime connection state of a drive.</summary>
public enum DriveState
{
    Disconnected,
    Connecting,
    Connected,
    Offline,
    Disconnecting,
}
